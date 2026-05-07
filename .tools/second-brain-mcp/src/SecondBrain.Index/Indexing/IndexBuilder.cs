using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SecondBrain.Files;
using SecondBrain.Index.Search;

namespace SecondBrain.Index.Indexing;

public sealed class IndexBuilder
{
    private readonly SourceConfigLoader _configLoader = new();
    private readonly SourceFolderScanner _scanner = new();
    private readonly FrontmatterParser _frontmatterParser = new();
    private readonly FileReader _fileReader;

    public IndexBuilder()
    {
        // FileReader is initialized with no allowed roots here;
        // roots are set per-build from the loaded source folders.
        _fileReader = new FileReader([]);
    }

    public IndexBuildSummary Build(string sourcesConfigPath, string dbPath, int maxBytes)
    {
        var sw = Stopwatch.StartNew();

        var folders = _configLoader.Load(sourcesConfigPath);
        var allowedRoots = folders.Select(f => f.AbsolutePath).ToList();
        var fileReader = new FileReader(allowedRoots);

        // Full rebuild: release any pooled connections then delete existing fts.db
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var dir = Path.GetDirectoryName(dbPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        // Enable WAL mode for concurrent reader access
        using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
            pragmaCmd.ExecuteNonQuery();
        }

        var schemaManager = new SchemaManager();
        schemaManager.CreateFtsSchema(conn);

        int indexed = 0;
        int skipped = 0;

        using var txn = conn.BeginTransaction();
        try
        {
            using var insertFiles = conn.CreateCommand();
            insertFiles.Transaction = txn;
            insertFiles.CommandText = """
                INSERT INTO files
                    (source_folder_id, absolute_path, relative_path, size_bytes, mtime, indexed_at, source_type, metadata)
                VALUES
                    (@sfid, @abspath, @relpath, @size, @mtime, @indexed_at, @source_type, @metadata)
                """;
            insertFiles.Parameters.Add("@sfid", SqliteType.Text);
            insertFiles.Parameters.Add("@abspath", SqliteType.Text);
            insertFiles.Parameters.Add("@relpath", SqliteType.Text);
            insertFiles.Parameters.Add("@size", SqliteType.Integer);
            insertFiles.Parameters.Add("@mtime", SqliteType.Real);
            insertFiles.Parameters.Add("@indexed_at", SqliteType.Text);
            insertFiles.Parameters.Add("@source_type", SqliteType.Text);
            insertFiles.Parameters.Add("@metadata", SqliteType.Text);

            using var insertFts = conn.CreateCommand();
            insertFts.Transaction = txn;
            insertFts.CommandText = "INSERT INTO files_fts(rowid, path, content, summary) VALUES (@rowid, @path, @content, '')";
            insertFts.Parameters.Add("@rowid", SqliteType.Integer);
            insertFts.Parameters.Add("@path", SqliteType.Text);
            insertFts.Parameters.Add("@content", SqliteType.Text);

            using var lastId = conn.CreateCommand();
            lastId.Transaction = txn;
            lastId.CommandText = "SELECT last_insert_rowid()";

            var indexedAt = DateTime.UtcNow.ToString("o");

            foreach (var folder in folders)
            {
                foreach (var file in _scanner.Scan(folder, maxBytes))
                {
                    string content;
                    try
                    {
                        content = fileReader.Read(file.AbsolutePath);
                    }
                    catch (InvalidDataException)
                    {
                        // Binary file — skip
                        skipped++;
                        continue;
                    }
                    catch (Exception)
                    {
                        skipped++;
                        continue;
                    }

                    var fm = _frontmatterParser.Parse(content);
                    var metadataJson = fm.Metadata.HasValue
                        ? fm.Metadata.Value.GetRawText()
                        : null;

                    insertFiles.Parameters["@sfid"].Value = file.SourceFolderId;
                    insertFiles.Parameters["@abspath"].Value = file.AbsolutePath;
                    insertFiles.Parameters["@relpath"].Value = file.RelativePath;
                    insertFiles.Parameters["@size"].Value = file.SizeBytes;
                    insertFiles.Parameters["@mtime"].Value = file.MTime;
                    insertFiles.Parameters["@indexed_at"].Value = indexedAt;
                    insertFiles.Parameters["@source_type"].Value = (object?)fm.SourceType ?? DBNull.Value;
                    insertFiles.Parameters["@metadata"].Value = (object?)metadataJson ?? DBNull.Value;
                    insertFiles.ExecuteNonQuery();

                    var rowId = (long)lastId.ExecuteScalar()!;

                    insertFts.Parameters["@rowid"].Value = rowId;
                    insertFts.Parameters["@path"].Value = file.RelativePath;
                    insertFts.Parameters["@content"].Value = content;
                    insertFts.ExecuteNonQuery();

                    indexed++;
                }
            }

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }

        sw.Stop();
        return new IndexBuildSummary(indexed, skipped, sw.Elapsed, dbPath);
    }
}
