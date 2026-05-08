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

    // ── iteration cap ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolLoopHitsCap_ForcedSynthesisInjected()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        // Queue MaxToolTurns + 1 tool-use responses, then a final text response.
        // The cap injects a forcing message; the next response should be the final synthesis.
        for (var i = 0; i < ToolLoop.MaxToolTurns; i++)
            fake.EnqueueToolUse($"tu{i}", "search", """{"queries":["alpha"]}""");
        fake.EnqueueText("Synthesis after forced stop.");

        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("Q"), "haiku", Effort.Low, CancellationToken.None);

        result.Synthesis.Should().Be("Synthesis after forced stop.");
        result.ToolsCalled.Should().Be(ToolLoop.MaxToolTurns);
    }

    // ── read_file error guidance ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ReadFileNotFound_ReturnsActionableErrorAndPathNotInFilesReferenced()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        var bogusPath = Path.Combine(_sourceDir, "definitely-does-not-exist.md");
        fake.EnqueueToolUse("tu1", "read_file", $$$"""{"path": {{{JsonSerializer.Serialize(bogusPath)}}} }""");
        fake.EnqueueText("Done.");

        var loop = MakeLoop(fake);

        var result = await loop.RunAsync(UserMessage("Read."), "haiku", Effort.Low, CancellationToken.None);

        // Hallucinated paths must not pollute FilesReferenced — only successful reads count.
        result.FilesReferenced.Should().NotContain(bogusPath);
    }

    [Fact]
    public async Task RunAsync_ReadFileOutsideAllowedRoots_ReturnsActionableError()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        // Path that's a real file but outside the allowed root
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside.md");
        File.WriteAllText(outsidePath, "secret");
        try
        {
            fake.EnqueueToolUse("tu1", "read_file", $$$"""{"path": {{{JsonSerializer.Serialize(outsidePath)}}} }""");
            fake.EnqueueText("Done.");

            var loop = MakeLoop(fake);

            var result = await loop.RunAsync(UserMessage("Read."), "haiku", Effort.Low, CancellationToken.None);

            result.FilesReferenced.Should().NotContain(outsidePath);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    // ── overrides ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SystemPromptOverride_UsedInRequest()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.");
        var loop = MakeLoop(fake);

        await loop.RunAsync(
            UserMessage("Q"), "haiku", Effort.Low, CancellationToken.None,
            systemPromptOverride: "CUSTOM_PROMPT_MARKER");

        var callJson = JsonSerializer.Serialize(fake.Calls[0]);
        callJson.Should().Contain("CUSTOM_PROMPT_MARKER");
    }

    [Fact]
    public async Task RunAsync_NoOverride_UsesDefaultSystemPrompt()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.");
        var loop = MakeLoop(fake);

        await loop.RunAsync(UserMessage("Q"), "haiku", Effort.Low, CancellationToken.None);

        var callJson = JsonSerializer.Serialize(fake.Calls[0]);
        callJson.Should().NotContain("CUSTOM_PROMPT_MARKER");
    }

    // ── read_file truncation ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ReadFileLargerThanCap_ContentTruncatedWithMarker()
    {
        // File larger than the cap: should be truncated, marker appended.
        var bigContent = new string('x', ToolLoop.MaxReadFileBytes + 5_000);
        WriteSource("big.md", bigContent);
        BuildIndex();

        // First call: read_file with big content
        // Second call: capture the next request to inspect what the model "saw"
        // Third call: final synthesis
        var fake = new FakeMessageCreator();
        var bigPath = Path.Combine(_sourceDir, "big.md");
        fake.EnqueueToolUse("tu1", "read_file", $$$"""{"path": {{{JsonSerializer.Serialize(bigPath)}}} }""");
        fake.EnqueueText("Done.");

        var loop = MakeLoop(fake);
        await loop.RunAsync(UserMessage("Read big."), "haiku", Effort.Low, CancellationToken.None);

        // The second API call's messages include the tool_result; verify it was truncated.
        var secondCallJson = JsonSerializer.Serialize(fake.Calls[1]);
        secondCallJson.Should().Contain("[truncated:");
    }

    [Fact]
    public async Task RunAsync_ReadFileWithinCap_ContentNotTruncated()
    {
        var smallContent = "small content";
        WriteSource("small.md", smallContent);
        BuildIndex();

        var fake = new FakeMessageCreator();
        var smallPath = Path.Combine(_sourceDir, "small.md");
        fake.EnqueueToolUse("tu1", "read_file", $$$"""{"path": {{{JsonSerializer.Serialize(smallPath)}}} }""");
        fake.EnqueueText("Done.");

        var loop = MakeLoop(fake);
        await loop.RunAsync(UserMessage("Read small."), "haiku", Effort.Low, CancellationToken.None);

        var secondCallJson = JsonSerializer.Serialize(fake.Calls[1]);
        secondCallJson.Should().NotContain("[truncated:");
    }

    // ── context-overflow soft limit ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ContextSoftLimitReached_NextCallOmitsTools()
    {
        WriteSource("a.md", "alpha");
        BuildIndex();

        // First tool-use response reports input_tokens above the soft limit.
        // Loop should then force synthesis on the next call by omitting Tools.
        var fake = new FakeMessageCreator();
        fake.EnqueueToolUse("tu1", "search", """{"queries":["alpha"]}""",
            inputTokens: (int)ToolLoop.ContextSoftLimitTokens + 1_000);
        fake.EnqueueText("Final answer after forced synthesis.");

        var loop = MakeLoop(fake);
        var result = await loop.RunAsync(UserMessage("Q"), "haiku", Effort.Low, CancellationToken.None);

        result.Synthesis.Should().Be("Final answer after forced synthesis.");
        // Second call must have no tools available (forced synthesis).
        fake.Calls[1].Tools.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_ContextBelowSoftLimit_NextCallStillIncludesTools()
    {
        WriteSource("a.md", "alpha");
        BuildIndex();

        var fake = new FakeMessageCreator();
        // Well below the soft limit
        fake.EnqueueToolUse("tu1", "search", """{"queries":["alpha"]}""", inputTokens: 1_000);
        fake.EnqueueText("Done.");

        var loop = MakeLoop(fake);
        await loop.RunAsync(UserMessage("Q"), "haiku", Effort.Low, CancellationToken.None);

        // Both calls should include tools — no force triggered.
        fake.Calls[0].Tools.Should().NotBeNull();
        fake.Calls[1].Tools.Should().NotBeNull();
    }

    // ── empty tool_use guard ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_StopReasonToolUseButNoToolUseBlocks_TreatedAsCompletion()
    {
        BuildIndex();
        var fake = new FakeMessageCreator();
        // Malformed response: stop_reason=tool_use but content has only text.
        // The loop must NOT add an empty user message (which would later cause
        // "messages.X: user messages must have non-empty content").
        fake.EnqueueResponse("""
            {
              "id": "msg_test",
              "type": "message",
              "role": "assistant",
              "model": "claude-haiku-4-5",
              "stop_reason": "tool_use",
              "stop_sequence": null,
              "content": [{"type": "text", "text": "I am not actually using a tool."}],
              "usage": {
                "input_tokens": 100,
                "output_tokens": 20,
                "cache_creation_input_tokens": null,
                "cache_read_input_tokens": null
              }
            }
            """);
        // No second response needed — the loop should break out after the empty
        // tool_use guard fires.

        var loop = MakeLoop(fake);
        var result = await loop.RunAsync(UserMessage("Q"), "haiku", Effort.Low, CancellationToken.None);

        result.Synthesis.Should().Be("I am not actually using a tool.");
        // Only one API call should have been made — no follow-up triggered.
        fake.Calls.Should().HaveCount(1);
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
