using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SecondBrain.Llm;

namespace SecondBrain.PromptEval.TestCases;

/// <summary>
/// Picks documents from the live FTS index (stratified by source_type) and asks
/// Claude to generate a plain-language query that should retrieve each one.
/// </summary>
public sealed class TestCaseGenerator
{
    private readonly HarnessEnvironment _env;
    private readonly ILogger _logger;
    private readonly Random _rng;

    public const int MinDocChars = 500;
    public const int MaxDocCharsForPrompt = 8000;

    // Default query-generation prompt. Tunable in a later phase via overrides.
    public const string DefaultQueryGenPrompt = """
        You are generating a realistic user query for a personal knowledge retrieval system.
        The system stores meeting transcripts, 1:1 notes, planning documents, daily standups,
        and personal notes from one engineer's work life.

        Given the document below, write a single short question (one to two sentences,
        max 25 words) that someone might naturally ask if they were trying to find this
        information days or weeks later.

        Critical rules:
        - Do NOT quote unique phrases verbatim. Phrase it the way a real user would, who
          remembers the gist but not the exact wording.
        - Do NOT mention the document type ("transcript", "1on1", etc.) unless a real user
          would have a reason to.
        - Do NOT use proper nouns that only appear once in the doc — those make the query
          trivially searchable and don't test retrieval quality.
        - DO use the kind of vocabulary the user would naturally reach for: project names
          they'd remember, people they collaborated with, the gist of the topic.

        Output the question only, with no preamble, no quotes, and no explanation.
        """;

    public TestCaseGenerator(HarnessEnvironment env, int seed = 42)
    {
        _env = env;
        _logger = env.LoggerFactory.CreateLogger<TestCaseGenerator>();
        _rng = new Random(seed);
    }

    public sealed record GenerationConfig(
        IReadOnlyDictionary<string, int> CountPerSourceType,
        string SetId,
        string? QueryGenPromptOverride = null);

    public static GenerationConfig DefaultConfig() => new(
        // Calibrated to actual index distribution: ~4800 untyped docs (NULL),
        // ~150 transcripts, ~60 notes, ~25 backlog items. Other types have <5 docs each
        // so they're not worth stratifying on.
        CountPerSourceType: new Dictionary<string, int>
        {
            ["transcript"] = 5,
            ["note"] = 4,
            [""] = 4, // NULL source_type bucket — vast majority of the corpus
            ["product backlog item"] = 2,
        },
        SetId: "tc-v1");

    public async Task<TestCaseSet> GenerateAsync(GenerationConfig config, CancellationToken ct)
    {
        _logger.LogInformation("Stratified pick from {DbPath}", _env.FtsDbPath);

        var picked = PickDocumentsStratified(config.CountPerSourceType);
        _logger.LogInformation("Picked {Count} candidate documents", picked.Count);

        var cases = new List<TestCase>(picked.Count);
        var prompt = config.QueryGenPromptOverride ?? DefaultQueryGenPrompt;
        var caseIndex = 0;

        foreach (var doc in picked)
        {
            ct.ThrowIfCancellationRequested();
            caseIndex++;

            string content;
            try
            {
                content = await File.ReadAllTextAsync(doc.AbsolutePath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping {Path}: read failed", doc.RelativePath);
                continue;
            }

            if (content.Length < MinDocChars)
            {
                _logger.LogDebug("Skipping {Path}: too short ({Len} chars)", doc.RelativePath, content.Length);
                continue;
            }

            var truncated = content.Length > MaxDocCharsForPrompt
                ? content[..MaxDocCharsForPrompt]
                : content;

            var query = await GenerateQueryAsync(prompt, truncated, doc, ct);
            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.LogWarning("Empty query generated for {Path}, skipping", doc.RelativePath);
                continue;
            }

            var displaySourceType = string.IsNullOrEmpty(doc.SourceType) ? "unknown" : doc.SourceType;
            cases.Add(new TestCase
            {
                Id = $"tc_{caseIndex:D3}",
                TargetPaths = [doc.AbsolutePath],
                Query = query.Trim(),
                SourceType = displaySourceType,
                Rationale = $"Doc is {displaySourceType} at {doc.RelativePath}",
                GeneratedAt = DateTimeOffset.UtcNow.ToString("o"),
            });

            _logger.LogInformation("[{N}/{Total}] {Type}: {Query}",
                cases.Count, picked.Count, doc.SourceType, query.Trim());
        }

        return new TestCaseSet
        {
            Id = config.SetId,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("o"),
            IndexFingerprint = ComputeIndexFingerprint(),
            Cases = cases,
        };
    }

    // ── Document picking ─────────────────────────────────────────────────────────

    private sealed record DocRecord(
        long Id,
        string AbsolutePath,
        string RelativePath,
        string SourceType,
        long SizeBytes);

    private List<DocRecord> PickDocumentsStratified(IReadOnlyDictionary<string, int> countPerType)
    {
        // Load all candidate docs (with summary set, meaning they survived the summarizer
        // pass — those are the ones with substantive content) grouped by source_type.
        // Empty-string key in countPerType matches NULL source_type rows.
        var byType = new Dictionary<string, List<DocRecord>>();

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _env.FtsDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, absolute_path, relative_path, source_type, size_bytes
              FROM files
             WHERE summary IS NOT NULL
               AND length(summary) > 0
               AND size_bytes >= @minSize
            """;
        cmd.Parameters.AddWithValue("@minSize", MinDocChars);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sourceType = reader.IsDBNull(3) ? "" : reader.GetString(3);
            if (!countPerType.ContainsKey(sourceType))
                continue;

            var doc = new DocRecord(
                Id: reader.GetInt64(0),
                AbsolutePath: reader.GetString(1),
                RelativePath: reader.GetString(2),
                SourceType: sourceType,
                SizeBytes: reader.GetInt64(4));

            if (!byType.TryGetValue(sourceType, out var list))
                byType[sourceType] = list = new List<DocRecord>();
            list.Add(doc);
        }

        // Sample N per stratum.
        var picked = new List<DocRecord>();
        foreach (var (sourceType, target) in countPerType)
        {
            if (!byType.TryGetValue(sourceType, out var pool) || pool.Count == 0)
            {
                _logger.LogWarning("No candidates for source_type={Type}", sourceType);
                continue;
            }

            // Shuffle then take. Random with fixed seed for reproducibility.
            var shuffled = pool.OrderBy(_ => _rng.Next()).ToList();
            picked.AddRange(shuffled.Take(target));

            if (shuffled.Count < target)
                _logger.LogWarning("Only {Avail} candidates for source_type={Type} (wanted {Want})",
                    shuffled.Count, sourceType, target);
        }

        return picked;
    }

    // ── Query generation via LLM ─────────────────────────────────────────────────

    private async Task<string> GenerateQueryAsync(string systemPrompt, string docContent, DocRecord doc, CancellationToken ct)
    {
        var userMsg = $"Document type: {doc.SourceType}\nDocument path (do not quote): {doc.RelativePath}\n\n--- DOCUMENT START ---\n{docContent}\n--- DOCUMENT END ---";

        var systemBlocks = new List<TextBlockParam>
        {
            new() { Text = systemPrompt, CacheControl = new CacheControlEphemeral() },
        };

        var createParams = new MessageCreateParams
        {
            Model = _env.DefaultModel,
            MaxTokens = 200,
            System = new MessageCreateParamsSystem(systemBlocks),
            Messages =
            [
                new MessageParam { Role = Role.User, Content = userMsg },
            ],
        };

        var supportsOutputConfig = !string.Equals(
            Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX"), "1", StringComparison.Ordinal);
        if (supportsOutputConfig)
            createParams = createParams with { OutputConfig = new OutputConfig { Effort = Effort.Low } };

        var response = await _env.Client.CreateAsync(createParams, ct);

        var sb = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out var text))
                sb.Append(text.Text);

        return sb.ToString();
    }

    // ── Index fingerprint ────────────────────────────────────────────────────────

    private string ComputeIndexFingerprint()
    {
        // Cheap fingerprint: file count + last_indexed_at. Not cryptographic;
        // just enough to detect "the corpus shifted significantly" between runs.
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _env.FtsDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(indexed_at) FROM files";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var count = reader.GetInt64(0);
        var maxIndexed = reader.IsDBNull(1) ? "" : reader.GetString(1);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{count}|{maxIndexed}"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
