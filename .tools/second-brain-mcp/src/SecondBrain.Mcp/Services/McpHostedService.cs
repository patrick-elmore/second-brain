using Microsoft.Extensions.Options;
using SecondBrain.Files;
using SecondBrain.Index.Indexing;
using SecondBrain.Index.Search;
using SecondBrain.Llm;
using SecondBrain.Mcp.Configuration;
using SecondBrain.Mcp.Handler;
using SecondBrain.Mcp.Stats;

namespace SecondBrain.Mcp.Services;

public sealed class McpHostedService : IHostedService
{
    private readonly McpSettings _settings;
    private readonly McpServiceState _state;
    private readonly ILogger<McpHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public McpHostedService(
        IOptions<McpSettings> settings,
        McpServiceState state,
        ILogger<McpHostedService> logger,
        ILoggerFactory loggerFactory)
    {
        _settings = settings.Value;
        _state = state;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting {DisplayName}...", _settings.DisplayName);

        var sb = _settings.SecondBrain;

        // Resolve paths relative to the executable directory
        var baseDir = AppContext.BaseDirectory;
        string Resolve(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);

        var ftsDbPath = Resolve(sb.FtsDbPath);
        var requestsDbPath = Resolve(sb.RequestsDbPath);
        var sessionStatePath = Resolve(sb.SessionStatePath);
        var sourcesConfig = Resolve(sb.SourcesConfig);

        // Build stats layer
        var pricingPath = Resolve(Path.Combine("config", "pricing.json"));
        var statsPath = Resolve(Path.Combine("logs", "stats.json"));
        var pricing = PricingTable.Load(pricingPath);
        var indexStatsProvider = new IndexStatsProvider(ftsDbPath);
        var statsTracker = new StatsTracker(
            pricing,
            statsPath,
            _loggerFactory.CreateLogger<StatsTracker>(),
            indexStatsProvider);
        _state.StatsTracker = statsTracker;

        // Build files layer
        var configLoader = new SourceConfigLoader();
        var sourceFolders = File.Exists(sourcesConfig)
            ? configLoader.Load(sourcesConfig)
            : [];
        var allowedRoots = sourceFolders.Select(f => f.AbsolutePath).ToList();
        var fileReader = new FileReader(allowedRoots);

        // Build index layer
        var searchEngine = new SearchEngine(ftsDbPath);
        var requestHistory = new SecondBrain.Index.RequestHistory.RequestHistory(requestsDbPath);

        // Build LLM layer
        var apiKey = sb.ResolveApiKey();
        var useVertex = string.Equals(
            Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX"), "1", StringComparison.Ordinal);
        if (!useVertex && string.IsNullOrEmpty(apiKey))
            _logger.LogWarning("ANTHROPIC_API_KEY not set and CLAUDE_CODE_USE_VERTEX is not 1; ask tool will fail");

        var session = ClaudeSessionFactory.Create(
            apiKey: apiKey,
            searchEngine: searchEngine,
            fileReader: fileReader,
            statePath: sessionStatePath,
            stateBackupCount: sb.StateBackupCount,
            defaultModel: sb.DefaultModel,
            escalationModel: sb.EscalationModel,
            compactThresholdTokens: sb.CompactThresholdTokens,
            persistEveryNMessages: sb.StatePersistEveryNMessages,
            vertexBaseUrl: sb.VertexBaseUrl,
            logger: _logger,
            stats: statsTracker);

        // Build a separate client for the document summarizer (same credentials,
        // separate instance so it doesn't share state with the session client).
        var summarizerRawClient = ClaudeSessionFactory.BuildClient(apiKey, sb.VertexBaseUrl);
        var summarizerClient = new AnthropicMessageCreator(summarizerRawClient);
        var summarizer = new DocumentSummarizer(summarizerClient, _logger, statsTracker);

        var handler = new SecondBrainMcpHandler(
            session: session,
            searchEngine: searchEngine,
            requestHistory: requestHistory,
            sourcesConfigPath: sourcesConfig,
            ftsDbPath: ftsDbPath,
            indexMaxBytes: sb.IndexMaxBytes,
            fileReader: fileReader,
            summarizer: summarizer,
            mcpTimeoutSeconds: _settings.McpTimeout,
            summarizeSafetyBufferSeconds: sb.SummarizeSafetyBufferSeconds,
            logger: _logger,
            stats: statsTracker);

        await handler.StartAsync(cancellationToken);
        _state.Handler = handler;

#if DEBUG
        // Simulate anomalous refresh so the override alert can be tested on /stats.
        statsTracker.SetAnomalousRefresh(247);
#endif

        _logger.LogInformation("{DisplayName} started on port {Port}",
            _settings.DisplayName, _settings.HttpPort);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_state.Handler != null)
            await _state.Handler.StopAsync(cancellationToken);

        _state.StatsTracker?.PersistToDisk();

        _logger.LogInformation("{DisplayName} stopped", _settings.DisplayName);
    }
}
