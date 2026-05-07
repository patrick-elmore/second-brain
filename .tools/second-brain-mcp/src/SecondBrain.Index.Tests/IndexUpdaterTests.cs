using System.Text;
using Microsoft.Data.Sqlite;
using SecondBrain.Index.Indexing;
using SecondBrain.Index.Search;

namespace SecondBrain.Index.Tests;

public sealed class IndexUpdaterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _ftsDb;
    private readonly string _configPath;

    public IndexUpdaterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _sourceDir = Path.Combine(_tempDir, "sources");
        _ftsDb = Path.Combine(_tempDir, "fts.db");
        _configPath = Path.Combine(_tempDir, "sources.json");
        Directory.CreateDirectory(_sourceDir);
        File.WriteAllText(_configPath,
            $$"""[{"id":"src","path":"{{_sourceDir.Replace("\\", "\\\\")}}"}]""",
            Encoding.UTF8);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteFile(string name, string content)
        => File.WriteAllText(Path.Combine(_sourceDir, name), content, new UTF8Encoding(false));

    private void DeleteFile(string name)
        => File.Delete(Path.Combine(_sourceDir, name));

    private void TouchFile(string name, string newContent)
    {
        var path = Path.Combine(_sourceDir, name);
        File.WriteAllText(path, newContent, new UTF8Encoding(false));
        // Ensure mtime is newer than what was stored
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
    }

    private void BuildInitialIndex(params string[] fileNames)
    {
        foreach (var n in fileNames)
            if (!File.Exists(Path.Combine(_sourceDir, n)))
                WriteFile(n, $"Content of {n}");
        new IndexBuilder().Build(_configPath, _ftsDb, 5_000_000);
    }

    private IndexUpdateSummary Update() => new IndexUpdater().Update(_configPath, _ftsDb, 5_000_000);

    private long RowCount()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _ftsDb, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files";
            return (long)cmd.ExecuteScalar()!;
        }
        finally { conn.Close(); SqliteConnection.ClearAllPools(); }
    }

    private string? ReadFileContent(string relativePath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _ftsDb, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT content FROM files_fts JOIN files ON files.id = files_fts.rowid WHERE files.relative_path = @p";
            cmd.Parameters.AddWithValue("@p", relativePath);
            return cmd.ExecuteScalar() as string;
        }
        finally { conn.Close(); SqliteConnection.ClearAllPools(); }
    }

    private string? ReadIndexedAt(string relativePath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _ftsDb, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT indexed_at FROM files WHERE relative_path = @p";
            cmd.Parameters.AddWithValue("@p", relativePath);
            return cmd.ExecuteScalar() as string;
        }
        finally { conn.Close(); SqliteConnection.ClearAllPools(); }
    }

    // ── fallback-to-full-rebuild paths ────────────────────────────────────────

    [Fact]
    public void Update_NoDatabaseExists_FallsBackToFullRebuild()
    {
        WriteFile("a.md", "hello");
        WriteFile("b.md", "world");

        var summary = Update();

        summary.FullRebuild.Should().BeTrue();
        summary.Added.Should().Be(2);
        summary.Modified.Should().Be(0);
        summary.Removed.Should().Be(0);
        File.Exists(_ftsDb).Should().BeTrue();
    }

    [Fact]
    public void Update_DbExistsWithoutFilesTable_FallsBackToFullRebuild()
    {
        // Write a valid SQLite file but without the expected schema
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _ftsDb, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        conn.Open();
        conn.Close();
        SqliteConnection.ClearAllPools();

        WriteFile("a.md", "hello");

        var summary = Update();

        summary.FullRebuild.Should().BeTrue();
    }

    // ── incremental cases ─────────────────────────────────────────────────────

    [Fact]
    public void Update_NoChanges_ReturnsAllUnchanged()
    {
        WriteFile("a.md", "hello");
        BuildInitialIndex("a.md");

        var summary = Update();

        summary.FullRebuild.Should().BeFalse();
        summary.Added.Should().Be(0);
        summary.Modified.Should().Be(0);
        summary.Removed.Should().Be(0);
        summary.Unchanged.Should().Be(1);
    }

    [Fact]
    public void Update_NewFile_CountedAsAdded()
    {
        BuildInitialIndex("existing.md");
        WriteFile("new.md", "brand new content");

        var summary = Update();

        summary.Added.Should().Be(1);
        summary.Unchanged.Should().Be(1);
        RowCount().Should().Be(2);
    }

    [Fact]
    public void Update_NewFile_IsSearchableAfterUpdate()
    {
        BuildInitialIndex("existing.md");
        WriteFile("new.md", "quantum entanglement discovery");

        Update();

        var result = new SearchEngine(_ftsDb).Search(new SearchParams(Query: "quantum entanglement"));
        result.Hits.Should().ContainSingle(h => h.RelativePath == "new.md");
    }

    [Fact]
    public void Update_ModifiedFile_CountedAsModified()
    {
        WriteFile("doc.md", "original content");
        BuildInitialIndex("doc.md");
        TouchFile("doc.md", "updated content");

        var summary = Update();

        summary.Modified.Should().Be(1);
        summary.Added.Should().Be(0);
        summary.Unchanged.Should().Be(0);
    }

    [Fact]
    public void Update_ModifiedFile_FtsReflectsNewContent()
    {
        WriteFile("doc.md", "original banana");
        BuildInitialIndex("doc.md");
        TouchFile("doc.md", "updated pineapple content");

        Update();

        var engine = new SearchEngine(_ftsDb);
        engine.Search(new SearchParams(Query: "banana")).Hits.Should().BeEmpty();
        engine.Search(new SearchParams(Query: "pineapple")).Hits.Should().HaveCount(1);
    }

    [Fact]
    public void Update_DeletedFile_CountedAsRemoved()
    {
        WriteFile("keep.md", "stays");
        WriteFile("gone.md", "will be deleted");
        BuildInitialIndex("keep.md", "gone.md");
        DeleteFile("gone.md");

        var summary = Update();

        summary.Removed.Should().Be(1);
        summary.Unchanged.Should().Be(1);
        RowCount().Should().Be(1);
    }

    [Fact]
    public void Update_DeletedFile_NoLongerSearchable()
    {
        WriteFile("keep.md", "stays here");
        WriteFile("gone.md", "unicorn content");
        BuildInitialIndex("keep.md", "gone.md");
        DeleteFile("gone.md");

        Update();

        var result = new SearchEngine(_ftsDb).Search(new SearchParams(Query: "unicorn"));
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Update_MixedChanges_AllCountsCorrect()
    {
        WriteFile("unchanged.md", "same");
        WriteFile("to-modify.md", "old");
        WriteFile("to-delete.md", "bye");
        BuildInitialIndex("unchanged.md", "to-modify.md", "to-delete.md");

        TouchFile("to-modify.md", "new content");
        DeleteFile("to-delete.md");
        WriteFile("added.md", "fresh");

        var summary = Update();

        summary.Added.Should().Be(1);
        summary.Modified.Should().Be(1);
        summary.Removed.Should().Be(1);
        summary.Unchanged.Should().Be(1);
        summary.FullRebuild.Should().BeFalse();
        RowCount().Should().Be(3); // unchanged + modified + added
    }

    [Fact]
    public void Update_UnchangedFile_IndexedAtPreserved()
    {
        WriteFile("stable.md", "content");
        BuildInitialIndex("stable.md");
        var indexedAtBefore = ReadIndexedAt("stable.md");

        Update(); // no changes

        ReadIndexedAt("stable.md").Should().Be(indexedAtBefore);
    }

    [Fact]
    public void Update_BinaryFile_CountedAsSkipped()
    {
        BuildInitialIndex(); // empty initial
        File.WriteAllBytes(Path.Combine(_sourceDir, "binary.bin"), [0xFF, 0xFE, 0x00, 0xD8, 0xC0, 0x80]);

        var summary = Update();

        summary.Skipped.Should().Be(1);
        summary.Added.Should().Be(0);
        RowCount().Should().Be(0);
    }

    [Fact]
    public void Update_ElapsedTimeIsSet()
    {
        BuildInitialIndex();

        var summary = Update();

        summary.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Update_DbPathReturnedInSummary()
    {
        BuildInitialIndex();

        var summary = Update();

        summary.DbPath.Should().Be(_ftsDb);
    }
}
