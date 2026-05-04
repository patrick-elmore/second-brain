using System.Text.Json;
using Anthropic.Models.Messages;

namespace SecondBrain.Llm;

internal static class ToolDefinitions
{
    public static IReadOnlyList<ToolUnion> Build()
    {
        // Cache breakpoint on the last tool — caches tool definitions across calls.
        // Tools are stable, so this gives a guaranteed cache hit prefix.
        return [SearchTool(), ReadFileToolCached()];
    }

    private static ToolUnion ReadFileToolCached()
    {
        var tool = (Tool)((ToolUnion)ReadFileTool()).Value!;
        return tool with { CacheControl = new CacheControlEphemeral() };
    }

    private static ToolUnion SearchTool() => new Tool
    {
        Name = "search",
        Description = """
            Search the local knowledge corpus using FTS5 full-text search with optional
            structured filters. Returns file paths and snippets. Use this to locate
            relevant documents before deciding whether to read them in full.
            """,
        InputSchema = new InputSchema
        {
            Type = JsonSerializer.SerializeToElement("object"),
            Properties = new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "FTS5 query string. Supports AND (space), OR, phrase quotes, prefix*."
                }),
                ["date_start"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    format = "date",
                    description = "Filter files with metadata.created >= this date (YYYY-MM-DD)."
                }),
                ["date_end"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    format = "date",
                    description = "Filter files with metadata.created <= this date (YYYY-MM-DD)."
                }),
                ["people"] = JsonSerializer.SerializeToElement(new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Filter by attendees in frontmatter (partial match, e.g. email or name)."
                }),
                ["source_type"] = JsonSerializer.SerializeToElement(new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Filter by source type: transcript, standup, 1on1, planning, note."
                }),
                ["source_folders"] = JsonSerializer.SerializeToElement(new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Limit search to specific source folder IDs."
                }),
                ["top"] = JsonSerializer.SerializeToElement(new
                {
                    type = "integer",
                    description = "Maximum results to return. Default 30."
                }),
                ["return_mode"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    @enum = new[] { "snippets", "paths" },
                    description = "Return snippets (default) or just file paths."
                }),
            },
        },
    };

    private static ToolUnion ReadFileTool() => new Tool
    {
        Name = "read_file",
        Description = """
            Read the full content of a file by absolute path. Only use this when a
            search snippet is insufficient and you need the complete document.
            The path must be within a configured source folder.
            """,
        InputSchema = new InputSchema
        {
            Type = JsonSerializer.SerializeToElement("object"),
            Properties = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "Absolute path to the file to read."
                }),
            },
            Required = ["path"],
        },
    };
}
