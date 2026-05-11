using Microsoft.Data.Sqlite;
using SecondBrain.Index.Indexing;

namespace SecondBrain.Index.Tests;

public sealed class SchemaManagerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _tempDir;

    public SchemaManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private SqliteConnection OpenRw()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        conn.Open();
        return conn;
    }

    private List<string> GetTableNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private List<string> GetColumnNames(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(1)); // column name
        return cols;
    }

    [Fact]
    public void CreateFtsSchema_ProducesFilesAndFtsTables()
    {
        using var conn = OpenRw();
        var mgr = new SchemaManager();

        mgr.CreateFtsSchema(conn);

        var tables = GetTableNames(conn);
        tables.Should().Contain("files");
        // FTS5 tables appear as multiple entries; at minimum the content table
        tables.Should().Contain(t => t.StartsWith("files_fts"));
    }

    [Fact]
    public void CreateFtsSchema_FilesTableHasExpectedColumns()
    {
        using var conn = OpenRw();
        new SchemaManager().CreateFtsSchema(conn);

        var cols = GetColumnNames(conn, "files");

        cols.Should().Contain("id");
        cols.Should().Contain("source_folder_id");
        cols.Should().Contain("absolute_path");
        cols.Should().Contain("relative_path");
        cols.Should().Contain("size_bytes");
        cols.Should().Contain("mtime");
        cols.Should().Contain("indexed_at");
        cols.Should().Contain("source_type");
        cols.Should().Contain("metadata");
        cols.Should().Contain("summary");
        cols.Should().Contain("effective_date");
        cols.Should().Contain("file_created_at");
        cols.Should().Contain("file_modified_at");
        cols.Should().Contain("local_date");
    }

    [Fact]
    public void EnsureFtsSchema_AddsNewDateColumnsToExistingSchema()
    {
        // Simulate a pre-existing database that lacks the three new date columns.
        // Create the schema using raw SQL that matches the old schema shape.
        using var conn = OpenRw();
        using (var createOld = conn.CreateCommand())
        {
            createOld.CommandText = """
                CREATE TABLE files (
                    id                INTEGER PRIMARY KEY,
                    source_folder_id  TEXT NOT NULL,
                    absolute_path     TEXT NOT NULL UNIQUE,
                    relative_path     TEXT NOT NULL,
                    size_bytes        INTEGER NOT NULL,
                    mtime             REAL NOT NULL,
                    indexed_at        TEXT NOT NULL,
                    source_type       TEXT,
                    metadata          TEXT,
                    summary           TEXT
                );
                CREATE VIRTUAL TABLE files_fts USING fts5(path, content, summary, tokenize='porter unicode61');
                """;
            createOld.ExecuteNonQuery();
        }

        // Insert a row with the old schema
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO files (source_folder_id, absolute_path, relative_path, size_bytes, mtime, indexed_at) VALUES ('s','a','r',1,1.0,'now')";
            ins.ExecuteNonQuery();
        }

        // Calling EnsureFtsSchema should add the new columns without dropping data
        new SchemaManager().EnsureFtsSchema(conn);

        var cols = GetColumnNames(conn, "files");
        cols.Should().Contain("effective_date");
        cols.Should().Contain("file_created_at");
        cols.Should().Contain("file_modified_at");
        cols.Should().Contain("local_date");

        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM files";
        ((long)count.ExecuteScalar()!).Should().Be(1);
    }

    [Fact]
    public void CreateFtsSchema_DropsExistingDataOnRebuild()
    {
        using var conn = OpenRw();
        var mgr = new SchemaManager();
        mgr.CreateFtsSchema(conn);

        // Insert a row
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO files (source_folder_id, absolute_path, relative_path, size_bytes, mtime, indexed_at) VALUES ('s','a','r',1,1.0,'now')";
            ins.ExecuteNonQuery();
        }

        // Recreate — should drop and recreate
        mgr.CreateFtsSchema(conn);

        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM files";
        ((long)count.ExecuteScalar()!).Should().Be(0);
    }

    [Fact]
    public void EnsureFtsSchema_IsIdempotent_PreservesExistingData()
    {
        using var conn = OpenRw();
        var mgr = new SchemaManager();
        mgr.CreateFtsSchema(conn);

        // Insert a row
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO files (source_folder_id, absolute_path, relative_path, size_bytes, mtime, indexed_at) VALUES ('s','a','r',1,1.0,'now')";
            ins.ExecuteNonQuery();
        }

        // Ensure schema — should not drop data
        mgr.EnsureFtsSchema(conn);

        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM files";
        ((long)count.ExecuteScalar()!).Should().Be(1);
    }

    [Fact]
    public void EnsureFtsSchema_CalledTwice_DoesNotThrow()
    {
        using var conn = OpenRw();
        var mgr = new SchemaManager();

        var act = () =>
        {
            mgr.EnsureFtsSchema(conn);
            mgr.EnsureFtsSchema(conn);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureRequestsSchema_IsIdempotent()
    {
        using var conn = OpenRw();
        var mgr = new SchemaManager();

        var act = () =>
        {
            mgr.EnsureRequestsSchema(conn);
            mgr.EnsureRequestsSchema(conn);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureRequestsSchema_ProducesRequestsAndRequestFilesTable()
    {
        using var conn = OpenRw();
        new SchemaManager().EnsureRequestsSchema(conn);

        var tables = GetTableNames(conn);
        tables.Should().Contain("requests");
        tables.Should().Contain("request_files");
    }

    [Fact]
    public void EnsureRequestsSchema_RequestsTableHasExpectedColumns()
    {
        using var conn = OpenRw();
        new SchemaManager().EnsureRequestsSchema(conn);

        var cols = GetColumnNames(conn, "requests");
        cols.Should().Contain("id");
        cols.Should().Contain("timestamp");
        cols.Should().Contain("tool");
        cols.Should().Contain("query");
        cols.Should().Contain("filters_json");
        cols.Should().Contain("result_count");
        cols.Should().Contain("synthesis");
    }

    [Fact]
    public void FtsPorterStemmer_MatchesStemmedQuery()
    {
        // This verifies the tokenize='porter unicode61' setting is actually applied.
        using var conn = OpenRw();
        new SchemaManager().CreateFtsSchema(conn);

        // Insert a doc containing "running"
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO files (source_folder_id, absolute_path, relative_path, size_bytes, mtime, indexed_at) VALUES ('s','/a.md','a.md',10,1.0,'now')";
            ins.ExecuteNonQuery();
            ins.CommandText = "SELECT last_insert_rowid()";
            var rowId = (long)ins.ExecuteScalar()!;

            using var fts = conn.CreateCommand();
            fts.CommandText = "INSERT INTO files_fts(rowid, path, content, summary) VALUES (@id,'a.md','She was running fast','')";
            fts.Parameters.AddWithValue("@id", rowId);
            fts.ExecuteNonQuery();
        }

        // Search for "run" — porter stemmer should match "running"
        using var search = conn.CreateCommand();
        search.CommandText = "SELECT COUNT(*) FROM files_fts WHERE files_fts MATCH 'run'";
        ((long)search.ExecuteScalar()!).Should().Be(1);
    }
}
