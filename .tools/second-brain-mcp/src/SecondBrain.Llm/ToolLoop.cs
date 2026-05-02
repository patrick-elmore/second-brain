using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SecondBrain.Files;
using SecondBrain.Index.Search;

namespace SecondBrain.Llm;

internal sealed class ToolLoopResult
{
    public required string Synthesis { get; init; }
    public required int ToolsCalled { get; init; }
    public required IReadOnlyList<string> FilesReferenced { get; init; }
    public required long InputTokensUsed { get; init; }
    public required long OutputTokensUsed { get; init; }
}

internal sealed class ToolLoop
{
    private readonly IAnthropicClient _client;
    private readonly SearchEngine _searchEngine;
    private readonly FileReader _fileReader;
    private readonly ILogger _logger;
    private readonly IStatsRecorder? _stats;
    private readonly bool _supportsOutputConfig;

    public ToolLoop(IAnthropicClient client, SearchEngine searchEngine, FileReader fileReader, ILogger? logger = null, IStatsRecorder? stats = null)
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
        string synthesis = string.Empty;

        while (true)
        {
            var createParams = new MessageCreateParams
            {
                Model = model,
                MaxTokens = 8192,
                Messages = messages,
                Tools = tools,
            };
            if (_supportsOutputConfig)
                createParams = createParams with { OutputConfig = new OutputConfig { Effort = apiEffort } };

            var response = await _client.Messages.Create(createParams, ct);

            inputTokens += response.Usage.InputTokens;
            outputTokens += response.Usage.OutputTokens;

            _stats?.RecordLlmCall(
                model,
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                response.Usage.CacheCreationInputTokens ?? 0,
                response.Usage.CacheReadInputTokens ?? 0);

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
        var p = BuildSearchParams(input);
        var result = _searchEngine.Search(p);

        foreach (var hit in result.Hits)
            filesThisTurn.Add(hit.AbsolutePath);

        return SerializeSearchResult(result);
    }

    private static SearchParams BuildSearchParams(IReadOnlyDictionary<string, JsonElement> input)
    {
        string? query = null;
        if (input.TryGetValue("query", out var q) && q.ValueKind == JsonValueKind.String)
            query = q.GetString();

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
