using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SecondBrain.Files;
using SecondBrain.Index.Indexing;
using SecondBrain.Index.RequestHistory;
using SecondBrain.Index.Search;
using SecondBrain.Llm;
using SecondBrain.Mcp.Tests.Fakes;
using SecondBrain.Mcp.Handler;

namespace SecondBrain.Mcp.Tests;

public sealed class SecondBrainMcpHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _ftsDb;
    private readonly string _requestsDb;
    private readonly string _configPath;

    public SecondBrainMcpHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _sourceDir = Path.Combine(_tempDir, "sources");
        _ftsDb = Path.Combine(_tempDir, "fts.db");
        _requestsDb = Path.Combine(_tempDir, "requests.db");
        _configPath = Path.Combine(_tempDir, "sources.json");
        Directory.CreateDirectory(_sourceDir);
        File.WriteAllText(_configPath,
            $$"""[{"id":"test","path":"{{_sourceDir.Replace("\\", "\\\\")}}"}]""",
            Encoding.UTF8);
        BuildIndex();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        // Background summarization tasks may still hold DB file handles.
        // Retry a few times to give them time to finish.
        for (var i = 0; i < 5; i++)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_tempDir, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
        // Best-effort — accept if still locked
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void WriteSource(string name, string content)
        => File.WriteAllText(Path.Combine(_sourceDir, name), content, new UTF8Encoding(false));

    private void BuildIndex()
        => new IndexBuilder().Build(_configPath, _ftsDb, 5_000_000);

    private SecondBrainMcpHandler MakeHandler(FakeMessageCreator? fake = null)
    {
        var client = fake ?? new FakeMessageCreator();
        var engine = new SearchEngine(_ftsDb);
        var history = new RequestHistory(_requestsDb);
        var reader = new FileReader([_sourceDir]);
        var compactor = new Compactor(client, "claude-sonnet-4-6");
        var session = new ClaudeSession(
            client: client,
            searchEngine: engine,
            fileReader: reader,
            compactor: compactor,
            defaultModel: "claude-haiku-4-5");
        var summarizer = new DocumentSummarizer(client);

        return new SecondBrainMcpHandler(
            session: session,
            searchEngine: engine,
            requestHistory: history,
            sourcesConfigPath: _configPath,
            ftsDbPath: _ftsDb,
            indexMaxBytes: 5_000_000,
            fileReader: reader,
            summarizer: summarizer,
            mcpTimeoutSeconds: 120,
            summarizeSafetyBufferSeconds: 30,
            logger: NullLogger.Instance);
    }

    private static JsonNode MakeRequest(string method, JsonNode? @params = null, int id = 1)
    {
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params != null) req["params"] = @params;
        return req;
    }

    private static JsonNode ToolCallRequest(string toolName, JsonObject? args = null, int id = 1)
        => MakeRequest("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = args ?? new JsonObject(),
        }, id);

    // ── JSON-RPC envelope ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleRequestAsync_Initialize_ReturnsProtocolVersion()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(MakeRequest("initialize"));

        response["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2024-11-05");
        response["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("second-brain-mcp");
    }

    [Fact]
    public async Task HandleRequestAsync_ToolsList_ContainsExpectedTools()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(MakeRequest("tools/list"));

        var tools = response["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        names.Should().Contain("search");
        names.Should().Contain("ask");
        names.Should().Contain("rebuild_index");
        names.Should().Contain("session_info");
    }

    [Fact]
    public async Task HandleRequestAsync_UnknownMethod_ReturnsErrorCode32601()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(MakeRequest("nonexistent/method"));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
    }

    [Fact]
    public async Task HandleRequestAsync_MethodNotificationsInitialized_ReturnsSuccess()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(MakeRequest("notifications/initialized"));

        response["result"].Should().NotBeNull();
        response["error"].Should().BeNull();
    }

    // ── search tool ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCall_Search_ReturnsRequestIdAndHits()
    {
        WriteSource("note.md", "quantum entanglement physics");
        BuildIndex();
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("search", new JsonObject
        {
            ["query"] = "quantum entanglement",
        }));

        var content = ExtractToolContent(response);
        var result = JsonDocument.Parse(content).RootElement;
        result.GetProperty("request_id").GetString().Should().HaveLength(8);
        result.GetProperty("hits").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ToolCall_Search_PersistedToRequestHistory()
    {
        WriteSource("article.md", "machine learning deployment");
        BuildIndex();
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("search", new JsonObject
        {
            ["query"] = "machine learning",
        }));

        var content = ExtractToolContent(response);
        var requestId = JsonDocument.Parse(content).RootElement.GetProperty("request_id").GetString()!;

        // Retrieve via get_request
        var getResponse = await handler.HandleRequestAsync(ToolCallRequest("get_request", new JsonObject
        {
            ["request_id"] = requestId,
        }));
        var getContent = ExtractToolContent(getResponse);
        var getResult = JsonDocument.Parse(getContent).RootElement;
        getResult.GetProperty("tool").GetString().Should().Be("search");
        getResult.GetProperty("query").GetString().Should().Be("machine learning");
    }

    [Fact]
    public async Task ToolCall_Search_WithListSources_ReturnsSources()
    {
        WriteSource("doc.md", "kubernetes orchestration");
        BuildIndex();
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("search", new JsonObject
        {
            ["query"] = "kubernetes",
            ["list_sources"] = true,
        }));

        var content = ExtractToolContent(response);
        var result = JsonDocument.Parse(content).RootElement;
        result.TryGetProperty("sources_summary", out var sources).Should().BeTrue();
        sources.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // ── get_request tool ──────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCall_GetRequest_UnknownId_ReturnsError()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("get_request", new JsonObject
        {
            ["request_id"] = "nonexistent",
        }));

        IsToolError(response).Should().BeTrue();
        ExtractToolContent(response).Should().Contain("not found");
    }

    // ── rebuild_index tool ────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCall_RebuildIndex_IncrementalMode_ReturnsStats()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("rebuild_index", new JsonObject
        {
            ["mode"] = "incremental",
        }));

        var content = ExtractToolContent(response);
        var result = JsonDocument.Parse(content).RootElement;
        result.TryGetProperty("added", out _).Should().BeTrue();
        result.TryGetProperty("modified", out _).Should().BeTrue();
        result.TryGetProperty("removed", out _).Should().BeTrue();
        result.TryGetProperty("elapsed_seconds", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ToolCall_RebuildIndex_FullMode_ReturnsIndexed()
    {
        WriteSource("a.md", "some content");
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("rebuild_index", new JsonObject
        {
            ["mode"] = "full",
        }));

        var content = ExtractToolContent(response);
        var result = JsonDocument.Parse(content).RootElement;
        result.GetProperty("mode").GetString().Should().Be("full");
        result.TryGetProperty("indexed", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ToolCall_RebuildIndex_InvalidMode_ReturnsError()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("rebuild_index", new JsonObject
        {
            ["mode"] = "bogus",
        }));

        IsToolError(response).Should().BeTrue();
        ExtractToolContent(response).Should().Contain("bogus");
    }

    // ── session_info and reset_session ────────────────────────────────────────

    [Fact]
    public async Task ToolCall_SessionInfo_ReturnsMessagesAndModel()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("session_info"));

        var content = ExtractToolContent(response);
        var result = JsonDocument.Parse(content).RootElement;
        result.TryGetProperty("messages", out _).Should().BeTrue();
        result.TryGetProperty("current_default_model", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ToolCall_ResetSession_ReturnsStatusReset()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("reset_session"));

        var content = ExtractToolContent(response);
        content.Should().Contain("reset");
    }

    // ── unknown tool ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCall_UnknownTool_ReturnsErrorResult()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("mystery_tool"));

        IsToolError(response).Should().BeTrue();
        ExtractToolContent(response).Should().Contain("mystery_tool");
    }

    // ── generate_summaries ────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCall_GenerateSummaries_FirstCall_ReturnsStarted()
    {
        var handler = MakeHandler();

        var response = await handler.HandleRequestAsync(ToolCallRequest("generate_summaries"));

        var content = ExtractToolContent(response);
        content.Should().Contain("started");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string ExtractToolContent(JsonNode response)
    {
        var contentArr = response["result"]!["content"]!.AsArray();
        return contentArr[0]!["text"]!.GetValue<string>();
    }

    private static bool IsToolError(JsonNode response)
    {
        var isError = response["result"]?["isError"];
        return isError?.GetValue<bool>() == true;
    }
}
