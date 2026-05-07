using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SecondBrain.Files;
using SecondBrain.Files.Models;
using SecondBrain.Index.Search;

namespace SecondBrain.Index.Indexing;

/// <summary>
/// Incremental update of the FTS index. Diffs the on-disk source folders against
/// what's already in <c>files</c> and applies adds/modifies/removes in a single
/// transaction. Falls back to a full rebuild if the database is missing or the
/// schema has not been initialised.
/// </summary>
public sealed class IndexUpdater
{
    private readonly SourceConfigLoader _configLoader = new();
    private readonly SourceFolderScanner _scanner = new();
    private readonly FrontmatterParser _frontmatterParser = new();
    private readonly SchemaManager _schemaManager = new();

    public IndexUpdateSummary Update(string sourcesConfigPath, string dbPath, int maxBytes)
    {
        var sw = Stopwatch.StartNew();

        var folders = _configLoader.Load(sourcesConfigPath);
        var allowedRoots = folders.Select(f => f.AbsolutePath).ToList();
        var fileReader = new FileReader(allowedRoots);

        // No DB or no schema → delegate to a full rebuild. Same observable result;
        // simpler than maintaining two schema-creation paths.
        if (!File.Exists(dbPath) || !HasFilesTable(dbPath))
        {
            var builder = new IndexBuilder();
            var summary = builder.Build(sourcesConfigPath, dbPath, maxBytes);
            return new IndexUpdateSummary(
                Added: summary.IndexedCount,
                Modified: 0,
                Removed: 0,
                Unchanged: 0,
                Skipped: summary.SkippedCount,
                FullRebuild: true,
                Elapsed: summary.Elapsed,
                DbPath: summary.DbPath);
        }

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        // Make sure WAL is on so concurrent readers don't get blocked.
        using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
            pragmaCmd.ExecuteNonQuery();
        }

        _schemaManager.EnsureFtsSchema(conn);

        var existing = LoadExistingFiles(conn);
        var (toAdd, toModify, unchanged, seenPaths) = ScanAndDiff(folders, maxBytes, existing);
        var toRemove = existing
            .Where(kvp => !seenPaths.Contains(kvp.Key))
            .Select(kvp => kvp.Value.Id)
            .ToList();

        int added = 0, modified = 0, removed = 0, skipped = 0;
        var indexedAt = DateTime.UtcNow.ToString("o");

        using var txn = conn.BeginTransaction();
        try
        {
            using var deleteCmd = BuildDeleteCommand(conn, txn);
            using var insertFiles = BuildInsertFilesCommand(conn, txn);
            using var insertFts = BuildInsertFtsCommand(conn, txn);
            using var lastIdCmd = BuildLastInsertCommand(conn, txn);

            foreach (var id in toRemove)
            {
                deleteCmd.Parameters["@id"].Value = id;
                deleteCmd.ExecuteNonQuery();
                removed++;
            }

            foreach (var (file, existingId) in toModify)
            {
                deleteCmd.Parameters["@id"].Value = existingId;
                deleteCmd.ExecuteNonQuery();

                if (TryInsertFile(file, fileReader, indexedAt, insertFiles, insertFts, lastIdCmd))
                    modified++;
                else
                    skipped++;
            }

            foreach (var file in toAdd)
            {
                if (TryInsertFile(file, fileReader, indexedAt, insertFiles, insertFts, lastIdCmd))
                    added++;
                else
                    skipped++;
            }

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }

        sw.Stop();
        return new IndexUpdateSummary(added, modified, removed, unchanged, skipped, FullRebuild: false, sw.Elapsed, dbPath);
    }

    private static Dictionary<string, (long Id, double Mtime)> LoadExistingFiles(SqliteConnection conn)
    {
        var existing = new Dictionary<string, (long Id, double Mtime)>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, absolute_path, mtime FROM files";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            existing[reader.GetString(1)] = (reader.GetInt64(0), reader.GetDouble(2));
        return existing;
    }

    private (List<SourceFile> ToAdd, List<(SourceFile File, long ExistingId)> ToModify, int Unchanged, HashSet<string> SeenPaths)
        ScanAndDiff(
            IReadOnlyList<SourceFolder> folders,
            int maxBytes,
            Dictionary<string, (long Id, double Mtime)> existing)
    {
        var toAdd = new List<SourceFile>();
        var toModify = new List<(SourceFile File, long ExistingId)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unchanged = 0;

        foreach (var folder in folders)
        {
            foreach (var file in _scanner.Scan(folder, maxBytes))
            {
                seen.Add(file.AbsolutePath);
                if (existing.TryGetValue(file.AbsolutePath, out var found))
                {
                    if (file.MTime > found.Mtime)
                        toModify.Add((file, found.Id));
                    else
                        unchanged++;
                }
                else
                {
                    toAdd.Add(file);
                }
            }
        }

        return (toAdd, toModify, unchanged, seen);
    }

    private bool TryInsertFile(
        SourceFile file,
        FileReader reader,
        string indexedAt,
        SqliteCommand insertFiles,
        SqliteCommand insertFts,
        SqliteCommand lastIdCmd)
    {
        string content;
        try { content = reader.Read(file.AbsolutePath); }
        catch (InvalidDataException) { return false; }
        catch (Exception) { return false; }

        var fm = _frontmatterParser.Parse(content);
        var metadataJson = fm.Metadata.HasValue ? fm.Metadata.Value.GetRawText() : null;

        insertFiles.Parameters["@sfid"].Value = file.SourceFolderId;
        insertFiles.Parameters["@abspath"].Value = file.AbsolutePath;
        insertFiles.Parameters["@relpath"].Value = file.RelativePath;
        insertFiles.Parameters["@size"].Value = file.SizeBytes;
        insertFiles.Parameters["@mtime"].Value = file.MTime;
        insertFiles.Parameters["@indexed_at"].Value = indexedAt;
        insertFiles.Parameters["@source_type"].Value = (object?)fm.SourceType ?? DBNull.Value;
        insertFiles.Parameters["@metadata"].Value = (object?)metadataJson ?? DBNull.Value;
        insertFiles.ExecuteNonQuery();

        var rowId = (long)lastIdCmd.ExecuteScalar()!;

        insertFts.Parameters["@rowid"].Value = rowId;
        insertFts.Parameters["@path"].Value = file.RelativePath;
        insertFts.Parameters["@content"].Value = content;
        insertFts.ExecuteNonQuery();

        return true;
    }

    private static SqliteCommand BuildDeleteCommand(SqliteConnection conn, SqliteTransaction txn)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = txn;
        // Delete from both tables. files_fts shares rowid with files.id.
        cmd.CommandText = """
            DELETE FROM files_fts WHERE rowid = @id;
            DELETE FROM files     WHERE id    = @id;
            """;
        cmd.Parameters.Add("@id", SqliteType.Integer);
        return cmd;
    }

    private static SqliteCommand BuildInsertFilesCommand(SqliteConnection conn, SqliteTransaction txn)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = """
            INSERT INTO files
                (source_folder_id, absolute_path, relative_path, size_bytes, mtime, indexed_at, source_type, metadata)
            VALUES
                (@sfid, @abspath, @relpath, @size, @mtime, @indexed_at, @source_type, @metadata)
            """;
        cmd.Parameters.Add("@sfid", SqliteType.Text);
        cmd.Parameters.Add("@abspath", SqliteType.Text);
        cmd.Parameters.Add("@relpath", SqliteType.Text);
        cmd.Parameters.Add("@size", SqliteType.Integer);
        cmd.Parameters.Add("@mtime", SqliteType.Real);
        cmd.Parameters.Add("@indexed_at", SqliteType.Text);
        cmd.Parameters.Add("@source_type", SqliteType.Text);
        cmd.Parameters.Add("@metadata", SqliteType.Text);
        return cmd;
    }

    private static SqliteCommand BuildInsertFtsCommand(SqliteConnection conn, SqliteTransaction txn)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = "INSERT INTO files_fts(rowid, path, content, summary) VALUES (@rowid, @path, @content, '')";
        cmd.Parameters.Add("@rowid", SqliteType.Integer);
        cmd.Parameters.Add("@path", SqliteType.Text);
        cmd.Parameters.Add("@content", SqliteType.Text);
        return cmd;
    }

    private static SqliteCommand BuildLastInsertCommand(SqliteConnection conn, SqliteTransaction txn)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = "SELECT last_insert_rowid()";
        return cmd;
    }

    private static bool HasFilesTable(string dbPath)
    {
        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='files'";
            using var reader = cmd.ExecuteReader();
            return reader.Read();
        }
        catch
        {
            return false;
        }
    }
}
