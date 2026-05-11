using System.Text.Json;
using Microsoft.Data.Sqlite;
using SecondBrain.Files;
using SecondBrain.Index.Indexing;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

// Subcommand dispatch
if (args[0] == "backfill-dates")
    return BackfillDates(args[1..]);

// Legacy positional interface: <sources-config> <fts-db> [max-bytes]
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

try
{
    var builder = new IndexBuilder();
    var summary = builder.Build(sourcesConfig, dbPath, maxBytes,
        frontmatterDateFolders: ReadFrontmatterDateFoldersFromConfig());

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

// ── backfill-dates subcommand ─────────────────────────────────────────────

int BackfillDates(string[] cmdArgs)
{
    var dbPath2 = ArgValue(cmdArgs, "--db") ?? DefaultDbPath();
    var configPath = ArgValue(cmdArgs, "--config") ?? DefaultConfigPath();
    var dryRun = cmdArgs.Contains("--dry-run");

    Console.WriteLine($"db       : {dbPath2}");
    Console.WriteLine($"config   : {configPath}");
    Console.WriteLine($"dry-run  : {dryRun}");
    Console.WriteLine();

    if (!File.Exists(dbPath2))
    {
        Console.Error.WriteLine($"db not found: {dbPath2}");
        return 1;
    }

    var frontmatterFolders = ReadFrontmatterDateFoldersFromConfig(configPath);
    var dateDeriver = new DateDeriver(frontmatterFolders);

    // Ensure new columns exist before reading/writing them
    var ensureConnStr = new SqliteConnectionStringBuilder
    {
        DataSource = dbPath2,
        Mode = SqliteOpenMode.ReadWrite,
    }.ToString();
    using (var ensureConn = new SqliteConnection(ensureConnStr))
    {
        ensureConn.Open();
        new SchemaManager().EnsureFtsSchema(ensureConn);
    }

    // Load all rows from the DB
    var rows = LoadAllRows(dbPath2);
    Console.WriteLine($"rows loaded : {rows.Count:N0}");

    int updated = 0;
    int missing = 0;
    int skipped = 0;

    if (!dryRun)
    {
        var writeConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath2,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        using var writeConn = new SqliteConnection(writeConnStr);
        writeConn.Open();

        using var updateCmd = writeConn.CreateCommand();
        updateCmd.CommandText = """
            UPDATE files
            SET effective_date   = @effective_date,
                file_created_at  = @file_created_at,
                file_modified_at = @file_modified_at,
                local_date       = @local_date
            WHERE id = @id
            """;
        updateCmd.Parameters.Add("@effective_date", SqliteType.Real);
        updateCmd.Parameters.Add("@file_created_at", SqliteType.Real);
        updateCmd.Parameters.Add("@file_modified_at", SqliteType.Real);
        updateCmd.Parameters.Add("@local_date", SqliteType.Text);
        updateCmd.Parameters.Add("@id", SqliteType.Integer);

        // Process in batches of 500 to avoid holding huge transactions
        const int batchSize = 500;
        for (var start = 0; start < rows.Count; start += batchSize)
        {
            var batch = rows.Skip(start).Take(batchSize).ToList();
            using var txn = writeConn.BeginTransaction();
            updateCmd.Transaction = txn;

            foreach (var (id, absolutePath, mtime) in batch)
            {
                if (!File.Exists(absolutePath))
                {
                    // File no longer on disk — leave columns NULL, log it
                    Console.WriteLine($"  [missing] {absolutePath}");
                    missing++;
                    continue;
                }

                FileInfo info;
                try { info = new FileInfo(absolutePath); }
                catch { missing++; continue; }

                var ctime = info.CreationTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;
                string content;
                try { content = File.ReadAllText(absolutePath); }
                catch { content = string.Empty; }

                var dateResult = dateDeriver.Derive(absolutePath, content, ctime);

                updateCmd.Parameters["@effective_date"].Value = (object?)dateResult.EffectiveDate ?? DBNull.Value;
                updateCmd.Parameters["@file_created_at"].Value = ctime;
                updateCmd.Parameters["@file_modified_at"].Value = mtime;
                updateCmd.Parameters["@local_date"].Value = dateResult.LocalDate;
                updateCmd.Parameters["@id"].Value = id;
                updateCmd.ExecuteNonQuery();
                updated++;
            }

            txn.Commit();

            var pct = Math.Min(100.0, (start + batch.Count) * 100.0 / rows.Count);
            Console.Write($"\r  {start + batch.Count:N0} / {rows.Count:N0}  ({pct:F1}%)  ");
        }

        Console.WriteLine();
    }
    else
    {
        // Dry-run: just count what would be done
        foreach (var (_, absolutePath, _) in rows)
        {
            if (!File.Exists(absolutePath))
                missing++;
            else
                skipped++; // would be updated but not in dry-run mode
        }
        updated = rows.Count - missing;
    }

    Console.WriteLine();
    Console.WriteLine("=== summary ===");
    Console.WriteLine($"  rows examined : {rows.Count:N0}");
    Console.WriteLine($"  updated       : {updated:N0}{(dryRun ? "  (dry-run, nothing written)" : "")}");
    Console.WriteLine($"  file missing  : {missing:N0}  (no longer on disk; columns left NULL)");

    // Post-run verification: count rows with NULL local_date (should be 0 for present files)
    if (!dryRun)
    {
        var nullCount = CountNullLocalDate(dbPath2);
        Console.WriteLine($"  null local_date remaining : {nullCount:N0} (expect only missing files)");
    }

    return 0;
}

static List<(long Id, string AbsolutePath, double Mtime)> LoadAllRows(string dbPath)
{
    var connStr = new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString();

    using var conn = new SqliteConnection(connStr);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, absolute_path, mtime FROM files ORDER BY id";
    using var reader = cmd.ExecuteReader();
    var rows = new List<(long, string, double)>();
    while (reader.Read())
        rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2)));
    return rows;
}

static long CountNullLocalDate(string dbPath)
{
    var connStr = new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString();
    using var conn = new SqliteConnection(connStr);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM files WHERE local_date IS NULL";
    return (long)cmd.ExecuteScalar()!;
}

static int? ReadIndexMaxBytesFromConfig(string? configPath = null)
{
    configPath ??= Path.Combine(AppContext.BaseDirectory, "mcp_config.json");
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
    catch (JsonException) { }
    return null;
}

static List<string> ReadFrontmatterDateFoldersFromConfig(string? configPath = null)
{
    configPath ??= Path.Combine(AppContext.BaseDirectory, "mcp_config.json");
    if (!File.Exists(configPath))
        return DefaultFrontmatterFolders();

    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        if (doc.RootElement.TryGetProperty("second_brain", out var sb) &&
            sb.TryGetProperty("frontmatter_date_folders", out var fdf) &&
            fdf.ValueKind == JsonValueKind.Array)
        {
            var folders = new List<string>();
            foreach (var el in fdf.EnumerateArray())
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s))
                    folders.Add(s);
            }
            if (folders.Count > 0)
                return folders;
        }
    }
    catch (JsonException) { }

    return DefaultFrontmatterFolders();
}

// No built-in default paths — operators must configure frontmatter_date_folders
// in mcp_config.json. Returning an empty list causes DateDeriver to skip
// the frontmatter step and fall back to filepath regex + filesystem ctime.
static List<string> DefaultFrontmatterFolders() => [];

static string DefaultDbPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SecondBrainMcpServer", "index", "fts.db");

static string DefaultConfigPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SecondBrainMcpServer", "mcp_config.json");

static string? ArgValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  SecondBrain.IndexBuilder <sources-config-path> <fts-db-path> [max-bytes]");
    Console.WriteLine("  SecondBrain.IndexBuilder backfill-dates [--db <path>] [--config <mcp_config.json>] [--dry-run]");
}
