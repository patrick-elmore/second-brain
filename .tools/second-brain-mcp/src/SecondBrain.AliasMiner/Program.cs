using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SecondBrain.Llm;

// ── Parse args ────────────────────────────────────────────────────────────────
string? configPath = null;
string outputDir = Path.Combine(Directory.GetCurrentDirectory(), "alias-mining");
int workers = 5;
string effortStr = "medium";
int batchSize = 15;
bool dryRun = false;
bool clearOutput = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
        case "--output" when i + 1 < args.Length: outputDir = args[++i]; break;
        case "--workers" when i + 1 < args.Length: workers = int.Parse(args[++i]); break;
        case "--effort" when i + 1 < args.Length: effortStr = args[++i]; break;
        case "--batch-size" when i + 1 < args.Length: batchSize = int.Parse(args[++i]); break;
        case "--dry-run": dryRun = true; break;
        case "--clear-output": clearOutput = true; break;
    }
}

// ── Setup logging ─────────────────────────────────────────────────────────────
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
           .SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("AliasMiner");

// ── Load config ───────────────────────────────────────────────────────────────
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var defaultConfigPath = Path.Combine(localAppData, "SecondBrainMcpServer", "mcp_config.json");

string resolvedConfig;
if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
    resolvedConfig = configPath;
else if (File.Exists(defaultConfigPath))
    resolvedConfig = defaultConfigPath;
else
{
    Console.Error.WriteLine($"Config not found. Checked:\n  {configPath ?? "(--config not specified)"}\n  {defaultConfigPath}");
    return 1;
}

string ftsDbPath;
string vertexBaseUrl = "";
string anthropicApiKeyEnv = "ANTHROPIC_API_KEY";
try
{
    using var configDoc = JsonDocument.Parse(File.ReadAllText(resolvedConfig));
    var sb = configDoc.RootElement.GetProperty("second_brain");
    var rawDbPath = sb.GetProperty("fts_db_path").GetString() ?? "index/fts.db";
    var configDir = Path.GetDirectoryName(resolvedConfig)!;
    ftsDbPath = Path.IsPathRooted(rawDbPath) ? rawDbPath : Path.Combine(configDir, rawDbPath);
    if (sb.TryGetProperty("vertex_base_url", out var vbu)) vertexBaseUrl = vbu.GetString() ?? "";
    if (sb.TryGetProperty("anthropic_api_key_env", out var ake)) anthropicApiKeyEnv = ake.GetString() ?? "ANTHROPIC_API_KEY";
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to read config at {resolvedConfig}: {ex.Message}");
    return 1;
}

logger.LogInformation("Config: {Config}", resolvedConfig);
logger.LogInformation("DB: {Db}", ftsDbPath);
logger.LogInformation("Output: {Output}", outputDir);

// ── Validate output dir ───────────────────────────────────────────────────────
var batchesDir = Path.Combine(outputDir, "batches");
if (!clearOutput && Directory.Exists(batchesDir))
{
    var existing = Directory.GetFiles(batchesDir, "batch-*.json", SearchOption.TopDirectoryOnly);
    if (existing.Length > 0)
    {
        Console.Error.WriteLine($"Output dir contains {existing.Length} existing batch files. Use --clear-output to overwrite.");
        return 1;
    }
}
if (clearOutput && Directory.Exists(outputDir))
    Directory.Delete(outputDir, recursive: true);
Directory.CreateDirectory(batchesDir);

// ── Build Anthropic client ────────────────────────────────────────────────────
var apiKey = Environment.GetEnvironmentVariable(anthropicApiKeyEnv) ?? "";
var useVertex = string.Equals(
    Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX"), "1", StringComparison.Ordinal);
if (!useVertex && string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine($"API key not set. Set {anthropicApiKeyEnv} or use Vertex (CLAUDE_CODE_USE_VERTEX=1).");
    return 1;
}
var supportsOutputConfig = !useVertex;
var client = ClaudeSessionFactory.BuildClient(apiKey, string.IsNullOrEmpty(vertexBaseUrl) ? null : vertexBaseUrl);

// ── Phase A: SQL extraction ───────────────────────────────────────────────────
var sw = System.Diagnostics.Stopwatch.StartNew();
logger.LogInformation("Phase A: querying index...");

var docRecords = new List<DocRecord>();
using var connection = new SqliteConnection($"Data Source={ftsDbPath};Mode=ReadOnly;");
connection.Open();

using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = @"
        SELECT id, relative_path, absolute_path, source_type, summary, size_bytes,
               json_extract(metadata, '$.attendees') AS attendees
          FROM files
         ORDER BY id";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        docRecords.Add(new DocRecord(
            Id: reader.GetInt64(0),
            RelativePath: reader.IsDBNull(1) ? "" : reader.GetString(1),
            AbsolutePath: reader.IsDBNull(2) ? "" : reader.GetString(2),
            SourceType: reader.IsDBNull(3) ? "" : reader.GetString(3),
            Summary: reader.IsDBNull(4) ? null : reader.GetString(4),
            SizeBytes: reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
            AttendeesJson: reader.IsDBNull(6) ? null : reader.GetString(6)));
    }
}

logger.LogInformation("Phase A: {Count} docs loaded", docRecords.Count);

// Build signal blobs from SQL data
var signalBlobs = docRecords.Select(doc =>
{
    var candidates = ExtractCandidates(
        Path.GetFileNameWithoutExtension(doc.RelativePath),
        doc.Summary ?? "",
        ParseAttendeesJson(doc.AttendeesJson));
    return new SignalBlob(doc, candidates);
}).ToList();

// ── Phase B: Body sampling ────────────────────────────────────────────────────
logger.LogInformation("Phase B: body sampling for sparse docs...");
int bodySampled = 0;
foreach (var blob in signalBlobs)
{
    var doc = blob.Doc;
    bool needsBody = doc.Summary is null
        || doc.Summary.Length < 200
        || (doc.SizeBytes > 20_000 && blob.Candidates.Count < 3);
    bool alreadySaturated = blob.Candidates.Count >= 5;

    if (!needsBody || alreadySaturated || string.IsNullOrEmpty(doc.AbsolutePath))
        continue;

    try
    {
        using var fs = File.OpenRead(doc.AbsolutePath);
        var head = new byte[4096];
        int headRead = fs.Read(head, 0, head.Length);
        var headStr = Encoding.UTF8.GetString(head, 0, headRead);

        string tailStr = "";
        if (doc.SizeBytes > 4096 + 2048)
        {
            fs.Seek(-2048, SeekOrigin.End);
            var tail = new byte[2048];
            int tailRead = fs.Read(tail, 0, tail.Length);
            tailStr = Encoding.UTF8.GetString(tail, 0, tailRead);
        }

        var bodyCandidates = ExtractCandidates("", headStr + " " + tailStr, []);
        foreach (var c in bodyCandidates)
            blob.Candidates.Add(c);
        bodySampled++;
    }
    catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
    {
        logger.LogDebug("Body read skipped for {Path}: {Msg}", doc.RelativePath, ex.Message);
    }
}

logger.LogInformation("Phase B: {Count} docs body-sampled", bodySampled);

// ── Dry run: write signals and exit ──────────────────────────────────────────
if (dryRun)
{
    var signalsPath = Path.Combine(outputDir, "signals.json");
    var signalsOutput = signalBlobs.Select(b => new
    {
        id = b.Doc.Id,
        relative_path = b.Doc.RelativePath,
        source_type = b.Doc.SourceType,
        candidates = b.Candidates.ToArray(),
    });
    await File.WriteAllTextAsync(signalsPath,
        JsonSerializer.Serialize(signalsOutput, new JsonSerializerOptions { WriteIndented = true }));
    logger.LogInformation("Dry run complete. Signals written to {Path}", signalsPath);
    return 0;
}

// ── Phase C: Haiku consolidation ──────────────────────────────────────────────
logger.LogInformation("Phase C: building batches...");

const int MaxBatchChars = 12_000;
var batches = new List<List<SignalBlob>>();
var current = new List<SignalBlob>();
int currentChars = 0;

foreach (var blob in signalBlobs)
{
    var blobText = BuildBlobText(blob);
    if (current.Count >= batchSize || (current.Count > 0 && currentChars + blobText.Length > MaxBatchChars))
    {
        batches.Add(current);
        current = new List<SignalBlob>();
        currentChars = 0;
    }
    current.Add(blob);
    currentChars += blobText.Length;
}
if (current.Count > 0) batches.Add(current);

var effort = effortStr switch
{
    "low" => Effort.Low,
    "high" => Effort.High,
    _ => Effort.Medium,
};

logger.LogInformation("Phase C: {Batches} batches, {Workers} workers, effort={Effort}",
    batches.Count, workers, effortStr);

var failed = new ConcurrentBag<int>();

await Parallel.ForEachAsync(
    batches.Select((batch, i) => (Index: i, Batch: batch)),
    new ParallelOptions { MaxDegreeOfParallelism = workers },
    async (item, ct) =>
    {
        try
        {
            var entities = await CallHaikuAsync(client, item.Batch, effort, supportsOutputConfig, ct);
            var path = Path.Combine(batchesDir, $"batch-{item.Index:D4}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entities), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Batch {Index} failed", item.Index);
            failed.Add(item.Index);
        }
    });

logger.LogInformation("Phase C complete: {Total} batches, {Failed} failed", batches.Count, failed.Count);

// ── Phase D: Merge and write ─────────────────────────────────────────────────
logger.LogInformation("Phase D: merging results...");

var mergedByCanonical = new Dictionary<string, MergedEntity>(StringComparer.OrdinalIgnoreCase);
foreach (var batchFile in Directory.GetFiles(batchesDir, "batch-*.json").OrderBy(f => f))
{
    List<RawCandidateEntity>? entities;
    try
    {
        entities = JsonSerializer.Deserialize<List<RawCandidateEntity>>(
            await File.ReadAllTextAsync(batchFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException ex)
    {
        logger.LogWarning("Could not parse {File}: {Msg}", batchFile, ex.Message);
        continue;
    }
    if (entities is null) continue;

    foreach (var entity in entities)
    {
        if (string.IsNullOrWhiteSpace(entity.Canonical)) continue;
        if (!mergedByCanonical.TryGetValue(entity.Canonical, out var merged))
        {
            merged = new MergedEntity(entity.Canonical, entity.Category ?? "other");
            mergedByCanonical[entity.Canonical] = merged;
        }
        foreach (var alias in entity.Aliases ?? [])
            if (!string.IsNullOrWhiteSpace(alias))
                merged.Aliases.Add(alias);
        foreach (var docId in entity.DocIds ?? [])
            merged.DocIds.Add(docId);
    }
}

// Drop low-occurrence entities (keep all people and codenames regardless)
var survivors = mergedByCanonical.Values
    .Where(e => e.DocIds.Count >= 2 || e.Category is "people" or "codenames")
    .ToList();

// Enrich with sample_paths from DB
foreach (var entity in survivors)
{
    var ids = entity.DocIds.Take(3).ToList();
    if (ids.Count == 0) continue;
    var placeholders = string.Join(",", ids.Select((_, i) => $"@p{i}"));
    using var cmd = connection.CreateCommand();
    cmd.CommandText = $"SELECT relative_path FROM files WHERE id IN ({placeholders}) LIMIT 3";
    for (int i = 0; i < ids.Count; i++)
        cmd.Parameters.AddWithValue($"@p{i}", ids[i]);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        entity.SamplePaths.Add(reader.GetString(0));
}

// Sort: fixed category order, then doc count descending
var categoryOrder = new[] { "people", "products", "systems", "codenames", "methodologies", "roles", "yourcompany_specific", "other" };
var categoryRank = categoryOrder.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
survivors.Sort((a, b) =>
{
    var ai = categoryRank.TryGetValue(a.Category, out var av) ? av : 999;
    var bi = categoryRank.TryGetValue(b.Category, out var bv) ? bv : 999;
    if (ai != bi) return ai.CompareTo(bi);
    return b.DocIds.Count.CompareTo(a.DocIds.Count);
});

// Write candidates.json
var candidatesJson = survivors.Select(e => new
{
    canonical = e.Canonical,
    aliases = e.Aliases.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray(),
    category = e.Category,
    doc_count = e.DocIds.Count,
    sample_paths = e.SamplePaths.ToArray(),
    doc_ids = e.DocIds.OrderBy(x => x).ToArray(),
});
await File.WriteAllTextAsync(
    Path.Combine(outputDir, "candidates.json"),
    JsonSerializer.Serialize(candidatesJson, new JsonSerializerOptions { WriteIndented = true }));

// Write candidates.md (format matches Prompts.local/aliases.md exactly)
var md = new StringBuilder();
md.AppendLine("# Entity Expansion Map");
md.AppendLine();
md.AppendLine("Generated by SecondBrain.AliasMiner. Review and edit before promoting:");
md.AppendLine("  cp <output-dir>/candidates.md src/SecondBrain.Llm/Prompts.local/aliases.md");
md.AppendLine();
string? lastCategory = null;
foreach (var entity in survivors)
{
    if (entity.Category != lastCategory)
    {
        if (lastCategory is not null) md.AppendLine();
        md.AppendLine($"## {CategoryHeader(entity.Category)}");
        lastCategory = entity.Category;
    }
    var parts = new List<string> { entity.Canonical };
    parts.AddRange(entity.Aliases.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
    md.AppendLine($"- {string.Join(" ↔ ", parts)}");
}
await File.WriteAllTextAsync(Path.Combine(outputDir, "candidates.md"), md.ToString());

// Write run-summary.json
sw.Stop();
var runSummary = new
{
    total_docs = docRecords.Count,
    total_batches = batches.Count,
    failed_batches = failed.Count,
    total_entities = survivors.Count,
    elapsed_seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
};
await File.WriteAllTextAsync(
    Path.Combine(outputDir, "run-summary.json"),
    JsonSerializer.Serialize(runSummary, new JsonSerializerOptions { WriteIndented = true }));

logger.LogInformation("Done: {Entities} entities, {Elapsed}s. Review: {Md}",
    survivors.Count, Math.Round(sw.Elapsed.TotalSeconds, 1), Path.Combine(outputDir, "candidates.md"));
return 0;

// ── Local helpers ─────────────────────────────────────────────────────────────

static HashSet<string> ExtractCandidates(string filename, string text, IEnumerable<string> attendees)
{
    var combined = filename + " " + text;
    var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (Match m in Regex.Matches(combined, @"[A-Z][a-z]+(?: [A-Z][a-z]+){1,3}"))
        candidates.Add(m.Value);
    foreach (Match m in Regex.Matches(combined, @"\b[A-Z]{2,8}\b"))
        candidates.Add(m.Value);
    foreach (Match m in Regex.Matches(combined, @"[A-Za-z][A-Za-z0-9_-]*(?=\s*\()"))
        candidates.Add(m.Value);
    foreach (Match m in Regex.Matches(combined, @"(?<=\b(?:with|from|by|met|saw|told)\s+)[A-Z][a-z]+"))
        candidates.Add(m.Value);
    foreach (Match m in Regex.Matches(combined, @"\b([a-z][a-z0-9._-]+)@"))
        candidates.Add(m.Groups[1].Value);

    foreach (var a in attendees)
        if (!string.IsNullOrWhiteSpace(a))
            candidates.Add(a.Trim());

    return candidates;
}

static List<string> ParseAttendeesJson(string? json)
{
    var result = new List<string>();
    if (string.IsNullOrEmpty(json)) return result;
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.String)
        {
            var s = doc.RootElement.GetString();
            if (!string.IsNullOrEmpty(s)) result.Add(s);
        }
    }
    catch { }
    return result;
}

static string BuildBlobText(SignalBlob blob)
{
    var doc = blob.Doc;
    var sb = new StringBuilder();
    sb.AppendLine($"[doc {doc.Id}]");
    sb.AppendLine($"filename: {Path.GetFileName(doc.RelativePath)}");
    if (!string.IsNullOrEmpty(doc.SourceType))
        sb.AppendLine($"type: {doc.SourceType}");
    var attendees = ParseAttendeesJson(doc.AttendeesJson);
    if (attendees.Count > 0)
        sb.AppendLine($"attendees: {string.Join(", ", attendees)}");
    if (!string.IsNullOrEmpty(doc.Summary))
        sb.AppendLine($"summary: {doc.Summary[..Math.Min(500, doc.Summary.Length)]}");
    if (blob.Candidates.Count > 0)
        sb.AppendLine($"candidates: {string.Join(", ", blob.Candidates.Take(20))}");
    return sb.ToString();
}

static async Task<List<RawCandidateEntity>> CallHaikuAsync(
    IAnthropicClient client,
    List<SignalBlob> batch,
    Effort effort,
    bool supportsOutputConfig,
    CancellationToken ct)
{
    var userMsg = new StringBuilder();
    int seq = 1;
    foreach (var blob in batch)
    {
        userMsg.AppendLine($"=====BEGIN:DOC:{seq}=====");
        userMsg.AppendLine(BuildBlobText(blob));
        userMsg.AppendLine($"=====END:DOC:{seq}=====");
        seq++;
    }

    var systemBlocks = new List<TextBlockParam>
    {
        new() { Text = MinerPrompts.System, CacheControl = new CacheControlEphemeral() },
    };

    var createParams = new MessageCreateParams
    {
        Model = "claude-haiku-4-5",
        MaxTokens = 4096,
        System = new MessageCreateParamsSystem(systemBlocks),
        Messages =
        [
            new MessageParam { Role = Role.User, Content = userMsg.ToString() },
        ],
    };
    if (supportsOutputConfig)
        createParams = createParams with { OutputConfig = new OutputConfig { Effort = effort } };

    var response = await client.Messages.Create(createParams, ct);
    var text = response.Content
        .Where(b => b.TryPickText(out _))
        .Select(b => { b.TryPickText(out var t); return t!.Text; })
        .FirstOrDefault() ?? "";

    text = text.Trim();
    if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        text = text["```json".Length..].TrimStart();
    if (text.StartsWith("```"))
        text = text[3..].TrimStart();
    if (text.EndsWith("```"))
        text = text[..^3].TrimEnd();

    return JsonSerializer.Deserialize<List<RawCandidateEntity>>(text,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
}

static string CategoryHeader(string category) => category switch
{
    "people" => "People",
    "products" => "Products and systems",
    "systems" => "Systems",
    "codenames" => "Teams and codenames",
    "methodologies" => "Methodologies and artifacts",
    "roles" => "Roles and titles",
    "yourcompany_specific" => "YourCompany-specific",
    _ => "Other",
};


// ── Types ─────────────────────────────────────────────────────────────────────

record DocRecord(
    long Id,
    string RelativePath,
    string AbsolutePath,
    string SourceType,
    string? Summary,
    long SizeBytes,
    string? AttendeesJson);

class SignalBlob(DocRecord doc, HashSet<string> candidates)
{
    public DocRecord Doc { get; } = doc;
    public HashSet<string> Candidates { get; } = candidates;
}

class MergedEntity(string canonical, string category)
{
    public string Canonical { get; } = canonical;
    public string Category { get; } = category;
    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<long> DocIds { get; } = [];
    public List<string> SamplePaths { get; } = [];
}

class RawCandidateEntity
{
    [JsonPropertyName("canonical")] public string? Canonical { get; set; }
    [JsonPropertyName("aliases")]
    [JsonConverter(typeof(FlexibleStringArrayConverter))]
    public string[]? Aliases { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("doc_ids")] public long[]? DocIds { get; set; }
}

// Haiku occasionally emits numbers (years, version numbers) in the aliases array.
// This converter coerces any scalar token to its string representation.
class FlexibleStringArrayConverter : JsonConverter<string[]?>
{
    public override string[]? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) return [];

        var result = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var s = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString() ?? "",
                JsonTokenType.Number => reader.TryGetInt64(out var n)
                    ? n.ToString()
                    : reader.GetDouble().ToString(),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                _ => "",
            };
            if (!string.IsNullOrWhiteSpace(s))
                result.Add(s);
        }
        return result.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, string[]? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

static class MinerPrompts
{
    public const string System = """
        You are an entity extractor for a personal knowledge corpus alias map. You
        will receive a batch of document signal blobs. Each blob has a filename,
        source type, attendees list, summary text, and a `candidates:` line listing
        regex-extracted tokens that are likely (but not guaranteed) to be entities.

        For the batch, return a JSON array of canonical entities with their alias
        variants and category. Use the `candidates:` line as a hint — promote tokens
        that look like real entities, drop tokens that are noise (random
        capitalization in transcripts, common English words that happened to be
        capitalized at sentence start, etc.). Also surface entities you see in the
        filename/summary/attendees that the candidates list missed.

        Rules:
        - Group surface forms of the same entity (acronym ↔ expansion ↔ first-name ↔
          email local-part ↔ known transcription corruptions).
        - Categories: people, products, systems, codenames, methodologies, roles,
          yourcompany_specific, other.
        - Skip generic English vocabulary (meeting, decision, plan, team, etc.).
        - Skip tokens that look like one-off transcription noise unless they're
          clearly a corrupted form of a real entity.
        - For each entity, propose plausible transcription corruptions ONLY when the
          source type is `transcript` and the entity has been observed in transcript
          contexts. Use phonetic similarity (Atlas → Atless, Bayer → Beyer).
        - For people, include first name + email local-part if either appears in
          attendees or summary.
        - Output a JSON array, no commentary, no surrounding prose, no markdown
          code fence. The first non-whitespace character of your response must be `[`.

        Format:
        [
          { "canonical": "AWS Atlas", "aliases": ["Atlas", "Atless"],
            "category": "products", "doc_ids": [12, 45, 78] },
          { "canonical": "Jane Public", "aliases": ["Jane", "jane.public"],
            "category": "people", "doc_ids": [12, 45] },
          ...
        ]

        If the batch contains no extractable entities, return an empty array: [].
        """;
}
