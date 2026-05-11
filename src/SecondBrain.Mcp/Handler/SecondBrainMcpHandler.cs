using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SecondBrain.Index.Indexing;
using SecondBrain.Index.RequestHistory;
using SecondBrain.Index.Search;
using SecondBrain.Llm;
using SecondBrain.Files;
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
    private readonly string _sourcesConfigPath;
    private readonly string _ftsDbPath;
    private readonly int _indexMaxBytes;
    private readonly IReadOnlyList<string> _frontmatterDateFolders;
    private readonly FileReader _fileReader;
    private readonly DocumentSummarizer _summarizer;
    private readonly int _mcpTimeoutSeconds;
    private readonly int _summarizeSafetyBufferSeconds;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private int _summarizationRunning = 0;

    public bool IsHealthy => true;

    public SecondBrainMcpHandler(
        ClaudeSession session,
        SearchEngine searchEngine,
        RequestHistory requestHistory,
        string sourcesConfigPath,
        string ftsDbPath,
        int indexMaxBytes,
        FileReader fileReader,
        DocumentSummarizer summarizer,
        int mcpTimeoutSeconds,
        int summarizeSafetyBufferSeconds,
        ILogger logger,
        StatsTracker? stats = null,
        IReadOnlyList<string>? frontmatterDateFolders = null)
    {
        _session = session;
        _searchEngine = searchEngine;
        _requestHistory = requestHistory;
        _sourcesConfigPath = sourcesConfigPath;
        _ftsDbPath = ftsDbPath;
        _indexMaxBytes = indexMaxBytes;
        _frontmatterDateFolders = frontmatterDateFolders ?? [];
        _fileReader = fileReader;
        _summarizer = summarizer;
        _mcpTimeoutSeconds = mcpTimeoutSeconds;
        _summarizeSafetyBufferSeconds = summarizeSafetyBufferSeconds;
        _logger = logger;
        _stats = stats;
    }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public bool TryStartSummarization()
    {
        if (Interlocked.CompareExchange(ref _summarizationRunning, 1, 0) != 0)
            return false;
        _ = Task.Run(() => RunSummarizationAsync(null));
        return true;
    }

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
                "rebuild_index" => HandleRebuildIndex(arguments),
                "generate_summaries" => await HandleGenerateSummariesAsync(arguments, ct),
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

        // Prepend an explicit date context so the model can resolve relative date
        // references ("Friday", "yesterday", "this week") without guessing.
        // This rides in the user message rather than the system prompt to keep
        // the system prompt cacheable.
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
        var datePrefix = $"""
            [DATE CONTEXT: Today is {now:dddd, MMMM d, yyyy}. You MUST use this date to resolve every relative date reference in the question — "today", "yesterday", "this week", "last week", day names like "Monday" or "Friday", and any other time-relative expression. Calculate the explicit calendar date before searching. Do not ask for clarification about relative dates; compute them from the anchor above.]

            """;
        var questionWithDate = datePrefix + question;

        var askResult = await _session.AskAsync(questionWithDate, compactInstruction, effort, ct);

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
            ["estimated_cost_usd"] = Math.Round(askResult.EstimatedCostUsd, 6),
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
            ["estimated_cost_usd"] = Math.Round(result.EstimatedCostUsd, 6),
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

    private JsonNode HandleRebuildIndex(JsonObject args)
    {
        var mode = (args["mode"]?.GetValue<string>() ?? "incremental").ToLowerInvariant();

        if (mode is not ("incremental" or "full"))
            return ResponseBuilder.ToolResult(
                $"{{\"error\":\"Unknown mode '{mode}'. Expected 'incremental' or 'full'.\"}}",
                isError: true);

        try
        {
            if (mode == "full")
            {
                var builder = new IndexBuilder();
                var summary = builder.Build(_sourcesConfigPath, _ftsDbPath, _indexMaxBytes,
                    frontmatterDateFolders: _frontmatterDateFolders);
                _logger.LogInformation(
                    "rebuild_index full: indexed={Indexed} skipped={Skipped} elapsed={Elapsed}",
                    summary.IndexedCount, summary.SkippedCount, summary.Elapsed);
                var response = new JsonObject
                {
                    ["mode"] = "full",
                    ["indexed"] = summary.IndexedCount,
                    ["skipped"] = summary.SkippedCount,
                    ["elapsed_seconds"] = Math.Round(summary.Elapsed.TotalSeconds, 2),
                    ["db_path"] = summary.DbPath,
                };
                return ResponseBuilder.ToolResult(response.ToJsonString());
            }

            var updater = new IndexUpdater();
            var update = updater.Update(_sourcesConfigPath, _ftsDbPath, _indexMaxBytes,
                frontmatterDateFolders: _frontmatterDateFolders);
            _logger.LogInformation(
                "rebuild_index incremental: added={Added} modified={Modified} removed={Removed} unchanged={Unchanged} skipped={Skipped} fullRebuild={Full} elapsed={Elapsed}",
                update.Added, update.Modified, update.Removed, update.Unchanged, update.Skipped, update.FullRebuild, update.Elapsed);
            var resp = new JsonObject
            {
                ["mode"] = update.FullRebuild ? "full (fallback)" : "incremental",
                ["added"] = update.Added,
                ["modified"] = update.Modified,
                ["removed"] = update.Removed,
                ["unchanged"] = update.Unchanged,
                ["skipped"] = update.Skipped,
                ["elapsed_seconds"] = Math.Round(update.Elapsed.TotalSeconds, 2),
                ["db_path"] = update.DbPath,
            };
            return ResponseBuilder.ToolResult(resp.ToJsonString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "rebuild_index ({Mode}) failed", mode);
            var errorJson = new JsonObject { ["error"] = ex.Message }.ToJsonString();
            return ResponseBuilder.ToolResult(errorJson, isError: true);
        }
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

    private async Task<JsonNode> HandleGenerateSummariesAsync(JsonObject args, CancellationToken ct)
    {
        var sourceTypeFilter = args["source_type"]?.GetValue<string>();

        if (Interlocked.CompareExchange(ref _summarizationRunning, 1, 0) != 0)
            return ResponseBuilder.ToolResult(new JsonObject { ["status"] = "already_running" }.ToJsonString());

        _ = Task.Run(() => RunSummarizationAsync(sourceTypeFilter));

        return ResponseBuilder.ToolResult(new JsonObject { ["status"] = "started" }.ToJsonString());
    }

    private async Task RunSummarizationAsync(string? sourceTypeFilter)
    {
        const int pageSize = 1000;
        // Sentinel written for permanently-skipped rows so they don't reload on the next
        // page. Empty string is non-NULL (so the WHERE summary IS NULL filter excludes
        // them) and contributes zero weight to the FTS index.
        const string SkipSentinel = "";

        int processed = 0, skippedPermanent = 0, failedTransient = 0;
        var sw = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                var rows = LoadUnsummarizedRows(pageSize, sourceTypeFilter);
                if (rows.Count == 0) break;

                int pageRetired = 0;

                await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (row, _) =>
                {
                    try
                    {
                        var entry = new BatchDocEntry(row.Id, row.AbsolutePath, row.RelativePath, row.SourceType, row.MetadataJson);
                        var results = await _summarizer.SummarizeBatchAsync([entry], CancellationToken.None);
                        var result = results.FirstOrDefault(r => r.Id == row.Id);

                        if (result is null)
                        {
                            // Defensive: summarizer contract returns one result per input. If
                            // we ever lose a result, treat as transient and let it retry.
                            _logger.LogWarning("Summarizer returned no result for id={Id}", row.Id);
                            Interlocked.Increment(ref failedTransient);
                            return;
                        }

                        switch (result.Outcome)
                        {
                            case SummarizationOutcome.Summarized:
                                WriteSummary(row.Id, row.RelativePath, result.Summary!, result.SourceType);
                                Interlocked.Increment(ref processed);
                                Interlocked.Increment(ref pageRetired);
                                break;

                            case SummarizationOutcome.Skipped:
                                // Permanent — write sentinel so the row exits the unsummarized list.
                                WriteSummary(row.Id, row.RelativePath, SkipSentinel, sourceType: null);
                                Interlocked.Increment(ref skippedPermanent);
                                Interlocked.Increment(ref pageRetired);
                                _logger.LogDebug("Permanently skipped id={Id} reason={Reason} path={Path}",
                                    row.Id, result.Reason, row.RelativePath);
                                break;

                            case SummarizationOutcome.Failed:
                                // Transient — leave NULL for a later retry.
                                Interlocked.Increment(ref failedTransient);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Unexpected error in the per-row pipeline (DB write, etc.). Treat
                        // as transient; do not retire the row.
                        _logger.LogWarning(ex, "Failed to summarize id={Id}", row.Id);
                        Interlocked.Increment(ref failedTransient);
                    }
                });

                if (pageRetired == 0)
                {
                    // No row in this page made progress. Continuing would re-load the same
                    // rows and re-fail forever. Bail out; the next refresh trigger will retry.
                    _logger.LogWarning(
                        "Summarization halting: page of {Count} rows produced zero progress (transient failures only). Will retry on next trigger.",
                        rows.Count);
                    break;
                }
            }

            _logger.LogInformation(
                "Summarization complete: processed={Processed} skipped={Skipped} failed={Failed} elapsed={Elapsed}",
                processed, skippedPermanent, failedTransient, sw.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summarization failed after processed={Processed} skipped={Skipped} failed={Failed}",
                processed, skippedPermanent, failedTransient);
        }
        finally
        {
            Interlocked.Exchange(ref _summarizationRunning, 0);
        }
    }

    private sealed record UnsummarizedRow(long Id, string AbsolutePath, string RelativePath, string? SourceType, string? MetadataJson, long SizeBytes);

    private List<UnsummarizedRow> LoadUnsummarizedRows(int limit, string? sourceTypeFilter)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _ftsDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();

        if (string.IsNullOrEmpty(sourceTypeFilter))
        {
            cmd.CommandText = "SELECT id, absolute_path, relative_path, source_type, metadata, size_bytes FROM files WHERE summary IS NULL ORDER BY id ASC LIMIT @limit";
        }
        else
        {
            cmd.CommandText = "SELECT id, absolute_path, relative_path, source_type, metadata, size_bytes FROM files WHERE summary IS NULL AND source_type = @type ORDER BY id ASC LIMIT @limit";
            cmd.Parameters.AddWithValue("@type", sourceTypeFilter);
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        var rows = new List<UnsummarizedRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new UnsummarizedRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5)));
        }
        return rows;
    }

    /// <summary>
    /// Groups rows into batches whose total effective content stays within
    /// the summarizer's configured content budget. Rows that individually
    /// exceed the budget are placed alone in their own batch.
    /// </summary>
    private List<List<UnsummarizedRow>> BuildDynamicBatches(IReadOnlyList<UnsummarizedRow> rows)
    {
        var batches = new List<List<UnsummarizedRow>>();
        var current = new List<UnsummarizedRow>();
        int budgetUsed = 0;

        foreach (var row in rows)
        {
            var effective = (int)Math.Min(row.SizeBytes, _summarizer.InputCharLimit(row.SourceType));

            if (current.Count > 0 && budgetUsed + effective > _summarizer.ContentBudgetChars)
            {
                batches.Add(current);
                current = new List<UnsummarizedRow>();
                budgetUsed = 0;
            }

            current.Add(row);
            budgetUsed += effective;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    private void WriteSummary(long id, string relPath, string summary, string? sourceType)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _ftsDbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var txn = conn.BeginTransaction();

        // Read current path and content for the FTS re-insert
        string path, content;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = txn;
            sel.CommandText = "SELECT relative_path FROM files WHERE id = @id";
            sel.Parameters.AddWithValue("@id", id);
            path = (string)sel.ExecuteScalar()!;
        }

        // Get content from FTS (path column is the relative path; content is the body)
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = txn;
            sel.CommandText = "SELECT content FROM files_fts WHERE rowid = @id";
            sel.Parameters.AddWithValue("@id", id);
            content = (string?)sel.ExecuteScalar() ?? "";
        }

        // FTS delete + re-insert with summary
        using (var del = conn.CreateCommand())
        {
            del.Transaction = txn;
            del.CommandText = "DELETE FROM files_fts WHERE rowid = @id";
            del.Parameters.AddWithValue("@id", id);
            del.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = txn;
            ins.CommandText = "INSERT INTO files_fts(rowid, path, content, summary) VALUES (@id, @path, @content, @summary)";
            ins.Parameters.AddWithValue("@id", id);
            ins.Parameters.AddWithValue("@path", path);
            ins.Parameters.AddWithValue("@content", content);
            ins.Parameters.AddWithValue("@summary", summary);
            ins.ExecuteNonQuery();
        }

        // Update files table — write source_type only when the summarizer chose
        // a value (sourceType non-null). Skip-sentinel writes leave the column alone.
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = txn;
            if (sourceType != null)
            {
                upd.CommandText = "UPDATE files SET summary = @summary, source_type = @source_type WHERE id = @id";
                upd.Parameters.AddWithValue("@source_type", sourceType);
            }
            else
            {
                upd.CommandText = "UPDATE files SET summary = @summary WHERE id = @id";
            }
            upd.Parameters.AddWithValue("@summary", summary);
            upd.Parameters.AddWithValue("@id", id);
            upd.ExecuteNonQuery();
        }

        txn.Commit();
    }

    private static string GenerateRequestId()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
