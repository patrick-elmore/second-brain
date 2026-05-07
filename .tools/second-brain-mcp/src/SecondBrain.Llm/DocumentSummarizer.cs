using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SecondBrain.Llm;

/// <summary>
/// Generates retrieval-optimized summaries for documents in the index.
/// Processes documents in batches: each API call contains up to <c>BatchSize</c>
/// documents, with a cached system prompt holding all summarization instructions.
/// Reduces call count from N to ceil(N / BatchSize) while benefiting from
/// prompt caching on the system prompt across the run.
/// </summary>
public sealed class DocumentSummarizer
{
    /// <summary>Maximum chars of document content per API call (≈20K tokens).</summary>
    public const int ContentBudgetChars = 80_000;

    private readonly IAnthropicClient _client;
    private readonly ILogger _logger;
    private readonly IStatsRecorder? _stats;
    private readonly bool _supportsOutputConfig;

    // Regex to extract SUMMARY blocks: =====BEGIN:SUMMARY:N=====\n...\n=====END:SUMMARY:N=====
    private static readonly Regex SummaryPattern = new(
        @"={5}BEGIN:SUMMARY:(\d+)={5}\s*(.*?)\s*={5}END:SUMMARY:\1={5}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public DocumentSummarizer(IAnthropicClient client, ILogger? logger = null, IStatsRecorder? stats = null)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
        _stats = stats;
        _supportsOutputConfig = !string.Equals(
            Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX"), "1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Summarizes a batch of documents in a single API call.
    /// Returns one entry per successfully summarized document (skipped docs are omitted).
    /// The returned summary includes the programmatic metadata prefix.
    /// </summary>
    public async Task<IReadOnlyList<(long Id, string Summary)>> SummarizeBatchAsync(
        IReadOnlyList<BatchDocEntry> docs,
        CancellationToken ct)
    {
        // Read and filter documents — skip those with insufficient content
        var prepared = new List<PreparedDoc>();
        foreach (var doc in docs)
        {
            string content;
            try { content = File.ReadAllText(doc.AbsolutePath); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read {Path}", doc.AbsolutePath);
                continue;
            }

            if (content.TrimEnd().Length < 100)
            {
                _logger.LogDebug("Skipping {Path}: content too short", doc.AbsolutePath);
                continue;
            }

            var charLimit = InputCharLimit(doc.SourceType);
            var truncated = content.Length > charLimit ? content[..charLimit] : content;
            prepared.Add(new PreparedDoc(doc, truncated, prepared.Count + 1));
        }

        if (prepared.Count == 0)
            return [];

        // Build user message — one block per doc
        var userMsg = BuildUserMessage(prepared);
        var maxTokens = Math.Min(prepared.Count * 500, 8192);

        var systemBlocks = new List<TextBlockParam>
        {
            new() { Text = BatchSystemPrompt, CacheControl = new CacheControlEphemeral() },
        };

        var createParams = new MessageCreateParams
        {
            Model = "claude-haiku-4-5",
            MaxTokens = maxTokens,
            System = new MessageCreateParamsSystem(systemBlocks),
            Messages =
            [
                new MessageParam { Role = Role.User, Content = userMsg },
            ],
        };

        if (_supportsOutputConfig)
            createParams = createParams with { OutputConfig = new OutputConfig { Effort = Effort.Low } };

        string responseText;
        try
        {
            var response = await _client.Messages.Create(createParams, ct);
            _stats?.RecordLlmCall(
                "claude-haiku-4-5",
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                response.Usage.CacheCreationInputTokens ?? 0,
                response.Usage.CacheReadInputTokens ?? 0);
            responseText = response.Content
                .Where(b => b.TryPickText(out _))
                .Select(b => { b.TryPickText(out var t); return t!.Text; })
                .FirstOrDefault() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch API call failed for {Count} docs", prepared.Count);
            return [];
        }

        // Parse response — extract SUMMARY blocks by sequential ID
        var results = new List<(long Id, string Summary)>();
        foreach (Match m in SummaryPattern.Matches(responseText))
        {
            if (!int.TryParse(m.Groups[1].Value, out var seq)) continue;
            var doc = prepared.FirstOrDefault(d => d.SequenceId == seq);
            if (doc == null) continue;

            var llmSummary = m.Groups[2].Value.Trim();
            if (string.IsNullOrEmpty(llmSummary)) continue;

            // Prepend programmatic metadata prefix
            var prefix = BuildPrefix(doc.Entry.SourceType, doc.Entry.MetadataJson, doc.Entry.RelativePath);
            var fullSummary = string.IsNullOrEmpty(prefix) ? llmSummary : prefix + "\n" + llmSummary;

            results.Add((doc.Entry.Id, fullSummary));
        }

        _logger.LogDebug("Batch: {Prepared} docs sent, {Parsed} summaries parsed", prepared.Count, results.Count);
        return results;
    }

    // ── Message building ──────────────────────────────────────────────────────

    private static string BuildUserMessage(IReadOnlyList<PreparedDoc> docs)
    {
        var sb = new StringBuilder();
        foreach (var doc in docs)
        {
            var typeTag = string.IsNullOrEmpty(doc.Entry.SourceType) ? "unknown" : doc.Entry.SourceType;
            sb.AppendLine($"=====BEGIN:DOC:{doc.SequenceId} type={typeTag} path={doc.Entry.RelativePath}=====");
            sb.AppendLine(doc.Content);
            sb.AppendLine($"=====END:DOC:{doc.SequenceId}=====");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── Programmatic prefix ───────────────────────────────────────────────────

    private static string BuildPrefix(string? sourceType, string? metadataJson, string relativePath)
    {
        var parts = new List<string>();

        var date = ExtractDate(metadataJson, relativePath);
        if (!string.IsNullOrEmpty(date)) parts.Add(date);

        if (!string.IsNullOrEmpty(sourceType)) parts.Add(sourceType);

        if (sourceType is "1on1" or "transcript")
        {
            var attendees = ExtractAttendees(metadataJson);
            if (!string.IsNullOrEmpty(attendees)) parts.Add(attendees);
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : "";
    }

    private static string? ExtractDate(string? metadataJson, string relativePath)
    {
        if (!string.IsNullOrEmpty(metadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.TryGetProperty("created", out var created))
                {
                    var raw = created.GetString();
                    if (!string.IsNullOrEmpty(raw)) return raw[..Math.Min(10, raw.Length)];
                }
            }
            catch { }
        }

        var filename = Path.GetFileName(relativePath);
        for (var i = 0; i <= filename.Length - 10; i++)
        {
            if (char.IsDigit(filename[i]) && i + 4 < filename.Length && filename[i + 4] == '-' &&
                i + 7 < filename.Length && filename[i + 7] == '-')
            {
                var candidate = filename.Substring(i, 10);
                if (DateOnly.TryParse(candidate, out _)) return candidate;
            }
        }

        return null;
    }

    private static string? ExtractAttendees(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("attendees", out var attendees)) return null;

            if (attendees.ValueKind == JsonValueKind.Array)
            {
                var names = attendees.EnumerateArray()
                    .Select(a => a.GetString()?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Take(6)
                    .ToList();
                return names.Count > 0 ? string.Join(", ", names) : null;
            }

            if (attendees.ValueKind == JsonValueKind.String)
                return attendees.GetString()?.Trim();
        }
        catch { }
        return null;
    }

    // ── Type strategy ─────────────────────────────────────────────────────────

    public static int InputCharLimit(string? sourceType) =>
        sourceType?.ToLowerInvariant() switch
        {
            "1on1" => 24_000,
            "transcript" => 20_000,
            "standup" => 6_000,
            "planning" => 16_000,
            "note" => 8_000,
            _ => 12_000,
        };

    // ── System prompt (cached) ────────────────────────────────────────────────

    private const string BatchSystemPrompt = """
        You are a document summarization engine for a personal knowledge retrieval system.
        You receive multiple documents in a single request. Each document is delimited by:

            =====BEGIN:DOC:N type=<source_type> path=<relative_path>=====
            (document content)
            =====END:DOC:N=====

        Where N is a sequential integer starting at 1.

        YOUR TASK:
        Summarize EACH document independently. Documents are completely unrelated to each
        other — do not let the content of one influence the summary of another.

        For EVERY document block you receive, produce exactly one summary block:

            =====BEGIN:SUMMARY:N=====
            (your summary)
            =====END:SUMMARY:N=====

        The N in your SUMMARY block must match the N in the corresponding DOC block.
        Produce summaries in ascending order of N. Do not skip any N.
        Do not produce any text outside the SUMMARY blocks.

        If a document has no meaningful content, produce:
            =====BEGIN:SUMMARY:N=====
            (no substantive content)
            =====END:SUMMARY:N=====

        UNIVERSAL SUMMARIZATION RULES (apply to every document regardless of type):
        - Lead with the most retrieval-relevant information
        - Use specific names, dates, project names, and technical terms as they appear
        - Correct obvious voice-to-text transcription errors silently (homophone swaps,
          dropped letters in proper nouns) — do not reproduce garbled text
        - Do not pad; stop when the substance is captured
        - Plain text only — no markdown headers, no bullet lists, no bold, no formatting
        - Do not describe the document's format, length, or structure — only its content
        - Do not open with "This document..." or "This transcript..." — start with the content

        TYPE-SPECIFIC GUIDANCE (determined by the type= tag on each DOC block):

        type=1on1 — one-on-one meeting transcript. Maximum 450 tokens.
          Extract: the primary agenda, each distinct topic with its decision/conclusion/outcome,
          action items and owners, any unresolved questions or pushback.

        type=transcript — general meeting transcript. Maximum 300 tokens.
          Extract: the meeting's purpose, key decisions and agreements, action items and owners,
          significant disagreements or open questions.

        type=standup — daily standup. Maximum 150 tokens.
          One or two dense sentences: what the team was working on, any blockers or incidents,
          any notable announcements or context changes. Skip purely formulaic status updates.

        type=planning — planning artifact, spec, or technical document. Maximum 250 tokens.
          Extract: what is being planned or built, the chosen approach, scope boundaries
          (what is explicitly in and out), open decisions or unresolved dependencies.
          Preserve technical terminology, system names, story/ticket numbers.

        type=note — general note or journal entry. Maximum 150 tokens.
          1-3 sentences on the note's purpose and content. If the note is brief, one sentence
          is sufficient — do not expand to fill the budget.

        type=unknown or any other value — reference docs, guides, logs, templates, work items.
          Maximum 200 tokens. 2-3 sentences: what the document covers, who it is for,
          specific tools/systems/technologies/versions mentioned.
        """;

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed class PreparedDoc(BatchDocEntry entry, string content, int sequenceId)
    {
        public BatchDocEntry Entry { get; } = entry;
        public string Content { get; } = content;
        public int SequenceId { get; } = sequenceId;
    }
}

public sealed record BatchDocEntry(
    long Id,
    string AbsolutePath,
    string RelativePath,
    string? SourceType,
    string? MetadataJson);
