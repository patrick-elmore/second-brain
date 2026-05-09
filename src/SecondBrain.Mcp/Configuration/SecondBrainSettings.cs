using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecondBrain.Mcp.Configuration;

public sealed class McpSettings
{
    [JsonPropertyName("service_name")]
    public string ServiceName { get; set; } = "SecondBrainHttpMcp";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "Second Brain HTTP MCP";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "Persistent-session knowledge retrieval over local source folders";

    [JsonPropertyName("http_host")]
    public string HttpHost { get; set; } = "0.0.0.0";

    [JsonPropertyName("http_port")]
    public int HttpPort { get; set; } = 9998;

    [JsonPropertyName("mcp_timeout")]
    public int McpTimeout { get; set; } = 120;

    [JsonPropertyName("log_level")]
    public string LogLevel { get; set; } = "INFO";

    [JsonPropertyName("enable_logging")]
    public bool EnableLogging { get; set; } = true;

    [JsonPropertyName("second_brain")]
    public SecondBrainSettings SecondBrain { get; set; } = new();

    public static McpSettings Load(string configPath)
    {
        if (!File.Exists(configPath))
            return new McpSettings();

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<McpSettings>(json) ?? new McpSettings();
    }
}

public sealed class SecondBrainSettings
{
    [JsonPropertyName("anthropic_api_key_env")]
    public string AnthropicApiKeyEnv { get; set; } = "ANTHROPIC_API_KEY";

    [JsonPropertyName("fts_db_path")]
    public string FtsDbPath { get; set; } = "index/fts.db";

    [JsonPropertyName("requests_db_path")]
    public string RequestsDbPath { get; set; } = "index/requests.db";

    [JsonPropertyName("session_state_path")]
    public string SessionStatePath { get; set; } = "index/session-state.json";

    [JsonPropertyName("sources_config")]
    public string SourcesConfig { get; set; } = "config/sources.json";

    [JsonPropertyName("default_model")]
    public string DefaultModel { get; set; } = "claude-haiku-4-5";

    [JsonPropertyName("escalation_model")]
    public string EscalationModel { get; set; } = "claude-sonnet-4-6";

    [JsonPropertyName("compact_threshold_tokens")]
    public long CompactThresholdTokens { get; set; } = 150_000;

    [JsonPropertyName("state_persist_every_n_messages")]
    public int StatePersistEveryNMessages { get; set; } = 5;

    [JsonPropertyName("state_backup_count")]
    public int StateBackupCount { get; set; } = 5;

    [JsonPropertyName("index_max_bytes")]
    public int IndexMaxBytes { get; set; } = 5_000_000;

    [JsonPropertyName("index_refresh_interval_seconds")]
    public int IndexRefreshIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Seconds before <c>McpTimeout</c> expires at which the summarizer stops
    /// dispatching new batch waves. Ensures in-flight requests complete and the
    /// response is returned before the MCP connection times out.
    /// </summary>
    [JsonPropertyName("summarize_safety_buffer_seconds")]
    public int SummarizeSafetyBufferSeconds { get; set; } = 30;

    /// <summary>
    /// Optional override for the Vertex base URL. Used to route requests through a
    /// local proxy. The Vertex SDK derives BaseUrl from region in its constructor,
    /// so this is applied via the client's init-only BaseUrl property after
    /// construction. Empty/null = use the SDK-derived Google URL.
    /// </summary>
    [JsonPropertyName("vertex_base_url")]
    public string VertexBaseUrl { get; set; } = "";

    public string ResolveApiKey() =>
        Environment.GetEnvironmentVariable(AnthropicApiKeyEnv) ?? string.Empty;
}
