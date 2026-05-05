using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Index.Indexing;
using SecondBrain.Llm;

namespace SecondBrain.Mcp.Stats;

/// <summary>
/// Tracks tool calls, LLM API usage, file reads, and estimated cost.
/// Mirrors db-mcp's StatsTracker pattern (hourly buckets, last 24h, persisted to disk),
/// extended with second-brain-specific metrics.
/// </summary>
public sealed class StatsTracker : IStatsRecorder
{
    private readonly PricingTable _pricing;
    private readonly string _statsFilePath;
    private readonly Lock _lock = new();
    private readonly ILogger<StatsTracker> _logger;
    private readonly DateTimeOffset _processStarted = DateTimeOffset.UtcNow;
    private readonly IndexStatsProvider? _indexStatsProvider;

    // Index refresh tracking (since process start; not persisted)
    private long _totalRefreshes;
    private DateTimeOffset? _lastRefreshAt;
    private RefreshSummary? _lastRefresh;

    // Hourly buckets of MCP tool calls (last 24h).
    private readonly ConcurrentDictionary<DateTimeOffset, long> _hourlyToolCalls = new();
    private DateTimeOffset _lastSeenHour;

    // Cumulative tool call counts by name.
    private readonly ConcurrentDictionary<string, long> _toolCallsByName = new();

    // Per-model LLM accumulators.
    private readonly ConcurrentDictionary<string, ModelStats> _llmByModel = new();

    // File reads
    private long _fileReads;
    private readonly HashSet<string> _distinctFilesRead = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _statsSince;

    public StatsTracker(
        PricingTable pricing,
        string statsFilePath,
        ILogger<StatsTracker> logger,
        IndexStatsProvider? indexStatsProvider = null)
    {
        _pricing = pricing;
        _statsFilePath = statsFilePath;
        _logger = logger;
        _indexStatsProvider = indexStatsProvider;
        _statsSince = DateTimeOffset.UtcNow;
        _lastSeenHour = TruncateToHour(DateTimeOffset.UtcNow);

        var dir = Path.GetDirectoryName(_statsFilePath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        LoadFromDisk();
    }

    public void RecordIndexRefresh(int added, int modified, int removed, int unchanged, int skipped, TimeSpan elapsed)
    {
        Interlocked.Increment(ref _totalRefreshes);
        lock (_lock)
        {
            _lastRefreshAt = DateTimeOffset.UtcNow;
            _lastRefresh = new RefreshSummary(
                Added: added,
                Modified: modified,
                Removed: removed,
                Unchanged: unchanged,
                Skipped: skipped,
                ElapsedSeconds: Math.Round(elapsed.TotalSeconds, 2));
        }
    }

    // ── IStatsRecorder ──────────────────────────────────────────────────────

    public decimal RecordLlmCall(string model, long inputTokens, long outputTokens, long cacheCreationTokens, long cacheReadTokens)
    {
        var cost = _pricing.CalculateCost(model, inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);
        var stats = _llmByModel.GetOrAdd(model, _ => new ModelStats());
        lock (stats)
        {
            stats.Requests++;
            stats.InputTokens += inputTokens;
            stats.OutputTokens += outputTokens;
            stats.CacheCreationTokens += cacheCreationTokens;
            stats.CacheReadTokens += cacheReadTokens;
            stats.EstimatedCostUsd += cost;
        }
        MaybePersist();
        return cost;
    }

    public void RecordToolDispatch(string toolName)
    {
        // Internal tool dispatches (search/read_file from inside ask) — tracked separately.
        _toolCallsByName.AddOrUpdate($"internal:{toolName}", 1, (_, c) => c + 1);
    }

    public void RecordFileRead(string absolutePath)
    {
        Interlocked.Increment(ref _fileReads);
        lock (_distinctFilesRead) _distinctFilesRead.Add(absolutePath);
    }

    // ── MCP-level recording (called from SecondBrainMcpHandler) ─────────────

    public void RecordMcpToolCall(string toolName)
    {
        var now = DateTimeOffset.UtcNow;
        var hourKey = TruncateToHour(now);
        _hourlyToolCalls.AddOrUpdate(hourKey, 1, (_, c) => c + 1);
        _toolCallsByName.AddOrUpdate(toolName, 1, (_, c) => c + 1);

        if (hourKey != _lastSeenHour)
        {
            _lastSeenHour = hourKey;
            PruneOlderThan(now.AddHours(-24));
        }
        MaybePersist();
    }

    // ── Snapshot ────────────────────────────────────────────────────────────

    public object GetStats()
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-24);

        var hourly = _hourlyToolCalls
            .Where(kvp => kvp.Key >= cutoff)
            .OrderByDescending(kvp => kvp.Key)
            .Select(kvp => new { hour = kvp.Key, count = kvp.Value })
            .ToList();

        var last24h = hourly.Sum(h => h.count);
        var currentHour = _hourlyToolCalls.GetValueOrDefault(TruncateToHour(now), 0);

        var byModel = new Dictionary<string, object>();
        decimal totalCost = 0m;
        long totalRequests = 0;
        foreach (var (model, stats) in _llmByModel)
        {
            lock (stats)
            {
                byModel[model] = new
                {
                    requests = stats.Requests,
                    input_tokens = stats.InputTokens,
                    output_tokens = stats.OutputTokens,
                    cache_creation_tokens = stats.CacheCreationTokens,
                    cache_read_tokens = stats.CacheReadTokens,
                    estimated_cost_usd = Math.Round(stats.EstimatedCostUsd, 6),
                };
                totalCost += stats.EstimatedCostUsd;
                totalRequests += stats.Requests;
            }
        }

        var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        int distinctCount;
        lock (_distinctFilesRead) distinctCount = _distinctFilesRead.Count;

        var index = BuildIndexSection();

        return new
        {
            uptime = (now - _processStarted).ToString(@"d\.hh\:mm\:ss"),
            stats_since = _statsSince,
            tool_calls = new
            {
                last_24h = last24h,
                current_hour = currentHour,
                by_tool = _toolCallsByName.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                hourly,
            },
            llm = new
            {
                total_requests = totalRequests,
                total_estimated_cost_usd = Math.Round(totalCost, 6),
                by_model = byModel,
            },
            files = new
            {
                total_reads = Interlocked.Read(ref _fileReads),
                distinct_files = distinctCount,
            },
            index,
            memory = new
            {
                working_set_mb = Math.Round(process.WorkingSet64 / 1048576.0, 1),
                gc_heap_mb = Math.Round(gcInfo.HeapSizeBytes / 1048576.0, 1),
                gen0_collections = GC.CollectionCount(0),
                gen1_collections = GC.CollectionCount(1),
                gen2_collections = GC.CollectionCount(2),
            },
        };
    }

    private object BuildIndexSection()
    {
        IndexStatsSnapshot? snap = null;
        try
        {
            snap = _indexStatsProvider?.Snapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IndexStatsProvider.Snapshot failed");
        }

        RefreshSummary? lastRefresh;
        DateTimeOffset? lastRefreshAt;
        long totalRefreshes;
        lock (_lock)
        {
            lastRefresh = _lastRefresh;
            lastRefreshAt = _lastRefreshAt;
            totalRefreshes = Interlocked.Read(ref _totalRefreshes);
        }

        if (snap == null)
        {
            return new
            {
                exists = false,
                refresh = new
                {
                    total = totalRefreshes,
                    last_at = lastRefreshAt,
                    last = lastRefresh,
                },
            };
        }

        return new
        {
            exists = snap.Exists,
            file_count = snap.FileCount,
            total_indexed_bytes = snap.TotalIndexedBytes,
            db_file_bytes = snap.DbFileSizeBytes,
            db_file_mtime = snap.DbFileMTime,
            last_indexed_at = snap.LastIndexedAt,
            by_source_folder = snap.BySourceFolder
                .Select(b => new { source_folder_id = b.Key, count = b.Count }),
            by_source_type = snap.BySourceType
                .Select(b => new { source_type = b.Key, count = b.Count }),
            refresh = new
            {
                total = totalRefreshes,
                last_at = lastRefreshAt,
                last = lastRefresh,
            },
        };
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    private int _writesSinceFlush;
    private const int FlushEveryN = 5;

    private void MaybePersist()
    {
        if (Interlocked.Increment(ref _writesSinceFlush) >= FlushEveryN)
        {
            Interlocked.Exchange(ref _writesSinceFlush, 0);
            PersistToDisk();
        }
    }

    public void PersistToDisk()
    {
        lock (_lock)
        {
            try
            {
                var snapshot = new StatsSnapshot
                {
                    StatsSince = _statsSince,
                    HourlyToolCalls = _hourlyToolCalls
                        .OrderByDescending(kvp => kvp.Key)
                        .Select(kvp => new HourlyCount { Hour = kvp.Key, Count = kvp.Value })
                        .ToList(),
                    ToolCallsByName = new Dictionary<string, long>(_toolCallsByName),
                    LlmByModel = _llmByModel.ToDictionary(kvp => kvp.Key, kvp =>
                    {
                        lock (kvp.Value) return new ModelStats
                        {
                            Requests = kvp.Value.Requests,
                            InputTokens = kvp.Value.InputTokens,
                            OutputTokens = kvp.Value.OutputTokens,
                            CacheCreationTokens = kvp.Value.CacheCreationTokens,
                            CacheReadTokens = kvp.Value.CacheReadTokens,
                            EstimatedCostUsd = kvp.Value.EstimatedCostUsd,
                        };
                    }),
                    TotalFileReads = Interlocked.Read(ref _fileReads),
                    DistinctFilesRead = _distinctFilesRead.ToList(),
                };

                File.WriteAllText(_statsFilePath, JsonSerializer.Serialize(snapshot, WriteOpts));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist stats to disk");
            }
        }
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_statsFilePath))
            return;

        try
        {
            var json = File.ReadAllText(_statsFilePath);
            var snapshot = JsonSerializer.Deserialize<StatsSnapshot>(json, WriteOpts);
            if (snapshot == null) return;

            _statsSince = snapshot.StatsSince == default ? DateTimeOffset.UtcNow : snapshot.StatsSince;

            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            foreach (var entry in snapshot.HourlyToolCalls.Where(e => e.Hour >= cutoff))
                _hourlyToolCalls[entry.Hour] = entry.Count;

            foreach (var (k, v) in snapshot.ToolCallsByName)
                _toolCallsByName[k] = v;

            foreach (var (k, v) in snapshot.LlmByModel)
                _llmByModel[k] = v;

            Interlocked.Exchange(ref _fileReads, snapshot.TotalFileReads);
            foreach (var path in snapshot.DistinctFilesRead)
                _distinctFilesRead.Add(path);

            _logger.LogInformation("Loaded stats from disk (since {StatsSince})", _statsSince);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stats from disk; starting fresh");
        }
    }

    private void PruneOlderThan(DateTimeOffset cutoff)
    {
        foreach (var key in _hourlyToolCalls.Keys.Where(k => k < cutoff).ToList())
            _hourlyToolCalls.TryRemove(key, out _);
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Offset);

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class StatsSnapshot
    {
        public DateTimeOffset StatsSince { get; set; }
        public List<HourlyCount> HourlyToolCalls { get; set; } = [];
        public Dictionary<string, long> ToolCallsByName { get; set; } = [];
        public Dictionary<string, ModelStats> LlmByModel { get; set; } = [];
        public long TotalFileReads { get; set; }
        public List<string> DistinctFilesRead { get; set; } = [];
    }

    private sealed class HourlyCount
    {
        public DateTimeOffset Hour { get; set; }
        public long Count { get; set; }
    }

    private sealed class ModelStats
    {
        public long Requests { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public decimal EstimatedCostUsd { get; set; }
    }

    private sealed record RefreshSummary(
        int Added,
        int Modified,
        int Removed,
        int Unchanged,
        int Skipped,
        double ElapsedSeconds);
}
