using System.Text.Json.Nodes;

namespace SecondBrain.Mcp.Handler;

/// <summary>
/// MCP protocol-level tool definitions (JSON-RPC schema objects, not Anthropic SDK Tool objects).
/// </summary>
internal static class McpToolSchemas
{
    public static JsonArray BuildToolList() => new(
        SearchTool(),
        AskTool(),
        CompactSessionTool(),
        ResetSessionTool(),
        SessionInfoTool(),
        GetRequestTool(),
        RebuildIndexTool()
    );

    private static JsonObject SearchTool() => new()
    {
        ["name"] = "search",
        ["description"] = "Deterministic FTS5 search with structured filters. Returns paths, snippets, and metadata. No LLM in the loop. Always returns a request_id.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = Prop("string", "FTS5 query string (optional if filtering only)"),
                ["date_start"] = Prop("string", "Start date filter (YYYY-MM-DD)"),
                ["date_end"] = Prop("string", "End date filter (YYYY-MM-DD)"),
                ["people"] = ArrayProp("Filter by attendees in frontmatter"),
                ["source_type"] = ArrayProp("transcript|standup|1on1|planning|note|..."),
                ["source_folders"] = ArrayProp("Limit to specific source folder IDs"),
                ["top"] = Prop("integer", "Max results (default 30)"),
                ["snippet_tokens"] = Prop("integer", "Tokens per snippet (default 32)"),
                ["return_mode"] = Prop("string", "paths or snippets (default)"),
                ["list_sources"] = Prop("boolean", "When true, include sources_summary"),
            },
        },
    };

    private static JsonObject AskTool() => new()
    {
        ["name"] = "ask",
        ["description"] = "Routes the question through the persistent Claude session. The session has full prior context across calls.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("question"),
            ["properties"] = new JsonObject
            {
                ["question"] = Prop("string", "Question to answer using the knowledge corpus"),
                ["compact_instruction"] = Prop("string", "If provided, compact session before answering"),
                ["effort"] = Prop("string", "low (default; haiku/high), medium (sonnet/low), high (sonnet/high). Scales model + API thinking effort."),
            },
        },
    };

    private static JsonObject CompactSessionTool() => new()
    {
        ["name"] = "compact_session",
        ["description"] = "Triggers compaction of the persistent Claude session.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["instruction"] = Prop("string", "Custom compaction prompt; defaults to standard if omitted"),
            },
        },
    };

    private static JsonObject ResetSessionTool() => new()
    {
        ["name"] = "reset_session",
        ["description"] = "Clears the persistent session state. Next ask starts fresh.",
        ["inputSchema"] = new JsonObject { ["type"] = "object" },
    };

    private static JsonObject SessionInfoTool() => new()
    {
        ["name"] = "session_info",
        ["description"] = "Returns metadata about the current persistent session state.",
        ["inputSchema"] = new JsonObject { ["type"] = "object" },
    };

    private static JsonObject GetRequestTool() => new()
    {
        ["name"] = "get_request",
        ["description"] = "Retrieves a stored request/response entity by ID.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("request_id"),
            ["properties"] = new JsonObject
            {
                ["request_id"] = Prop("string", "Request ID returned by search or ask"),
                ["fields"] = ArrayProp("Optional fields to return: query, filters, timestamp, tool, files, synthesis, result_count"),
            },
        },
    };

    private static JsonObject RebuildIndexTool() => new()
    {
        ["name"] = "rebuild_index",
        ["description"] = "Triggers a full rebuild of the FTS5 index. Stubbed in v1.",
        ["inputSchema"] = new JsonObject { ["type"] = "object" },
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject ArrayProp(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };
}
