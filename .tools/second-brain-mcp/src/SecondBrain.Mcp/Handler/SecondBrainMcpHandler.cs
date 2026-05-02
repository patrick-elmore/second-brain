using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SecondBrain.Index.RequestHistory;
using SecondBrain.Index.Search;
using SecondBrain.Llm;
using SecondBrain.Mcp.Services;
using SecondBrain.Mcp.Stats;

namespace SecondBrain.Mcp.Handler;

public sealed class SecondBrainMcpHandler : IMcpRequestHandler
{
    private readonly ClaudeSession _session;
    private readonly SearchEngine _searchEngine;
    private readonly RequestHistory _requestHistory;
    private readonly ILogger _logger;
    private readonly StatsTracker? _stats;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public bool IsHealthy => true;

    public SecondBrainMcpHandler(
        ClaudeSession session,
        SearchEngine searchEngine,
        RequestHistory requestHistory,
        ILogger logger,
        StatsTracker? stats = null)
    {
        _session = session;
        _searchEngine = searchEngine;
        _requestHistory = requestHistory;
        _logger = logger;
        _stats = stats;
    }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<JsonNode> HandleRequestAsync(JsonNode request, CancellationToken ct = default)
    {
        var id = request["id"];
        var method = request["method"]?.GetValue<string>();
        var @params = request["params"];

        try
        {
            return method switch
            {
                "initialize" => HandleInitialize(id, @params),
                "tools/list" => HandleToolsList(id),
                "tools/call" => await HandleToolCallAsync(id, @params, ct),
                "notifications/initialized" => ResponseBuilder.Success(id, new JsonObject()),
                _ => ResponseBuilder.Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (OperationCanceledException)
        {
            return ResponseBuilder.Error(id, -32000, "Request cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling MCP request method={Method}", method);
            return ResponseBuilder.Error(id, -32603, ex.Message);
        }
    }

    private static JsonNode HandleInitialize(JsonNode? id, JsonNode? @params)
    {
        return ResponseBuilder.Success(id, new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "second-brain-mcp",
                ["version"] = "1.0.0",
            },
        });
    }

    private static JsonNode HandleToolsList(JsonNode? id)
    {
        return ResponseBuilder.Success(id, new JsonObject
        {
            ["tools"] = McpToolSchemas.BuildToolList(),
        });
    }

    private async Task<JsonNode> HandleToolCallAsync(JsonNode? id, JsonNode? @params, CancellationToken ct)
    {
        var toolName = @params?["name"]?.GetValue<string>()
            ?? throw new ArgumentException("tools/call requires 'name'");
        var arguments = @params?["arguments"] as JsonObject ?? new JsonObject();

        _stats?.RecordMcpToolCall(toolName);

        await _mutex.WaitAsync(ct);
        try
        {
            var result = toolName switch
            {
                "search" => await HandleSearchAsync(arguments, ct),
                "ask" => await HandleAskAsync(arguments, ct),
                "compact_session" => await HandleCompactAsync(arguments, ct),
                "reset_session" => HandleReset(),
                "session_info" => HandleSessionInfo(),
                "get_request" => HandleGetRequest(arguments),
                "rebuild_index" => HandleRebuildIndex(),
                _ => ResponseBuilder.ToolResult($"Unknown tool: {toolName}", isError: true),
            };

            return ResponseBuilder.Success(id, result);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<JsonNode> HandleSearchAsync(JsonObject args, CancellationToken ct)
    {
        var p = ParseSearchParams(args);
        var result = _searchEngine.Search(p);
        var requestId = GenerateRequestId();

        // Persist request record
        var files = result.Hits.Select((h, i) => new RequestFile(
            Rank: i,
            AbsolutePath: h.AbsolutePath,
            RelativePath: h.RelativePath,
            SourceFolderId: h.SourceFolderId,
            Score: h.Score)).ToList();

        _requestHistory.PersistRequest(new RequestRecord(
            Id: requestId,
            Timestamp: DateTime.UtcNow,
            Tool: "search",
            Query: p.Query,
            FiltersJson: SerializeFilters(p),
            ResultCount: result.Hits.Count,
            Synthesis: null), files);

        var hitsArray = new JsonArray();
        foreach (var hit in result.Hits)
        {
            var hitNode = new JsonObject
            {
                ["absolute_path"] = hit.AbsolutePath,
                ["relative_path"] = hit.RelativePath,
                ["source_folder_id"] = hit.SourceFolderId,
                ["score"] = hit.Score,
            };

            if (hit.Metadata.HasValue)
                hitNode["metadata"] = JsonNode.Parse(hit.Metadata.Value.GetRawText());

            if (hit.Matches.Count > 0)
            {
                var matchArray = new JsonArray();
                foreach (var m in hit.Matches)
                    matchArray.Add(new JsonObject { ["snippet"] = m.Snippet });
                hitNode["matches"] = matchArray;
            }

            hitsArray.Add(hitNode);
        }

        var response = new JsonObject
        {
            ["request_id"] = requestId,
            ["hits"] = hitsArray,
        };

        if (result.SourcesSummary != null)
        {
            var sourcesArray = new JsonArray();
            foreach (var s in result.SourcesSummary)
                sourcesArray.Add(new JsonObject
                {
                    ["source_folder_id"] = s.SourceFolderId,
                    ["hit_count"] = s.HitCount,
                });
            response["sources_summary"] = sourcesArray;
        }

        return ResponseBuilder.ToolResult(response.ToJsonString());
    }

    private async Task<JsonNode> HandleAskAsync(JsonObject args, CancellationToken ct)
    {
        var question = args["question"]?.GetValue<string>()
            ?? throw new ArgumentException("ask requires 'question'");
        var compactInstruction = args["compact_instruction"]?.GetValue<string>();
        var effort = args["effort"]?.GetValue<string>() ?? "low";

        var askResult = await _session.AskAsync(question, compactInstruction, effort, ct);

        // Persist ask request
        var files = askResult.FilesReferenced.Select((path, i) => new RequestFile(
            Rank: i,
            AbsolutePath: path,
            RelativePath: path, // best effort without source context
            SourceFolderId: "unknown",
            Score: null)).ToList();

        _requestHistory.PersistRequest(new RequestRecord(
            Id: askResult.RequestId,
            Timestamp: DateTime.UtcNow,
            Tool: "ask",
            Query: question,
            FiltersJson: "{}",
            ResultCount: askResult.FilesReferenced.Count,
            Synthesis: askResult.Synthesis), files);

        var response = new JsonObject
        {
            ["request_id"] = askResult.RequestId,
            ["synthesis"] = askResult.Synthesis,
            ["model_used"] = askResult.ModelUsed,
            ["tools_called"] = askResult.ToolsCalled,
            ["files_referenced"] = new JsonArray(askResult.FilesReferenced.Select(f => JsonValue.Create(f)).ToArray()),
        };

        return ResponseBuilder.ToolResult(response.ToJsonString());
    }

    private async Task<JsonNode> HandleCompactAsync(JsonObject args, CancellationToken ct)
    {
        var instruction = args["instruction"]?.GetValue<string>();
        var result = await _session.CompactAsync(instruction, ct);

        var response = new JsonObject
        {
            ["messages_before"] = result.MessagesBefore,
            ["messages_after"] = result.MessagesAfter,
            ["approximate_tokens_before"] = result.ApproximateTokensBefore,
            ["approximate_tokens_after"] = result.ApproximateTokensAfter,
        };

        return ResponseBuilder.ToolResult(response.ToJsonString());
    }

    private JsonNode HandleReset()
    {
        _session.Reset();
        return ResponseBuilder.ToolResult("{\"status\":\"reset\"}");
    }

    private JsonNode HandleSessionInfo()
    {
        var info = _session.Info();
        var response = new JsonObject
        {
            ["messages"] = info.Messages,
            ["approximate_tokens"] = info.ApproximateTokens,
            ["current_default_model"] = info.CurrentDefaultModel,
            ["last_compacted"] = info.LastCompacted?.ToString("o"),
            ["last_activity"] = info.LastActivity?.ToString("o"),
            ["state_persisted_at"] = info.StatePersistedAt?.ToString("o"),
        };
        return ResponseBuilder.ToolResult(response.ToJsonString());
    }

    private JsonNode HandleGetRequest(JsonObject args)
    {
        var requestId = args["request_id"]?.GetValue<string>()
            ?? throw new ArgumentException("get_request requires 'request_id'");

        IReadOnlyList<string>? fields = null;
        if (args["fields"] is JsonArray fieldsArray)
            fields = fieldsArray.Select(f => f?.GetValue<string>() ?? "").Where(f => f.Length > 0).ToList();

        var entity = _requestHistory.Get(requestId, fields);
        if (entity == null)
            return ResponseBuilder.ToolResult($"{{\"error\":\"Request not found: {requestId}\"}}", isError: true);

        var response = new JsonObject { ["request_id"] = entity.RequestId };
        if (entity.Timestamp != null) response["timestamp"] = entity.Timestamp;
        if (entity.Tool != null) response["tool"] = entity.Tool;
        if (entity.Query != null) response["query"] = entity.Query;
        if (entity.FiltersJson != null) response["filters"] = JsonNode.Parse(entity.FiltersJson);
        if (entity.ResultCount.HasValue) response["result_count"] = entity.ResultCount.Value;
        if (entity.Synthesis != null) response["synthesis"] = entity.Synthesis;
        if (entity.Files != null)
        {
            var filesArray = new JsonArray();
            foreach (var f in entity.Files)
                filesArray.Add(new JsonObject
                {
                    ["rank"] = f.Rank,
                    ["absolute_path"] = f.AbsolutePath,
                    ["relative_path"] = f.RelativePath,
                    ["source_folder_id"] = f.SourceFolderId,
                    ["score"] = f.Score,
                });
            response["files"] = filesArray;
        }

        return ResponseBuilder.ToolResult(response.ToJsonString());
    }

    private static JsonNode HandleRebuildIndex()
    {
        return ResponseBuilder.ToolResult(
            "{\"status\":\"not_implemented\",\"message\":\"Use the SecondBrain.IndexBuilder console app to rebuild.\"}");
    }

    private static SearchParams ParseSearchParams(JsonObject args)
    {
        string? query = args["query"]?.GetValue<string>();

        DateOnly? dateStart = null;
        var ds = args["date_start"]?.GetValue<string>();
        if (ds != null && DateOnly.TryParse(ds, out var parsedStart)) dateStart = parsedStart;

        DateOnly? dateEnd = null;
        var de = args["date_end"]?.GetValue<string>();
        if (de != null && DateOnly.TryParse(de, out var parsedEnd)) dateEnd = parsedEnd;

        IReadOnlyList<string>? people = ReadStringArray(args, "people");
        IReadOnlyList<string>? sourceType = ReadStringArray(args, "source_type");
        IReadOnlyList<string>? sourceFolders = ReadStringArray(args, "source_folders");

        int top = args["top"]?.GetValue<int>() ?? 30;
        int snippetTokens = args["snippet_tokens"]?.GetValue<int>() ?? 32;
        string returnMode = args["return_mode"]?.GetValue<string>() ?? "snippets";
        bool listSources = args["list_sources"]?.GetValue<bool>() ?? false;

        return new SearchParams(
            Query: query,
            DateStart: dateStart,
            DateEnd: dateEnd,
            People: people,
            SourceType: sourceType,
            SourceFolders: sourceFolders,
            Top: top,
            SnippetTokens: snippetTokens,
            ReturnMode: returnMode,
            ListSources: listSources);
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonObject args, string key)
    {
        if (args[key] is not JsonArray arr) return null;
        var list = arr.Select(n => n?.GetValue<string>()).Where(s => s != null).Select(s => s!).ToList();
        return list.Count > 0 ? list : null;
    }

    private static string SerializeFilters(SearchParams p)
    {
        return JsonSerializer.Serialize(new
        {
            date_start = p.DateStart?.ToString("yyyy-MM-dd"),
            date_end = p.DateEnd?.ToString("yyyy-MM-dd"),
            people = p.People,
            source_type = p.SourceType,
            source_folders = p.SourceFolders,
            top = p.Top,
            snippet_tokens = p.SnippetTokens,
            return_mode = p.ReturnMode,
        });
    }

    private static string GenerateRequestId()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
