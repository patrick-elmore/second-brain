using System.Text.Json;
using SecondBrain.Index.Indexing;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: SecondBrain.IndexBuilder <sources-config-path> <fts-db-path> [max-bytes]");
    return 1;
}

var sourcesConfig = args[0];
var dbPath = args[1];
var maxBytes = args.Length >= 3 && int.TryParse(args[2], out var mb)
    ? mb
    : ReadIndexMaxBytesFromConfig() ?? 500_000;

static int? ReadIndexMaxBytesFromConfig()
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "mcp_config.json");
    if (!File.Exists(configPath))
        return null;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        if (doc.RootElement.TryGetProperty("second_brain", out var sb) &&
            sb.TryGetProperty("index_max_bytes", out var imb) &&
            imb.TryGetInt32(out var v))
        {
            return v;
        }
    }
    catch (JsonException)
    {
        // Fall through to default
    }
    return null;
}

try
{
    var builder = new IndexBuilder();
    var summary = builder.Build(sourcesConfig, dbPath, maxBytes);

    var output = new
    {
        indexed = summary.IndexedCount,
        skipped = summary.SkippedCount,
        elapsed_seconds = Math.Round(summary.Elapsed.TotalSeconds, 2),
        db_path = summary.DbPath,
    };

    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 2;
}
