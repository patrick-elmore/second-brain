using System.Text;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using SecondBrain.Llm;
using SecondBrain.PromptEval.Scoring;

namespace SecondBrain.PromptEval.Tuning;

/// <summary>
/// Asks the LLM to propose a revised value for the surface being tuned, given
/// the current value and a summary of recent failures. Returns the new value
/// plus a rationale.
/// </summary>
public sealed class VariantProposer
{
    private readonly HarnessEnvironment _env;
    private readonly ILogger _logger;

    public VariantProposer(HarnessEnvironment env)
    {
        _env = env;
        _logger = env.LoggerFactory.CreateLogger<VariantProposer>();
    }

    public sealed record Proposal(string Value, string Rationale);

    public async Task<Proposal> ProposeAsync(
        string surface,
        string currentValue,
        string productionDefault,
        IReadOnlyList<PromptVariant> recentHistory,
        IReadOnlyList<CaseResult> failures,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt(surface);
        var userMsg = BuildUserMessage(surface, currentValue, productionDefault, recentHistory, failures);

        var systemBlocks = new List<TextBlockParam>
        {
            new() { Text = systemPrompt, CacheControl = new CacheControlEphemeral() },
        };

        // Medium-effort thinking helps the proposer with structural rewriting.
        // EffortConfig handles Vertex vs direct-API uniformly via the Thinking field.
        var (thinking, maxTokens) = EffortConfig.Resolve(Effort.Medium, baseOutputTokens: 16_000);

        var createParams = new MessageCreateParams
        {
            Model = _env.EscalationModel, // sonnet for proposals — better at structural reasoning
            MaxTokens = maxTokens,
            System = new MessageCreateParamsSystem(systemBlocks),
            Messages = [new MessageParam { Role = Role.User, Content = userMsg }],
        };

        if (thinking != null)
            createParams = createParams with { Thinking = thinking };

        var response = await _env.Client.CreateAsync(createParams, ct);

        var sb = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out var text))
                sb.Append(text.Text);

        return ParseProposal(sb.ToString());
    }

    private static string BuildSystemPrompt(string surface) =>
        surface switch
        {
            "system_prompt" => SystemPromptProposerInstructions,
            "user_wrapper" => UserWrapperProposerInstructions,
            "tool_descriptions" => ToolDescriptionsProposerInstructions,
            "query_gen_prompt" => QueryGenProposerInstructions,
            _ => throw new InvalidOperationException($"Unknown surface for proposer: {surface}"),
        };

    private const string SystemPromptProposerInstructions = """
        You are tuning the system prompt template for a personal-knowledge-retrieval LLM agent.
        The agent receives a plain-language user question and must use two tools to answer:
          - search(queries[], filters): full-text search against an FTS5 index of personal docs
          - read_file(path): read full text of a specific file

        The agent is scored on which files it ends up reading (FilesReferenced). The eval set has
        a known "expected" set of target files for each query; recall on that set is what we're
        optimizing (with some weight on precision so it doesn't over-fetch).

        IMPORTANT: The template contains a literal `{ALIASES}` marker. This is substituted at
        runtime with hundreds of name/project aliases. PRESERVE the `{ALIASES}` marker in your
        output verbatim — do not expand it, replace it, or remove it. Treat it as a black box.

        You will be given:
          - The CURRENT template being tested (with `{ALIASES}` marker)
          - The original PRODUCTION DEFAULT template (as an anchor — don't drift far without reason)
          - A short HISTORY of recent variants and their scores
          - The WORST-PERFORMING test cases under the current value (query, expected files,
            actual files, synthesis text)

        Your job: propose ONE revised template. Output the full revised template verbatim,
        keeping the `{ALIASES}` marker intact. Then on a new line write `RATIONALE:` followed
        by 1-3 sentences explaining what you changed and why you think it will improve recall
        on the failing cases without regressing the passing ones.

        Output format:
          ===PROMPT START===
          <revised template verbatim, including {ALIASES} marker>
          ===PROMPT END===
          RATIONALE: <one to three sentences>
        """;

    private const string UserWrapperProposerInstructions = """
        You are tuning the user-message wrapper template for a personal-knowledge-retrieval agent.
        The template is wrapped around the raw user query before it goes to the model. It uses
        `{query}` as the placeholder for the user's actual text. Examples:
          - `{query}` (identity, no transformation)
          - `Find me documents about: {query}`
          - `User question: {query}\n\nPlease search the knowledge base and answer.`

        Output format:
          ===PROMPT START===
          <wrapper template — must contain {query}>
          ===PROMPT END===
          RATIONALE: <one to three sentences>
        """;

    private const string ToolDescriptionsProposerInstructions = """
        Tool description tuning is not yet supported via this proposer. (Implement when ready.)
        """;

    private const string QueryGenProposerInstructions = """
        You are tuning the harness-side prompt that generates synthetic test queries from
        target documents. Better generation produces queries that more realistically simulate
        what an actual user would ask.

        Output format:
          ===PROMPT START===
          <revised generation prompt>
          ===PROMPT END===
          RATIONALE: <one to three sentences>
        """;

    private static string BuildUserMessage(
        string surface,
        string currentValue,
        string productionDefault,
        IReadOnlyList<PromptVariant> recentHistory,
        IReadOnlyList<CaseResult> failures)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Surface: {surface}");
        sb.AppendLine();

        sb.AppendLine("=== PRODUCTION DEFAULT (anchor — don't drift without reason) ===");
        sb.AppendLine(productionDefault);
        sb.AppendLine();

        sb.AppendLine("=== CURRENT VALUE ===");
        sb.AppendLine(currentValue);
        sb.AppendLine();

        if (recentHistory.Count > 0)
        {
            sb.AppendLine("=== RECENT HISTORY ===");
            foreach (var h in recentHistory.TakeLast(5))
            {
                sb.AppendLine($"Iter {h.Id}: F2={h.Score?.MeanF2:F3}  rationale: {h.Rationale ?? "(baseline)"}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("=== WORST-PERFORMING TEST CASES UNDER CURRENT VALUE ===");
        foreach (var f in failures.OrderBy(c => c.Score.F2).Take(5))
        {
            sb.AppendLine($"--- {f.TestCaseId} (F2={f.Score.F2:F2} P={f.Score.Precision:F2} R={f.Score.Recall:F2}) ---");
            sb.AppendLine($"Query: {f.Query}");
            sb.AppendLine($"Expected: {string.Join(", ", f.ExpectedPaths)}");
            sb.AppendLine($"Actual:   {string.Join(", ", f.ActualPaths)}");
            // Truncate synthesis to keep prompt size sane.
            var synth = f.Synthesis.Length > 500 ? f.Synthesis[..500] + "..." : f.Synthesis;
            sb.AppendLine($"Synthesis: {synth}");
            sb.AppendLine();
        }

        sb.AppendLine("Now propose ONE revised value. Output in the format specified by the system prompt.");
        return sb.ToString();
    }

    public static Proposal ParseProposal(string raw)
    {
        // Look for the markers; fall back gracefully if the model formatted differently.
        const string startMarker = "===PROMPT START===";
        const string endMarker = "===PROMPT END===";
        const string rationaleMarker = "RATIONALE:";

        var startIdx = raw.IndexOf(startMarker, StringComparison.Ordinal);
        var endIdx = raw.IndexOf(endMarker, StringComparison.Ordinal);
        var rationaleIdx = raw.IndexOf(rationaleMarker, StringComparison.Ordinal);

        if (startIdx < 0 || endIdx < 0 || endIdx < startIdx)
            throw new InvalidOperationException(
                $"Proposer response did not contain expected markers. Raw output:\n{raw}");

        var value = raw.Substring(startIdx + startMarker.Length, endIdx - startIdx - startMarker.Length).Trim();
        var rationale = rationaleIdx >= 0
            ? raw[(rationaleIdx + rationaleMarker.Length)..].Trim()
            : "(no rationale provided)";

        return new Proposal(value, rationale);
    }
}
