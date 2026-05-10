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
    /// <summary>
    /// Default per-source-type input char limits, used when no override dict
    /// is supplied. Mirrors the historical hardcoded switch.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultInputCharLimits =
        new Dictionary<string, int>
        {
            ["1on1"] = 24_000,
            ["transcript"] = 20_000,
            ["standup"] = 6_000,
            ["planning"] = 16_000,
            ["note"] = 8_000,
            ["default"] = 12_000,
        };

    /// <summary>
    /// Default canonical content types, used when the caller doesn't supply an
    /// override. Matches <c>SecondBrainSettings.SourceTypes</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSourceTypes =
        ["transcript", "standup", "1on1", "planning", "note"];

    private readonly IMessageCreator _client;
    private readonly ILogger _logger;
    private readonly IStatsRecorder? _stats;
    private readonly int _contentBudgetChars;
    private readonly int _maxOutputTokens;
    private readonly IReadOnlyDictionary<string, int> _inputCharLimits;
    private readonly IReadOnlyList<string> _sourceTypes;
    private readonly HashSet<string> _sourceTypeLookup;
    private readonly string _batchSystemPrompt;

    /// <summary>Maximum chars of document content per API call.</summary>
    public int ContentBudgetChars => _contentBudgetChars;

    /// <summary>The system prompt actually sent to the model. Exposed for testing.</summary>
    public string BatchSystemPrompt => _batchSystemPrompt;

    // Regex to extract SUMMARY blocks: =====BEGIN:SUMMARY:N=====\n...\n=====END:SUMMARY:N=====
    private static readonly Regex SummaryPattern = new(
        @"={5}BEGIN:SUMMARY:(\d+)={5}\s*(.*?)\s*={5}END:SUMMARY:\1={5}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Captures the leading "type: <value>" line of a summary block.
    private static readonly Regex TypeLinePattern = new(
        @"^\s*type\s*:\s*(\S+)\s*\r?\n",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DocumentSummarizer(
        IMessageCreator client,
        int contentBudgetChars = 80_000,
        int maxOutputTokens = 8_192,
        IReadOnlyDictionary<string, int>? inputCharLimits = null,
        IReadOnlyList<string>? sourceTypes = null,
        ILogger? logger = null,
        IStatsRecorder? stats = null)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
        _stats = stats;
        _contentBudgetChars = contentBudgetChars;
        _maxOutputTokens = maxOutputTokens;
        _inputCharLimits = inputCharLimits ?? DefaultInputCharLimits;
        _sourceTypes = sourceTypes is { Count: > 0 } ? sourceTypes : DefaultSourceTypes;
        _sourceTypeLookup = new HashSet<string>(_sourceTypes, StringComparer.OrdinalIgnoreCase);
        _batchSystemPrompt = BuildBatchSystemPrompt(_sourceTypes);
    }

    /// <summary>
    /// Summarizes a batch of documents in a single API call.
    /// Returns one <see cref="SummarizationResult"/> per input doc with an explicit outcome:
    /// <see cref="SummarizationOutcome.Summarized"/>, <see cref="SummarizationOutcome.Skipped"/>
    /// (permanent — content too short, unreadable, or no parseable summary), or
    /// <see cref="SummarizationOutcome.Failed"/> (transient — whole API call threw).
    /// The caller is responsible for retiring Skipped rows so they aren't re-attempted forever.
    /// </summary>
    public async Task<IReadOnlyList<SummarizationResult>> SummarizeBatchAsync(
        IReadOnlyList<BatchDocEntry> docs,
        CancellationToken ct)
    {
        // Read and filter documents — record an outcome for each input so callers can
        // distinguish permanent skips from transient API failures.
        var results = new List<SummarizationResult>(docs.Count);
        var prepared = new List<PreparedDoc>();
        foreach (var doc in docs)
        {
            string content;
            try { content = File.ReadAllText(doc.AbsolutePath); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read {Path}", doc.AbsolutePath);
                results.Add(SummarizationResult.Skip(doc.Id, $"unreadable: {ex.GetType().Name}"));
                continue;
            }

            if (content.TrimEnd().Length < 100)
            {
                _logger.LogDebug("Skipping {Path}: content too short", doc.AbsolutePath);
                results.Add(SummarizationResult.Skip(doc.Id, "content too short"));
                continue;
            }

            var charLimit = InputCharLimit(doc.SourceType);
            var truncated = content.Length > charLimit ? content[..charLimit] : content;
            prepared.Add(new PreparedDoc(doc, truncated, prepared.Count + 1));
        }

        if (prepared.Count == 0)
            return results;

        // Build user message — one block per doc
        var userMsg = BuildUserMessage(prepared);
        var maxTokens = Math.Min(prepared.Count * 500, _maxOutputTokens);

        var systemBlocks = new List<TextBlockParam>
        {
            new() { Text = _batchSystemPrompt, CacheControl = new CacheControlEphemeral() },
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

        // Summaries are short; no thinking needed.
        // (Effort.Low maps to no Thinking via EffortConfig, so omit entirely.)

        string responseText;
        try
        {
            var response = await _client.CreateAsync(createParams, ct);
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
            var reason = $"API error: {ex.GetType().Name}";
            foreach (var doc in prepared)
                results.Add(SummarizationResult.Fail(doc.Entry.Id, reason));
            return results;
        }

        // Parse response — extract SUMMARY blocks by sequential ID
        var summarizedSeqs = new HashSet<int>();
        foreach (Match m in SummaryPattern.Matches(responseText))
        {
            if (!int.TryParse(m.Groups[1].Value, out var seq)) continue;
            var doc = prepared.FirstOrDefault(d => d.SequenceId == seq);
            if (doc == null) continue;

            var (chosenType, llmSummary) = ExtractTypeAndSummary(m.Groups[2].Value);
            if (string.IsNullOrEmpty(llmSummary)) continue;

            // Effective type for prefix-building: model's choice if it picked a known
            // canonical value, otherwise fall back to whatever the file already had.
            var effectiveType = chosenType ?? doc.Entry.SourceType;
            var prefix = BuildPrefix(effectiveType, doc.Entry.MetadataJson, doc.Entry.RelativePath);
            var fullSummary = string.IsNullOrEmpty(prefix) ? llmSummary : prefix + "\n" + llmSummary;

            results.Add(SummarizationResult.Ok(doc.Entry.Id, fullSummary, chosenType));
            summarizedSeqs.Add(seq);
        }

        // Any prepared doc the model didn't summarize is a permanent skip — retrying would
        // get the same result and burn tokens forever.
        foreach (var doc in prepared)
        {
            if (!summarizedSeqs.Contains(doc.SequenceId))
                results.Add(SummarizationResult.Skip(doc.Entry.Id, "no summary parsed from response"));
        }

        var parsed = summarizedSeqs.Count;
        _logger.LogDebug("Batch: {Prepared} docs sent, {Parsed} summaries parsed", prepared.Count, parsed);
        return results;
    }

    // ── Message building ──────────────────────────────────────────────────────

    private static string BuildUserMessage(IReadOnlyList<PreparedDoc> docs)
    {
        var sb = new StringBuilder();
        foreach (var doc in docs)
        {
            sb.AppendLine($"=====BEGIN:DOC:{doc.SequenceId} path={doc.Entry.RelativePath}=====");
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

    // ── Response parsing ──────────────────────────────────────────────────────

    /// <summary>
    /// Splits a SUMMARY block body into its (type, summary-text) parts.
    /// Returns (null, body) if the body has no recognizable "type:" line.
    /// Returns (null, body-minus-type-line) if the type value isn't in the
    /// configured set — the prose is still usable, but the classification is dropped.
    /// </summary>
    internal (string? sourceType, string summary) ExtractTypeAndSummary(string blockBody)
    {
        var match = TypeLinePattern.Match(blockBody);
        if (!match.Success)
            return (null, blockBody.Trim());

        var rawType = match.Groups[1].Value.Trim().ToLowerInvariant();
        var prose = blockBody[match.Length..].TrimStart();

        // Drop a "summary:" prefix if the model emitted one
        if (prose.StartsWith("summary:", StringComparison.OrdinalIgnoreCase))
            prose = prose["summary:".Length..].TrimStart();

        prose = prose.TrimEnd();
        var validatedType = _sourceTypeLookup.Contains(rawType) ? rawType : null;
        return (validatedType, prose);
    }

    // ── Type strategy ─────────────────────────────────────────────────────────

    public int InputCharLimit(string? sourceType)
    {
        var key = sourceType?.ToLowerInvariant();
        if (key != null && _inputCharLimits.TryGetValue(key, out var v))
            return v;
        if (_inputCharLimits.TryGetValue("default", out var defaultValue))
            return defaultValue;
        return 12_000;
    }

    // ── System prompt (cached) ────────────────────────────────────────────────

    /// <summary>
    /// Builds the cached system prompt with the configured source-type list
    /// injected. Adding a new type to <c>SecondBrainSettings.SourceTypes</c>
    /// updates the prompt automatically — no parallel edits required.
    /// </summary>
    private static string BuildBatchSystemPrompt(IReadOnlyList<string> sourceTypes)
    {
        var typeList = string.Join(", ", sourceTypes);
        return $$"""
            You are a document summarization engine for a personal knowledge retrieval system.
            You receive multiple documents in a single request. Each document is delimited by:

                =====BEGIN:DOC:N path=<relative_path>=====
                (document content)
                =====END:DOC:N=====

            Where N is a sequential integer starting at 1.

            YOUR TASK:
            For each document, produce a short summary AND assign it a single content type
            from this list:

                {{typeList}}

            Documents are completely unrelated to each other — do not let the content of one
            influence the summary or type of another.

            For EVERY document block you receive, produce exactly one summary block in this
            exact shape:

                =====BEGIN:SUMMARY:N=====
                type: <one of the values listed above>
                summary: (your summary, plain prose)
                =====END:SUMMARY:N=====

            REQUIREMENTS:
            - The "type:" line MUST be the first line inside the block and MUST exactly match
              one of the values listed above (case-insensitive). Pick the value that best
              describes the document's content based on what's actually in it (not the file
              name or path). If nothing fits cleanly, pick the closest value — do not invent
              new types.
            - The N in your SUMMARY block must match the N in the corresponding DOC block.
            - Produce summaries in ascending order of N. Do not skip any N.
            - Do not produce any text outside the SUMMARY blocks.

            If a document has no meaningful content, produce:
                =====BEGIN:SUMMARY:N=====
                type: note
                summary: (no substantive content)
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

            TYPE-SPECIFIC GUIDANCE (apply when the type you choose matches one of the names below):

            1on1 — one-on-one meeting transcript. Maximum 450 tokens.
              Extract: the primary agenda, each distinct topic with its decision/conclusion/outcome,
              action items and owners, any unresolved questions or pushback.

            transcript — general meeting transcript. Maximum 300 tokens.
              Extract: the meeting's purpose, key decisions and agreements, action items and owners,
              significant disagreements or open questions.

            standup — daily standup. Maximum 150 tokens.
              One or two dense sentences: what the team was working on, any blockers or incidents,
              any notable announcements or context changes. Skip purely formulaic status updates.

            planning — planning artifact, spec, or technical document. Maximum 250 tokens.
              Extract: what is being planned or built, the chosen approach, scope boundaries
              (what is explicitly in and out), open decisions or unresolved dependencies.
              Preserve technical terminology, system names, story/ticket numbers.

            note — general note or journal entry. Maximum 150 tokens.
              1-3 sentences on the note's purpose and content. If the note is brief, one sentence
              is sufficient — do not expand to fill the budget.

            For any type not covered by guidance above (reference docs, guides, logs, templates,
            work items, or new categories the operator added): maximum 200 tokens. 2-3 sentences
            covering what the document is about, who or what it concerns, and any specific
            tools/systems/technologies mentioned.
            """;
    }

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
