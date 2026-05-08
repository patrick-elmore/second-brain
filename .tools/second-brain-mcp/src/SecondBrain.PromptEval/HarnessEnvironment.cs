using System.Text.Json;
using Microsoft.Extensions.Logging;
using SecondBrain.Files;
using SecondBrain.Index.Search;
using SecondBrain.Llm;

namespace SecondBrain.PromptEval;

/// <summary>
/// Resolves runtime config (DB path, API key, source folders) and constructs
/// the shared dependencies (search engine, file reader, LLM client). Each
/// subcommand receives one of these and pulls what it needs.
/// </summary>
public sealed class HarnessEnvironment
{
    public required string ConfigPath { get; init; }
    public required string FtsDbPath { get; init; }
    public required string SourcesConfigPath { get; init; }
    public required IReadOnlyList<string> AllowedRoots { get; init; }
    public required SearchEngine SearchEngine { get; init; }
    public required FileReader FileReader { get; init; }
    public required IMessageCreator Client { get; init; }
    public required string DefaultModel { get; init; }
    public required string EscalationModel { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required string StateDir { get; init; }

    public static HarnessEnvironment Resolve(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PromptEval");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configPath = Path.Combine(localAppData, "SecondBrainMcpServer", "mcp_config.json");

        if (!File.Exists(configPath))
            throw new FileNotFoundException(
                $"mcp_config.json not found at {configPath}. " +
                "Install the second-brain MCP service first.");

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var sb = doc.RootElement.GetProperty("second_brain");

        var configDir = Path.GetDirectoryName(configPath)!;
        string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(configDir, p);

        var ftsDbPath = Resolve(sb.GetProperty("fts_db_path").GetString() ?? "index/fts.db");
        var sourcesConfigPath = Resolve(sb.GetProperty("sources_config").GetString() ?? "config/sources.json");
        var defaultModel = sb.TryGetProperty("default_model", out var dm) ? dm.GetString()! : "claude-haiku-4-5";
        var escalationModel = sb.TryGetProperty("escalation_model", out var em) ? em.GetString()! : "claude-sonnet-4-6";
        var apiKeyEnv = sb.TryGetProperty("anthropic_api_key_env", out var ake) ? ake.GetString()! : "ANTHROPIC_API_KEY";
        var vertexBaseUrl = sb.TryGetProperty("vertex_base_url", out var vbu) ? vbu.GetString() ?? "" : "";

        if (!File.Exists(ftsDbPath))
            throw new FileNotFoundException($"FTS index not found at {ftsDbPath}. Run rebuild_index first.");
        if (!File.Exists(sourcesConfigPath))
            throw new FileNotFoundException($"Sources config not found at {sourcesConfigPath}.");

        // Load source folders to get allowed roots for FileReader
        var sourceFolders = new SourceConfigLoader().Load(sourcesConfigPath);
        var allowedRoots = sourceFolders.Select(f => f.AbsolutePath).ToList();

        var apiKey = Environment.GetEnvironmentVariable(apiKeyEnv) ?? "";
        var useVertex = string.Equals(
            Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX"), "1", StringComparison.Ordinal);
        if (!useVertex && string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                $"API key not set. Set {apiKeyEnv} or use Vertex (CLAUDE_CODE_USE_VERTEX=1).");

        var rawClient = ClaudeSessionFactory.BuildClient(apiKey, string.IsNullOrEmpty(vertexBaseUrl) ? null : vertexBaseUrl);
        var client = new AnthropicMessageCreator(rawClient);

        var searchEngine = new SearchEngine(ftsDbPath);
        var fileReader = new FileReader(allowedRoots);

        // State directory lives in the project source so it's tracked with the code
        // (test cases and tuning history are part of the eval suite, not transient).
        // Walk up from AppContext.BaseDirectory to find the project root.
        var stateDir = ResolveStateDir();
        Directory.CreateDirectory(stateDir);

        logger.LogInformation("Config: {Config}", configPath);
        logger.LogInformation("FTS DB: {Db}", ftsDbPath);
        logger.LogInformation("State:  {State}", stateDir);

        return new HarnessEnvironment
        {
            ConfigPath = configPath,
            FtsDbPath = ftsDbPath,
            SourcesConfigPath = sourcesConfigPath,
            AllowedRoots = allowedRoots,
            SearchEngine = searchEngine,
            FileReader = fileReader,
            Client = client,
            DefaultModel = defaultModel,
            EscalationModel = escalationModel,
            LoggerFactory = loggerFactory,
            StateDir = stateDir,
        };
    }

    private static string ResolveStateDir()
    {
        // From AppContext.BaseDirectory (e.g. .../SecondBrain.PromptEval/bin/Debug/net10.0/win-x64/)
        // walk up until we find the .csproj sibling, then state/ is alongside it.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SecondBrain.PromptEval.csproj")))
            dir = dir.Parent;

        if (dir == null)
            throw new InvalidOperationException(
                "Could not locate SecondBrain.PromptEval project directory from " + AppContext.BaseDirectory);

        return Path.Combine(dir.FullName, "state");
    }
}
