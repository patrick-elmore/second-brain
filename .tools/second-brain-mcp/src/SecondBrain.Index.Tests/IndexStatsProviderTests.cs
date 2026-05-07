using System.Text;
using Microsoft.Data.Sqlite;
using SecondBrain.Index.Indexing;

namespace SecondBrain.Index.Tests;

public sealed class IndexStatsProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _configPath;
    private readonly string _sourceDir;

    public IndexStatsProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _sourceDir = Path.Combine(_tempDir, "sources");
        _dbPath = Path.Combine(_tempDir, "fts.db");
        _configPath = Path.Combine(_tempDir, "sources.json");
        Directory.CreateDirectory(_sourceDir);
        File.WriteAllText(_configPath,
            $$"""[{"id":"test","path":"{{_sourceDir.Replace("\\", "\\\\")}}"}]""",
            Encoding.UTF8);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private void BuildIndex(params (string name, string content)[] files)
    {
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(_sourceDir, name), content, new UTF8Encoding(false));
        new IndexBuilder().Build(_configPath, _dbPath, 5_000_000);
    }

    [Fact]
    public void Snapshot_DbDoesNotExist_ReturnsFalseExists()
    {
        var provider = new IndexStatsProvider(Path.Combine(_tempDir, "nonexistent.db"));

        var snap = provider.Snapshot();

        snap.Exists.Should().BeFalse();
        snap.FileCount.Should().Be(0);
        snap.TotalIndexedBytes.Should().Be(0);
        snap.LastIndexedAt.Should().BeNull();
        snap.DbFileSizeBytes.Should().Be(0);
    }

    [Fact]
    public void Snapshot_EmptyIndex_ExistsButZeroCounts()
    {
        BuildIndex(); // no files → empty index

        var provider = new IndexStatsProvider(_dbPath);
        var snap = provider.Snapshot();

        snap.Exists.Should().BeTrue();
        snap.FileCount.Should().Be(0);
        snap.TotalIndexedBytes.Should().Be(0);
        snap.LastIndexedAt.Should().BeNull();
        snap.DbFileSizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Snapshot_WithFiles_ReturnsCorrectCounts()
    {
        BuildIndex(("a.md", "Hello world"), ("b.md", "Another file"));

        var snap = new IndexStatsProvider(_dbPath).Snapshot();

        snap.FileCount.Should().Be(2);
        snap.TotalIndexedBytes.Should().BeGreaterThan(0);
        snap.LastIndexedAt.Should().NotBeNull();
    }

    [Fact]
    public void Snapshot_SummarizedCount_OnlyCountsNonNullSummaries()
    {
        BuildIndex(("a.md", "content a"), ("b.md", "content b"));

        // Manually set summary on one row
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE files SET summary = 'my summary' WHERE relative_path = 'a.md'";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            conn.Close();
            SqliteConnection.ClearAllPools();
        }

        var snap = new IndexStatsProvider(_dbPath).Snapshot();

        snap.SummarizedCount.Should().Be(1);
    }

    [Fact]
    public void Snapshot_BySourceFolder_GroupsByFolderId()
    {
        BuildIndex(("note1.md", "A"), ("note2.md", "B"));

        var snap = new IndexStatsProvider(_dbPath).Snapshot();

        snap.BySourceFolder.Should().HaveCount(1);
        snap.BySourceFolder[0].Key.Should().Be("test");
        snap.BySourceFolder[0].Count.Should().Be(2);
    }

    [Fact]
    public void Snapshot_BySourceType_NullTypeGroupedAsNone()
    {
        // Files without frontmatter have null source_type
        BuildIndex(("plain.md", "No frontmatter here"));

        var snap = new IndexStatsProvider(_dbPath).Snapshot();

        snap.BySourceType.Should().Contain(b => b.Key == "(none)");
    }

    [Fact]
    public void Snapshot_BySourceType_OrderedByCountDesc()
    {
        // One transcript, two notes (via frontmatter type inference from title)
        BuildIndex(
            ("2024-01-01 Transcript.md", "---\ntype: transcript\n---\nContent"),
            ("note1.md", "---\ntype: note\n---\nA"),
            ("note2.md", "---\ntype: note\n---\nB"));

        var snap = new IndexStatsProvider(_dbPath).Snapshot();

        // note has count 2, transcript has count 1 — notes should be first
        snap.BySourceType.Should().NotBeEmpty();
        var noteIdx = snap.BySourceType.ToList().FindIndex(b => b.Key == "note");
        var transcriptIdx = snap.BySourceType.ToList().FindIndex(b => b.Key == "transcript");
        if (noteIdx >= 0 && transcriptIdx >= 0)
            noteIdx.Should().BeLessThan(transcriptIdx);
    }

    [Fact]
    public void Snapshot_NotAnSqliteFile_ReturnsFalseExists()
    {
        // Write garbage to the DB path — provider should catch and return empty
        File.WriteAllText(_dbPath, "not a database");

        var snap = new IndexStatsProvider(_dbPath).Snapshot();

        snap.Exists.Should().BeFalse();
    }

    [Fact]
    public void Snapshot_DbFileSizeBytes_MatchesActualFileSize()
    {
        BuildIndex(("a.md", "content"));

        var snap = new IndexStatsProvider(_dbPath).Snapshot();

        snap.DbFileSizeBytes.Should().Be(new FileInfo(_dbPath).Length);
    }
}
