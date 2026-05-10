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

public sealed class ClaudeSessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _ftsDb;
    private readonly string _configPath;
    private readonly string _statePath;

    public ClaudeSessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _sourceDir = Path.Combine(_tempDir, "sources");
        _ftsDb = Path.Combine(_tempDir, "fts.db");
        _configPath = Path.Combine(_tempDir, "sources.json");
        _statePath = Path.Combine(_tempDir, "state.json");
        Directory.CreateDirectory(_sourceDir);
        File.WriteAllText(_configPath,
            $$"""[{"id":"test","path":"{{_sourceDir.Replace("\\", "\\\\")}}"}]""",
            Encoding.UTF8);
        BuildIndex(); // empty index is enough for session tests
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private void BuildIndex()
        => new IndexBuilder().Build(_configPath, _ftsDb, 5_000_000);

    private ClaudeSession MakeSession(
        FakeMessageCreator fake,
        StatePersistence? persistence = null,
        long compactThreshold = 999_999_999)
    {
        var engine = new SearchEngine(_ftsDb);
        var reader = new FileReader([_sourceDir]);
        var compactor = new Compactor(fake, "claude-sonnet-4-6");
        return new ClaudeSession(
            client: fake,
            searchEngine: engine,
            fileReader: reader,
            compactor: compactor,
            statePersistence: persistence,
            defaultModel: "claude-haiku-4-5",
            escalationModel: "claude-sonnet-4-6",
            compactThresholdTokens: compactThreshold,
            persistEveryNMessages: 5);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsMessagesAndTokenCount()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Hello back.");
        var session = MakeSession(fake);

        // No API call made — just reset directly
        session.Reset();
        var info = session.Info();

        info.Messages.Should().Be(0);
        info.ApproximateTokens.Should().Be(0);
        info.LastCompacted.Should().BeNull();
        info.LastActivity.Should().BeNull();
    }

    [Fact]
    public void Reset_WithPersistence_WritesStateFile()
    {
        var fake = new FakeMessageCreator();
        var persistence = new StatePersistence(_statePath);
        var session = MakeSession(fake, persistence);

        session.Reset();

        File.Exists(_statePath).Should().BeTrue();
    }

    // ── Info ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Info_ReturnsDefaultModel()
    {
        var fake = new FakeMessageCreator();
        var session = MakeSession(fake);

        session.Info().CurrentDefaultModel.Should().Be("claude-haiku-4-5");
    }

    [Fact]
    public void Info_BeforeAnyActivity_LastActivityIsNull()
    {
        var fake = new FakeMessageCreator();
        var session = MakeSession(fake);

        session.Info().LastActivity.Should().BeNull();
    }

    // ── AskAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_SetsLastActivity()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.");
        var session = MakeSession(fake);

        await session.AskAsync("Question?", null, "low", CancellationToken.None);

        session.Info().LastActivity.Should().NotBeNull();
        session.Info().LastActivity.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AskAsync_ReturnsSynthesisFromToolLoop()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("The synthesis is here.");
        var session = MakeSession(fake);

        var result = await session.AskAsync("What?", null, "low", CancellationToken.None);

        result.Synthesis.Should().Be("The synthesis is here.");
    }

    [Fact]
    public async Task AskAsync_IncrementsApproximateTokens()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.", inputTokens: 500, outputTokens: 100);
        var session = MakeSession(fake);

        await session.AskAsync("Question?", null, "low", CancellationToken.None);

        session.Info().ApproximateTokens.Should().Be(600);
    }

    [Fact]
    public async Task AskAsync_WithPersistence_WritesStateAfterAsk()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.");
        var persistence = new StatePersistence(_statePath);
        var session = MakeSession(fake, persistence);

        await session.AskAsync("Question?", null, "low", CancellationToken.None);

        File.Exists(_statePath).Should().BeTrue();
    }

    [Fact]
    public async Task AskAsync_RequestIdIsEightHexChars()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.");
        var session = MakeSession(fake);

        var result = await session.AskAsync("Q?", null, "low", CancellationToken.None);

        result.RequestId.Should().HaveLength(8);
        result.RequestId.Should().MatchRegex("^[0-9a-f]{8}$");
    }

    [Fact]
    public async Task AskAsync_TokensAboveThreshold_AutoCompactionFires()
    {
        var fake = new FakeMessageCreator();
        // Ask #1 returns tokens that will exceed threshold
        fake.EnqueueText("First reply.", inputTokens: 50, outputTokens: 50);
        // Compaction call
        fake.EnqueueText("Compacted summary.");
        // Ask #2 after compaction
        fake.EnqueueText("Second reply.");

        // Very low threshold so first ask's 100 tokens triggers compaction on second ask
        var session = MakeSession(fake, compactThreshold: 1);

        await session.AskAsync("First question.", null, "low", CancellationToken.None);
        await session.AskAsync("Second question.", null, "low", CancellationToken.None);

        // Three API calls: first ask, compaction, second ask
        fake.Calls.Should().HaveCount(3);
    }

    // ── CompactAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_EmptyMessages_ReturnsZeroCountWithoutApiCall()
    {
        var fake = new FakeMessageCreator();
        var session = MakeSession(fake);

        var result = await session.CompactAsync(null, CancellationToken.None);

        result.MessagesBefore.Should().Be(0);
        result.MessagesAfter.Should().Be(0);
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CompactAsync_WithMessages_ReplacesWithSingleSummaryMessage()
    {
        var fake = new FakeMessageCreator();
        // Add messages via ask
        fake.EnqueueText("Reply 1.");
        fake.EnqueueText("Reply 2.");
        // Compaction
        fake.EnqueueText("Compacted: two turns happened.");

        var session = MakeSession(fake);
        await session.AskAsync("Q1", null, "low", CancellationToken.None);
        await session.AskAsync("Q2", null, "low", CancellationToken.None);
        var beforeCompact = session.Info().Messages;

        var result = await session.CompactAsync(null, CancellationToken.None);

        result.MessagesBefore.Should().Be(beforeCompact);
        result.MessagesAfter.Should().Be(1); // single summary message
        session.Info().LastCompacted.Should().NotBeNull();
    }

    // ── State restore ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Constructor_RestoresStateFromFile()
    {
        // Write a state file and create a new session over it
        var persistence = new StatePersistence(_statePath);

        // First session: perform an ask, persist state
        var fake1 = new FakeMessageCreator();
        fake1.EnqueueText("Reply.", inputTokens: 200, outputTokens: 50);
        var session1 = MakeSession(fake1, persistence);
        await session1.AskAsync("Q?", null, "low", CancellationToken.None);

        // Second session: restores from state file
        var fake2 = new FakeMessageCreator();
        var session2 = MakeSession(fake2, persistence);

        session2.Info().ApproximateTokens.Should().Be(250);
        session2.Info().Messages.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Constructor_CorruptStateFile_StartsEmpty()
    {
        File.WriteAllText(_statePath, "not valid json ))))");
        var persistence = new StatePersistence(_statePath);
        var fake = new FakeMessageCreator();

        var session = MakeSession(fake, persistence);

        session.Info().Messages.Should().Be(0);
        session.Info().ApproximateTokens.Should().Be(0);
    }

    [Fact]
    public void Constructor_NoPersistence_StartsEmpty()
    {
        var fake = new FakeMessageCreator();
        var session = MakeSession(fake, persistence: null);

        session.Info().Messages.Should().Be(0);
    }

    // ── AskOverrides ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_NoOverrides_UsesProductionSystemPrompt()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.");
        var session = MakeSession(fake);

        await session.AskAsync("Q?", null, "low", CancellationToken.None);

        var callJson = JsonSerializer.Serialize(fake.Calls[0]);
        // The default system prompt resolves to SystemPrompt.Text. Sentinel "knowledge
        // retrieval" appears in both the shipped template and any reasonable customized
        // prompt for this corpus.
        callJson.Should().Contain("knowledge retrieval");
    }

    [Fact]
    public async Task AskAsync_SystemPromptOverride_UsedInsteadOfDefault()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.");
        var session = MakeSession(fake);
        var overrides = new AskOverrides(SystemPromptOverride: "CUSTOM_SYSTEM_PROMPT_MARKER");

        await session.AskAsync("Q?", null, "low", CancellationToken.None, overrides);

        var callJson = JsonSerializer.Serialize(fake.Calls[0]);
        callJson.Should().Contain("CUSTOM_SYSTEM_PROMPT_MARKER");
    }

    [Fact]
    public async Task AskAsync_UserMessageWrapper_AppliedToUserMessage()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Reply.");
        var session = MakeSession(fake);
        var overrides = new AskOverrides(UserMessageWrapperTemplate: "PREFIX: {query} :SUFFIX");

        await session.AskAsync("the question", null, "low", CancellationToken.None, overrides);

        var callJson = JsonSerializer.Serialize(fake.Calls[0]);
        callJson.Should().Contain("PREFIX: the question :SUFFIX");
    }
}
