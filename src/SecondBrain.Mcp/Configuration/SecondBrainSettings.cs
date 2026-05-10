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
    public int IndexMaxBytes { get; set; } = 500_000;

    [JsonPropertyName("max_tool_turns")]
    public int MaxToolTurns { get; set; } = 25;

    [JsonPropertyName("max_read_file_bytes")]
    public int MaxReadFileBytes { get; set; } = 131_072;

    /// <summary>
    /// Default max output tokens for LLM calls. Used by the tool loop (via
    /// EffortConfig as the base before adding any thinking budget), the
    /// compactor, and the document summarizer's per-batch ceiling.
    /// </summary>
    [JsonPropertyName("base_output_tokens")]
    public int BaseOutputTokens { get; set; } = 8_192;

    /// <summary>
    /// Max output tokens for the compactor's one-shot summarization call.
    /// Defaults to BaseOutputTokens; override only if the compactor needs a
    /// different ceiling than the rest of the pipeline.
    /// </summary>
    [JsonPropertyName("compactor_max_output_tokens")]
    public int CompactorMaxOutputTokens { get; set; } = 8_192;

    /// <summary>
    /// Per-API-call input budget for the document summarizer, in characters.
    /// Larger documents are truncated according to <see cref="SummarizerInputCharLimits"/>.
    /// </summary>
    [JsonPropertyName("summarizer_content_budget_chars")]
    public int SummarizerContentBudgetChars { get; set; } = 80_000;

    /// <summary>
    /// Per-source-type cap on document content fed into the summarizer, in
    /// characters. Keys match the source_type values emitted by the indexer.
    /// The "default" key is used when a document's source type is not listed.
    /// </summary>
    [JsonPropertyName("summarizer_input_char_limits")]
    public Dictionary<string, int> SummarizerInputCharLimits { get; set; } = new()
    {
        ["1on1"] = 24_000,
        ["transcript"] = 20_000,
        ["standup"] = 6_000,
        ["planning"] = 16_000,
        ["note"] = 8_000,
        ["default"] = 12_000,
    };

    /// <summary>
    /// Hard cap on the snippet token count requested by callers of search.
    /// Larger requests are clamped down to this value at the engine layer.
    /// </summary>
    [JsonPropertyName("search_max_snippet_tokens")]
    public int SearchMaxSnippetTokens { get; set; } = 64;

    /// <summary>
    /// Lower bound on per-variant overfetch in multi-query (RRF) search. Each
    /// variant fetches max(min, top * 2) hits before fusion.
    /// </summary>
    [JsonPropertyName("search_per_variant_overfetch_min")]
    public int SearchPerVariantOverfetchMin { get; set; } = 30;

    /// <summary>
    /// Upper bound on per-variant overfetch in multi-query (RRF) search. Caps
    /// the per-variant fetch so a high <c>top</c> doesn't blow out the cost
    /// per fused query.
    /// </summary>
    [JsonPropertyName("search_per_variant_overfetch_max")]
    public int SearchPerVariantOverfetchMax { get; set; } = 50;

    /// <summary>
    /// File-change threshold above which the background index refresh treats
    /// the run as anomalous: summarization is blocked and an alert is raised
    /// on /stats. Protects against runaway summarization cost when the corpus
    /// changes en masse.
    /// </summary>
    [JsonPropertyName("index_anomaly_change_threshold")]
    public int IndexAnomalyChangeThreshold { get; set; } = 200;

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
