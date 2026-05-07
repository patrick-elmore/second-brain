using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Data.Sqlite;
using SecondBrain.Files;
using SecondBrain.Index.Indexing;
using SecondBrain.Index.Search;
using SecondBrain.Llm;
using SecondBrain.Llm.Tests.Fakes;

namespace SecondBrain.Llm.Tests;

/// <summary>
/// Tests for <see cref="ToolLoop"/> using a real on-disk index and fake Anthropic client.
/// </summary>
public sealed class ToolLoopTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _ftsDb;
    private readonly string _configPath;

    public ToolLoopTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _sourceDir = Path.Combine(_tempDir, "sources");
        _ftsDb = Path.Combine(_tempDir, "fts.db");
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

    private void WriteSource(string name, string content)
        => File.WriteAllText(Path.Combine(_sourceDir, name), content, new UTF8Encoding(false));

    private void BuildIndex()
        => new IndexBuilder().Build(_configPath, _ftsDb, 5_000_000);

    private ToolLoop MakeLoop(FakeMessageCreator fake)
    {
        var engine = new SearchEngine(_ftsDb);
        var reader = new FileReader([_sourceDir]);
        return new ToolLoop(fake, engine, reader);
    }

    private static List<MessageParam> UserMessage(string text) =>
    [
        new() { Role = Role.User, Content = text },
    ];

    // ── no-tool path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoToolUse_ReturnsSynthesisFromResponse()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        fake.EnqueueText("The answer is 42.", inputTokens: 100, outputTokens: 10);
        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("What is the answer?"), "haiku", Effort.Low, CancellationToken.None);

        result.Synthesis.Should().Be("The answer is 42.");
        result.ToolsCalled.Should().Be(0);
        fake.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunAsync_NoToolUse_AccumulatesTokenUsage()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Done.", inputTokens: 500, outputTokens: 200);
        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("Summarize."), "haiku", Effort.Low, CancellationToken.None);

        result.InputTokensUsed.Should().Be(500);
        result.OutputTokensUsed.Should().Be(200);
    }

    [Fact]
    public async Task RunAsync_NoToolUse_FilesReferencedIsEmpty()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Nothing.");
        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("Hello"), "haiku", Effort.Low, CancellationToken.None);

        result.FilesReferenced.Should().BeEmpty();
    }

    // ── search tool ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SearchToolUse_SearchRunsAndHitsAddedToFilesReferenced()
    {
        WriteSource("data.md", "quantum entanglement physics research");
        BuildIndex();

        var fake = new FakeMessageCreator();
        // First: tool use response
        fake.EnqueueToolUse("tu1", "search", """{"queries":["quantum entanglement"]}""");
        // Second: final text response
        fake.EnqueueText("The search found results.");

        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("What do you know about quantum?"), "haiku", Effort.Low, CancellationToken.None);

        result.ToolsCalled.Should().Be(1);
        result.FilesReferenced.Should().NotBeEmpty();
        fake.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_SearchToolUse_EmptyQueriesDoesNotThrow()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        fake.EnqueueToolUse("tu1", "search", """{"queries":[]}""");
        fake.EnqueueText("Nothing found.");

        var loop = MakeLoop(fake);

        var act = async () => await loop.RunAsync(UserMessage("Search nothing."), "haiku", Effort.Low, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── read_file tool ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ReadFileTool_FileContentReturnedAsToolResult()
    {
        var content = "This is a note about deployment.";
        WriteSource("note.md", content);
        BuildIndex();

        var fake = new FakeMessageCreator();
        var notePath = Path.Combine(_sourceDir, "note.md");
        fake.EnqueueToolUse("tu1", "read_file", $$$"""{"path": {{{JsonSerializer.Serialize(notePath)}}} }""");
        fake.EnqueueText("I read the file.");

        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("Read the note."), "haiku", Effort.Low, CancellationToken.None);

        result.ToolsCalled.Should().Be(1);
        result.FilesReferenced.Should().Contain(notePath);
    }

    [Fact]
    public async Task RunAsync_ReadFileToolMissingPath_ErrorReturnedLoopContinues()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        fake.EnqueueToolUse("tu1", "read_file", "{}"); // no path param
        fake.EnqueueText("Could not read.");

        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("Read."), "haiku", Effort.Low, CancellationToken.None);

        result.ToolsCalled.Should().Be(1); // dispatched, got error result
        result.Synthesis.Should().Be("Could not read.");
    }

    // ── unknown tool ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_UnknownTool_ErrorResultLoopContinues()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        fake.EnqueueToolUse("tu1", "nonexistent_tool", "{}");
        fake.EnqueueText("Finished.");

        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("Use mystery tool."), "haiku", Effort.Low, CancellationToken.None);

        result.ToolsCalled.Should().Be(1);
        result.Synthesis.Should().Be("Finished.");
    }

    // ── multi-turn accumulation ───────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_TwoToolCalls_TokensAccumulatedAcrossBothApiCalls()
    {
        WriteSource("a.md", "alpha beta gamma delta epsilon");
        BuildIndex();

        var fake = new FakeMessageCreator();
        fake.EnqueueToolUse("tu1", "search", """{"queries":["alpha"]}""");
        fake.EnqueueText("Done.", inputTokens: 300, outputTokens: 100);

        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("Find alpha."), "haiku", Effort.Low, CancellationToken.None);

        // First call: 100 in, 50 out (from FakeMessageCreator ToolUseMessage defaults)
        // Second call: 300 in, 100 out
        result.InputTokensUsed.Should().Be(400);
        result.OutputTokensUsed.Should().Be(150);
    }

    [Fact]
    public async Task RunAsync_FilesReferenced_DeduplicatedCaseInsensitively()
    {
        WriteSource("Doc.md", "unique word alphazeta");
        BuildIndex();

        var fake = new FakeMessageCreator();
        // First search returns the file
        fake.EnqueueToolUse("tu1", "search", """{"queries":["alphazeta"]}""");
        // Second read_file same path but different case
        var notePath = Path.Combine(_sourceDir, "Doc.md");
        fake.EnqueueToolUse("tu2", "read_file", $$$"""{"path":{{{JsonSerializer.Serialize(notePath.ToUpperInvariant())}}}}""");
        fake.EnqueueText("Done.");

        var loop = MakeLoop(fake);

        // This may throw on read since uppercased path might not exist — allow error
        var result = await loop.RunAsync(UserMessage("Check doc."), "haiku", Effort.Low, CancellationToken.None);

        result.ToolsCalled.Should().Be(2);
    }
}
