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

    public ToolLoop(IMessageCreator client, SearchEngine searchEngine, FileReader fileReader, ILogger? logger = null, IStatsRecorder? stats = null)
    {
        _client = client;
        _searchEngine = searchEngine;
        _fileReader = fileReader;
        _logger = logger ?? NullLogger.Instance;
        _stats = stats;
    }

    /// <summary>
    /// Hard cap on tool-use turns within a single ask. Each turn appends the
    /// model's response and the tool results to the message history; without a
    /// cap, a model that keeps calling tools can spiral past the 200K-token
    /// context limit and crash the request. Surfaced by the prompt-eval harness
    /// when one test case hit 208K tokens.
    /// </summary>
    public const int MaxToolTurns = 25;

    /// <summary>
    /// Per-call cap on the size of a read_file response. Larger files are
    /// truncated with a marker. ~32 KB encodes to roughly 8K tokens — large
    /// enough to be useful for a focused read, small enough that even several
    /// reads within a single ask cannot dominate the 200K context window.
    /// Surfaced by the prompt-eval harness: tc_014 plus three other cases hit
    /// the 200K limit even with MaxToolTurns enforced, because each read of a
    /// large file ate ~25K tokens at a time.
    /// </summary>
    public const int MaxReadFileBytes = 32_768;

    /// <summary>
    /// Soft cap on input tokens reported by the API. Once a single API call
    /// reports input_tokens above this threshold, the loop forces synthesis
    /// on the next call (omitting tools) instead of letting the conversation
    /// keep growing toward the 200K hard limit. Leaves a ~50K-token buffer
    /// for the synthesis call itself.
    /// </summary>
    public const long ContextSoftLimitTokens = 150_000;

    public async Task<ToolLoopResult> RunAsync(
        List<MessageParam> messages,
        string model,
        Effort apiEffort,
        CancellationToken ct,
        string? systemPromptOverride = null,
        IReadOnlyList<ToolUnion>? toolsOverride = null)
    {
        var filesThisTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = toolsOverride ?? ToolDefinitions.Build();
        var systemPromptText = systemPromptOverride ?? SystemPrompt.Text;
        var toolsCalled = 0;
        var toolTurns = 0;
        var forceSynthesis = false;
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
                new() { Text = systemPromptText, CacheControl = new CacheControlEphemeral() },
            };

            // Effort tier maps to Thinking budget + scaled MaxTokens. The earlier
            // OutputConfig.Effort path was silently dropped on Vertex; Thinking is
            // a standard API field and works on both Vertex and direct API.
            var (thinking, maxTokens) = EffortConfig.Resolve(apiEffort);

            var createParams = new MessageCreateParams
            {
                Model = model,
                MaxTokens = maxTokens,
                Messages = requestMessages,
                System = new MessageCreateParamsSystem(systemBlocks),
            };
            // Omit Tools entirely when forcing synthesis. The model has no tools
            // to call, so it must produce a final text response. Cleaner than
            // injecting a forcing user message (which created two consecutive
            // user messages and tripped "messages.X: empty content" rejections).
            if (!forceSynthesis)
                createParams = createParams with { Tools = tools };
            if (thinking != null)
                createParams = createParams with { Thinking = thinking };

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

            // Defensive: StopReason was tool_use but no tool_use blocks were
            // present (or all were filtered). Adding an empty-content user
            // message would cause the API to reject the next request with
            // "messages.X: user messages must have non-empty content". Treat
            // this as completion using whatever text the response did contain.
            if (toolResults.Count == 0)
            {
                _logger.LogWarning(
                    "Response had StopReason=ToolUse but no tool_use blocks dispatched; treating as completion.");
                var texts = response.Content
                    .Where(b => b.TryPickText(out _))
                    .Select(b => { b.TryPickText(out var t); return t!.Text; });
                synthesis = string.Join("\n", texts);
                break;
            }

            // Append tool results as a user message
            messages.Add(new MessageParam
            {
                Role = Role.User,
                Content = toolResults,
            });

            toolTurns++;

            // Two reasons to force synthesis on the next call: tool-turn cap
            // reached, or context approaching the hard 200K limit. Either way,
            // we set the flag and let the next iteration omit Tools so the
            // model produces a final text response.
            if (toolTurns >= MaxToolTurns)
            {
                _logger.LogWarning(
                    "Tool loop reached MaxToolTurns={Cap}; forcing synthesis on next call.",
                    MaxToolTurns);
                forceSynthesis = true;
            }
            else if (response.Usage.InputTokens >= ContextSoftLimitTokens)
            {
                _logger.LogWarning(
                    "Tool loop input reached {Tokens} tokens (soft limit {Limit}); forcing synthesis on next call to avoid 200K hard cap.",
                    response.Usage.InputTokens,
                    ContextSoftLimitTokens);
                forceSynthesis = true;
            }
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

        // Catch the two common errors and return guidance the model can act on.
        // Bare exception messages (FileNotFoundException, UnauthorizedAccessException)
        // don't tell the model what to do next; this does.
        try
        {
            var content = _fileReader.Read(path);
            // Only record as referenced if the read succeeded — hallucinated paths
            // shouldn't pollute FilesReferenced.
            filesThisTurn.Add(path);
            _stats?.RecordFileRead(path);
            return TruncateForToolResult(content);
        }
        catch (FileNotFoundException)
        {
            return $"File not found at path: {path}\n\n" +
                   "Use only absolute_path values returned by `search`. Do not invent or extrapolate paths " +
                   "from filenames or snippet content. If you need a different file, run `search` again.";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Path is outside the indexed source folders: {path}\n\n" +
                   "Use only absolute_path values returned by `search`. The corpus is read-only and bounded " +
                   "to the configured source roots; arbitrary local paths cannot be read.";
        }
        catch (InvalidDataException ex)
        {
            return $"Cannot read {path}: {ex.Message}";
        }
    }

    private static string TruncateForToolResult(string content)
    {
        if (content.Length <= MaxReadFileBytes)
            return content;

        return content.Substring(0, MaxReadFileBytes) +
               $"\n\n[truncated: file is {content.Length} bytes; returned first {MaxReadFileBytes}. " +
               "Run `search` with more specific terms to locate the section you need, " +
               "or read a different file.]";
    }
}
