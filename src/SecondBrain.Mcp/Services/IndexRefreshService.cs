using Microsoft.Extensions.Options;
using SecondBrain.Index.Indexing;
using SecondBrain.Mcp.Configuration;
using SecondBrain.Mcp.Stats;

namespace SecondBrain.Mcp.Services;

/// <summary>
/// Background loop that periodically runs the incremental index updater so the FTS
/// index stays current with on-disk changes without manual <c>rebuild_index</c> calls.
/// Disabled when <c>index_refresh_interval_seconds</c> is 0. Concurrent rebuilds with
/// MCP-driven calls are handled by SQLite's writer lock — they serialise; we don't
/// coordinate explicitly.
/// </summary>
public sealed class IndexRefreshService : BackgroundService
{
    private readonly McpSettings _settings;
    private readonly ILogger<IndexRefreshService> _logger;
    private readonly McpServiceState _state;

    public IndexRefreshService(
        IOptions<McpSettings> settings,
        ILogger<IndexRefreshService> logger,
        McpServiceState state)
    {
        _settings = settings.Value;
        _logger = logger;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sb = _settings.SecondBrain;
        var intervalSeconds = sb.IndexRefreshIntervalSeconds;

        if (intervalSeconds <= 0)
        {
            _logger.LogInformation("Index refresh service disabled (index_refresh_interval_seconds=0)");
            return;
        }

        var baseDir = AppContext.BaseDirectory;
        string Resolve(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);

        var sourcesConfig = Resolve(sb.SourcesConfig);
        var ftsDbPath = Resolve(sb.FtsDbPath);

        _logger.LogInformation(
            "Index refresh service started (interval={Interval}s)", intervalSeconds);

        // Fire once on startup to catch any drift from while the service was down.
        await RunRefreshAsync(sourcesConfig, ftsDbPath, sb.IndexMaxBytes, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunRefreshAsync(sourcesConfig, ftsDbPath, sb.IndexMaxBytes, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Service shutdown — expected.
        }
    }

    private Task RunRefreshAsync(string sourcesConfig, string ftsDbPath, int maxBytes, CancellationToken ct)
    {
        // Run on a thread-pool thread so the synchronous IndexUpdater doesn't block
        // the hosted-service execution context any longer than necessary.
        return Task.Run(() =>
        {
            try
            {
                var updater = new IndexUpdater();
                var summary = updater.Update(sourcesConfig, ftsDbPath, maxBytes,
                    frontmatterDateFolders: _settings.SecondBrain.FrontmatterDateFolders);

                _state.StatsTracker?.RecordIndexRefresh(
                    summary.Added, summary.Modified, summary.Removed,
                    summary.Unchanged, summary.Skipped, summary.Elapsed);

                var changed = summary.Added + summary.Modified;
                if (changed > 0 || summary.Removed > 0)
                {
                    _logger.LogInformation(
                        "Index auto-refresh: added={Added} modified={Modified} removed={Removed} unchanged={Unchanged} skipped={Skipped} elapsed={Elapsed}",
                        summary.Added, summary.Modified, summary.Removed,
                        summary.Unchanged, summary.Skipped, summary.Elapsed);
                }
                else
                {
                    _logger.LogDebug(
                        "Index auto-refresh: no changes (unchanged={Unchanged} skipped={Skipped} elapsed={Elapsed})",
                        summary.Unchanged, summary.Skipped, summary.Elapsed);
                }

                if (changed > _settings.SecondBrain.IndexAnomalyChangeThreshold)
                {
                    _state.StatsTracker?.SetAnomalousRefresh(changed);
                    _logger.LogWarning(
                        "Index auto-refresh: {Changed} files added/modified — exceeds anomaly threshold ({Threshold}). Summarization blocked pending override.",
                        changed,
                        _settings.SecondBrain.IndexAnomalyChangeThreshold);
                }
                else if (changed > 0)
                {
                    _state.StatsTracker?.ClearAnomalousRefresh();
                    var started = _state.Handler?.TryStartSummarization() ?? false;
                    if (started)
                        _logger.LogInformation("Index auto-refresh: triggered summarization for {Changed} new/modified files", changed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Index auto-refresh failed; loop continues");
            }
        }, ct);
    }
}
