using Microsoft.Data.Sqlite;
using SecondBrain.Index.RequestHistory;
using RequestHistoryStore = SecondBrain.Index.RequestHistory.RequestHistory;

namespace SecondBrain.Index.Tests;

public sealed class RequestHistoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly RequestHistoryStore _history;

    public RequestHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "requests.db");
        _history = new RequestHistoryStore(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private static RequestRecord MakeRecord(string id = "abc12345", string tool = "search", string? synthesis = null) =>
        new(Id: id,
            Timestamp: DateTime.UtcNow,
            Tool: tool,
            Query: "test query",
            FiltersJson: "{}",
            ResultCount: 2,
            Synthesis: synthesis);

    private static IReadOnlyList<RequestFile> MakeFiles() =>
    [
        new(Rank: 0, AbsolutePath: "/data/a.md", RelativePath: "a.md", SourceFolderId: "src", Score: -21.5),
        new(Rank: 1, AbsolutePath: "/data/b.md", RelativePath: "b.md", SourceFolderId: "src", Score: -19.0),
    ];

    [Fact]
    public void DbFile_CreatedOnConstruction()
    {
        File.Exists(_dbPath).Should().BeTrue();
    }

    [Fact]
    public void PersistAndGet_RoundTrips()
    {
        var record = MakeRecord();
        var files = MakeFiles();
        _history.PersistRequest(record, files);

        var entity = _history.Get(record.Id, null);

        entity.Should().NotBeNull();
        entity!.RequestId.Should().Be(record.Id);
        entity.Tool.Should().Be("search");
        entity.Query.Should().Be("test query");
        entity.ResultCount.Should().Be(2);
        entity.Synthesis.Should().BeNull();
    }

    [Fact]
    public void Get_FilesReturnedInRankOrder()
    {
        _history.PersistRequest(MakeRecord(), MakeFiles());

        var entity = _history.Get("abc12345", null);

        entity!.Files.Should().HaveCount(2);
        entity.Files![0].Rank.Should().Be(0);
        entity.Files[0].AbsolutePath.Should().Be("/data/a.md");
        entity.Files[1].Rank.Should().Be(1);
    }

    [Fact]
    public void Get_AskRequest_SynthesisPreserved()
    {
        var record = MakeRecord("def67890", "ask", synthesis: "This is the synthesis answer.");
        _history.PersistRequest(record, []);

        var entity = _history.Get("def67890", null);

        entity!.Synthesis.Should().Be("This is the synthesis answer.");
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var entity = _history.Get("nonexistent", null);
        entity.Should().BeNull();
    }

    [Fact]
    public void Get_FieldProjection_OnlyReturnedFields()
    {
        _history.PersistRequest(MakeRecord(), MakeFiles());

        var entity = _history.Get("abc12345", ["query", "result_count"]);

        entity.Should().NotBeNull();
        entity!.RequestId.Should().Be("abc12345"); // always returned
        entity.Query.Should().Be("test query");
        entity.ResultCount.Should().Be(2);
        // Fields not requested should be null
        entity.Tool.Should().BeNull();
        entity.Timestamp.Should().BeNull();
        entity.FiltersJson.Should().BeNull();
        entity.Files.Should().BeNull();
    }

    [Fact]
    public void Get_FilesProjection_IncludesFiles()
    {
        _history.PersistRequest(MakeRecord(), MakeFiles());

        var entity = _history.Get("abc12345", ["files"]);

        entity!.Files.Should().HaveCount(2);
        entity.Query.Should().BeNull();
    }

    [Fact]
    public void PersistRequest_EmptyFiles_IsValid()
    {
        _history.PersistRequest(MakeRecord("empty01"), []);

        var entity = _history.Get("empty01", null);

        entity.Should().NotBeNull();
        entity!.Files.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void PersistMultiple_EachRetrievableIndependently()
    {
        _history.PersistRequest(MakeRecord("req0001", "search"), MakeFiles());
        _history.PersistRequest(MakeRecord("req0002", "ask", "synthesis text"), []);

        var e1 = _history.Get("req0001", null);
        var e2 = _history.Get("req0002", null);

        e1!.Tool.Should().Be("search");
        e2!.Tool.Should().Be("ask");
        e2.Synthesis.Should().Be("synthesis text");
    }

    [Fact]
    public void SchemaIdempotent_ReinstantiatingDoesNotFail()
    {
        _history.PersistRequest(MakeRecord(), MakeFiles());

        // Create a second instance pointing at the same DB
        var history2 = new RequestHistoryStore(_dbPath);
        var entity = history2.Get("abc12345", null);

        entity.Should().NotBeNull();
    }

    [Fact]
    public void Get_ScorePreserved()
    {
        _history.PersistRequest(MakeRecord(), MakeFiles());

        var entity = _history.Get("abc12345", ["files"]);

        entity!.Files![0].Score.Should().BeApproximately(-21.5, 0.001);
        entity.Files[1].Score.Should().BeApproximately(-19.0, 0.001);
    }
}
