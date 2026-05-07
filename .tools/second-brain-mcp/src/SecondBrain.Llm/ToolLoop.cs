using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SecondBrain.Files;
using SecondBrain.Index.Search;
using SecondBrain.Llm.Prompts;

namespace SecondBrain.Llm;

internal sealed class ToolLoopResult
{
    public required string Synthesis { get; init; }
    public required int ToolsCalled { get; init; }
    public required IReadOnlyList<string> FilesReferenced { get; init; }
    public required long InputTokensUsed { get; init; }
    public required long OutputTokensUsed { get; init; }
    public required decimal EstimatedCostUsd { get; init; }
}

internal sealed class ToolLoop
{
    private readonly IMessageCreator _client;
    private readonly SearchEngine _searchEngine;
    private readonly FileReader _fileReader;
    private readonly ILogger _logger;
    private readonly IStatsRecorder? _stats;
    private readonly bool _supportsOutputConfig;

    public ToolLoop(IMessageCreator client, SearchEngine searchEngine, FileReader fileReader, ILogger? logger = null, IStatsRecorder? stats = null)
    {
        _client = client;
        _searchEngine = searchEngine;
        _fileReader = fileReader;
        _logger = logger ?? NullLogger.Instance;
        _stats = stats;
        // Vertex AI rejects output_config; only the direct Anthropic API supports it
        _supportsOutputConfig = !string.Equals(
            Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX"), "1", StringComparison.Ordinal);
    }

    public async Task<ToolLoopResult> RunAsync(
        List<MessageParam> messages,
        string model,
        Effort apiEffort,
        CancellationToken ct)
    {
        var filesThisTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = ToolDefinitions.Build();
        var toolsCalled = 0;
        long inputTokens = 0;
        long outputTokens = 0;
        decimal estimatedCost = 0m;
        string synthesis = string.Empty;

        while (true)
        {
            // Apply a cache breakpoint to the last message so subsequent calls
            // (in this loop and in future asks) hit cache for the prior conversation.
            var requestMessages = WithCacheBreakpointOnLast(messages);

            // System prompt as a cacheable text block (caches across all calls).
            var systemBlocks = new List<TextBlockParam>
            {
                new() { Text = SystemPrompt.Text, CacheControl = new CacheControlEphemeral() },
            };

            var createParams = new MessageCreateParams
            {
                Model = model,
                MaxTokens = 8192,
                Messages = requestMessages,
                Tools = tools,
                System = new MessageCreateParamsSystem(systemBlocks),
            };
            if (_supportsOutputConfig)
                createParams = createParams with { OutputConfig = new OutputConfig { Effort = apiEffort } };

            var response = await _client.CreateAsync(createParams, ct);

            inputTokens += response.Usage.InputTokens;
            outputTokens += response.Usage.OutputTokens;

            _logger.LogInformation(
                "API call: model={Model} input={Input} output={Output} cache_read={CacheRead} cache_create={CacheCreate}",
                model,
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                response.Usage.CacheReadInputTokens?.ToString() ?? "null",
                response.Usage.CacheCreationInputTokens?.ToString() ?? "null");

            estimatedCost += _stats?.RecordLlmCall(
                model,
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                response.Usage.CacheCreationInputTokens ?? 0,
                response.Usage.CacheReadInputTokens ?? 0) ?? 0m;

            // Append assistant response to messages (as ContentBlockParam via Json)
            messages.Add(new MessageParam
            {
                Role = Role.Assistant,
                Content = response.Content
                    .Select(b => new ContentBlockParam(b.Json))
                    .ToList(),
            });

            if (response.StopReason != StopReason.ToolUse)
            {
                // Extract final text
                var texts = response.Content
                    .Where(b => b.TryPickText(out _))
                    .Select(b => { b.TryPickText(out var t); return t!.Text; });
                synthesis = string.Join("\n", texts);
                break;
            }

            // Process all tool_use blocks and collect results
            var toolResults = new List<ContentBlockParam>();
            foreach (var block in response.Content)
            {
                if (!block.TryPickToolUse(out var toolUse))
                    continue;

                toolsCalled++;
                var resultContent = await DispatchToolAsync(toolUse, filesThisTurn, ct);

                ContentBlockParam toolResult = new ToolResultBlockParam
                {
                    ToolUseID = toolUse.ID,
                    Content = resultContent,
                };
                toolResults.Add(toolResult);
            }

            // Append tool results as a user message
            messages.Add(new MessageParam
            {
                Role = Role.User,
                Content = toolResults,
            });
        }

        return new ToolLoopResult
        {
            Synthesis = synthesis,
            ToolsCalled = toolsCalled,
            FilesReferenced = [.. filesThisTurn],
            InputTokensUsed = inputTokens,
            OutputTokensUsed = outputTokens,
            EstimatedCostUsd = estimatedCost,
        };
    }

    private async Task<string> DispatchToolAsync(
        ToolUseBlock toolUse,
        HashSet<string> filesThisTurn,
        CancellationToken ct)
    {
        try
        {
            _stats?.RecordToolDispatch(toolUse.Name);
            return toolUse.Name switch
            {
                "search" => await Task.FromResult(RunSearch(toolUse, filesThisTurn)),
                "read_file" => await Task.FromResult(RunReadFile(toolUse, filesThisTurn)),
                _ => $"Unknown tool: {toolUse.Name}",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Tool '{ToolName}' failed (tool_use_id={ToolUseId}, input={Input})",
                toolUse.Name,
                toolUse.ID,
                JsonSerializer.Serialize(toolUse.Input));
            return $"Error executing {toolUse.Name}: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private string RunSearch(ToolUseBlock toolUse, HashSet<string> filesThisTurn)
    {
        var input = toolUse.Input;
        var queries = ExtractQueries(input);
        var baseParams = BuildSearchParams(input);

        var result = queries.Count > 0
            ? _searchEngine.SearchMulti(queries, baseParams)
            : _searchEngine.Search(baseParams); // filter-only fallthrough

        foreach (var hit in result.Hits)
            filesThisTurn.Add(hit.AbsolutePath);

        return SerializeSearchResult(result);
    }

    private static List<string> ExtractQueries(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("queries", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static SearchParams BuildSearchParams(IReadOnlyDictionary<string, JsonElement> input)
    {
        // Query is null here — variants are passed via ExtractQueries / SearchMulti.
        string? query = null;

        DateOnly? dateStart = null;
        if (input.TryGetValue("date_start", out var ds) && ds.ValueKind == JsonValueKind.String
            && DateOnly.TryParse(ds.GetString(), out var parsedStart))
            dateStart = parsedStart;

        DateOnly? dateEnd = null;
        if (input.TryGetValue("date_end", out var de) && de.ValueKind == JsonValueKind.String
            && DateOnly.TryParse(de.GetString(), out var parsedEnd))
            dateEnd = parsedEnd;

        IReadOnlyList<string>? people = ReadStringArray(input, "people");
        IReadOnlyList<string>? sourceType = ReadStringArray(input, "source_type");
        IReadOnlyList<string>? sourceFolders = ReadStringArray(input, "source_folders");

        int top = 30;
        if (input.TryGetValue("top", out var topEl) && topEl.ValueKind == JsonValueKind.Number)
            top = topEl.GetInt32();

        string returnMode = "snippets";
        if (input.TryGetValue("return_mode", out var rm) && rm.ValueKind == JsonValueKind.String)
            returnMode = rm.GetString() ?? "snippets";

        return new SearchParams(
            Query: query,
            DateStart: dateStart,
            DateEnd: dateEnd,
            People: people,
            SourceType: sourceType,
            SourceFolders: sourceFolders,
            Top: top,
            ReturnMode: returnMode);
    }

    private static IReadOnlyList<string>? ReadStringArray(
        IReadOnlyDictionary<string, JsonElement> input,
        string key)
    {
        if (!input.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            var s = item.GetString();
            if (s != null) list.Add(s);
        }
        return list.Count > 0 ? list : null;
    }

    private static string SerializeSearchResult(SearchResult result)
    {
        if (result.Hits.Count == 0)
            return "No results found.";

        var parts = result.Hits.Select(h =>
        {
            var snippet = h.Matches.Count > 0 ? $"\n  Snippet: {h.Matches[0].Snippet}" : "";
            return $"- {h.RelativePath} [score: {h.Score:F2}]{snippet}";
        });

        return $"Found {result.Hits.Count} result(s):\n{string.Join("\n", parts)}";
    }

    /// <summary>
    /// Returns a copy of <paramref name="messages"/> where the last entry has
    /// <c>cache_control: { type: "ephemeral" }</c> on its last content block.
    /// Done at JSON level to handle string-content and block-list-content uniformly
    /// without grappling with the SDK's discriminated union types.
    /// </summary>
    private static List<MessageParam> WithCacheBreakpointOnLast(IReadOnlyList<MessageParam> messages)
    {
        if (messages.Count == 0) return [];
        var copy = messages.ToList();
        var last = copy[^1];

        try
        {
            var json = JsonSerializer.SerializeToNode(last)?.AsObject();
            if (json == null) return copy;

            var content = json["content"];
            switch (content)
            {
                case JsonArray arr when arr.Count > 0:
                {
                    if (arr[^1] is JsonObject lastBlock)
                        lastBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                    break;
                }
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                {
                    // Convert string-content into a single text block with cache_control
                    json["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = v.GetValue<string>(),
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                    });
                    break;
                }
                default:
                    return copy;
            }

            var modified = JsonSerializer.Deserialize<MessageParam>(json.ToJsonString());
            if (modified != null) copy[^1] = modified;
        }
        catch
        {
            // If anything fails, fall back to sending without cache_control on this message.
            // The cached tools still apply.
        }

        return copy;
    }

    private string RunReadFile(ToolUseBlock toolUse, HashSet<string> filesThisTurn)
    {
        if (!toolUse.Input.TryGetValue("path", out var pathEl)
            || pathEl.ValueKind != JsonValueKind.String)
        {
            return "Error: 'path' parameter is required.";
        }

        var path = pathEl.GetString()!;
        filesThisTurn.Add(path);
        _stats?.RecordFileRead(path);
        return _fileReader.Read(path);
    }
}
