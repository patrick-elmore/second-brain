using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecondBrain.Mcp.Stats;

/// <summary>
/// Renders the StatsTracker.GetStats() snapshot as a minimal HTML dashboard.
/// Reads from JsonNode rather than the anonymous object so it stays decoupled
/// from the snapshot's exact .NET type.
/// </summary>
internal static class StatsHtmlRenderer
{
    public static string Render(object statsSnapshot)
    {
        var node = JsonSerializer.SerializeToNode(statsSnapshot)?.AsObject()
            ?? new JsonObject();

        var sb = new StringBuilder(8192);
        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <title>second-brain stats</title>
            <style>
            html { background: #1a1a1a; }
            body { font-family: system-ui, -apple-system, sans-serif; max-width: 1000px; margin: 1.5rem auto; padding: 0 1rem; color: #e0e0e0; background: #1a1a1a; }
            h1 { font-size: 1.4rem; margin: 0 0 .25rem; color: #fff; }
            h2 { font-size: 1.05rem; margin: 1.75rem 0 .25rem; border-bottom: 1px solid #333; padding-bottom: .25rem; color: #fff; }
            h3 { font-size: .95rem; margin: 1.25rem 0 .25rem; color: #aaa; }
            table { border-collapse: collapse; width: 100%; margin-top: .25rem; }
            th, td { text-align: left; padding: .3rem .55rem; border-bottom: 1px solid #2a2a2a; font-variant-numeric: tabular-nums; }
            th { background: #262626; font-weight: 600; color: #ddd; }
            td.num, th.num { text-align: right; }
            td.mono { font-family: ui-monospace, "SF Mono", Consolas, monospace; font-size: .92rem; }
            .muted { color: #888; font-size: .9rem; }
            .pair { margin: .25rem 0; }
            .pair .k { color: #999; margin-right: .5rem; }
            .pair .v { font-weight: 600; color: #fff; }
            footer { margin-top: 2rem; font-size: .85rem; color: #777; }
            footer a { color: #9ab; text-decoration: none; }
            footer a:hover { text-decoration: underline; }
            </style>
            </head><body>
            """);

        sb.Append("<h1>second-brain stats</h1>");
        sb.Append("<p class=\"muted\">uptime: ")
          .Append(Esc(node["uptime"]?.GetValue<string>()))
          .Append(" · stats since: ")
          .Append(Esc(FormatTimestamp(node["stats_since"])))
          .Append("</p>");

        RenderLlm(sb, node["llm"]);
        RenderIndex(sb, node["index"]);
        RenderToolCalls(sb, node["tool_calls"]);
        RenderFiles(sb, node["files"]);
        RenderMemory(sb, node["memory"]);

        sb.Append("<footer><a href=\"/stats.json\">raw JSON</a> · <a href=\"/health\">health</a></footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void RenderLlm(StringBuilder sb, JsonNode? llm)
    {
        if (llm is not JsonObject obj) return;

        sb.Append("<h2>LLM</h2>");
        sb.Append("<div class=\"pair\"><span class=\"k\">total cost</span><span class=\"v\">")
          .Append(FormatUsd(obj["total_estimated_cost_usd"]))
          .Append("</span></div>");
        sb.Append("<div class=\"pair\"><span class=\"k\">total requests</span><span class=\"v\">")
          .Append(FormatLong(obj["total_requests"]))
          .Append("</span></div>");

        if (obj["by_model"] is not JsonObject byModel || byModel.Count == 0)
        {
            sb.Append("<p class=\"muted\">no model usage yet.</p>");
            return;
        }

        sb.Append("""
            <table>
            <tr>
              <th>model</th>
              <th class="num">requests</th>
              <th class="num">input</th>
              <th class="num">output</th>
              <th class="num">cache create</th>
              <th class="num">cache read</th>
              <th class="num">cost (USD)</th>
            </tr>
            """);

        foreach (var (model, val) in byModel)
        {
            if (val is not JsonObject m) continue;
            sb.Append("<tr><td class=\"mono\">").Append(Esc(model)).Append("</td>")
              .Append("<td class=\"num\">").Append(FormatLong(m["requests"])).Append("</td>")
              .Append("<td class=\"num\">").Append(FormatLong(m["input_tokens"])).Append("</td>")
              .Append("<td class=\"num\">").Append(FormatLong(m["output_tokens"])).Append("</td>")
              .Append("<td class=\"num\">").Append(FormatLong(m["cache_creation_tokens"])).Append("</td>")
              .Append("<td class=\"num\">").Append(FormatLong(m["cache_read_tokens"])).Append("</td>")
              .Append("<td class=\"num\">").Append(FormatUsd(m["estimated_cost_usd"])).Append("</td></tr>");
        }

        sb.Append("</table>");
    }

    private static void RenderToolCalls(StringBuilder sb, JsonNode? toolCalls)
    {
        if (toolCalls is not JsonObject obj) return;

        sb.Append("<h2>Tool calls</h2>");
        sb.Append("<div class=\"pair\"><span class=\"k\">last 24h</span><span class=\"v\">")
          .Append(FormatLong(obj["last_24h"]))
          .Append("</span> · <span class=\"k\">current hour</span><span class=\"v\">")
          .Append(FormatLong(obj["current_hour"]))
          .Append("</span></div>");

        if (obj["by_tool"] is JsonObject byTool && byTool.Count > 0)
        {
            sb.Append("<h3>By tool (cumulative)</h3>");
            sb.Append("<table><tr><th>tool</th><th class=\"num\">count</th></tr>");
            foreach (var (name, val) in byTool)
            {
                sb.Append("<tr><td class=\"mono\">").Append(Esc(name)).Append("</td>")
                  .Append("<td class=\"num\">").Append(FormatLong(val)).Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        if (obj["hourly"] is JsonArray hourly && hourly.Count > 0)
        {
            sb.Append("<h3>Hourly (last 24h)</h3>");
            sb.Append("<table><tr><th>hour</th><th class=\"num\">calls</th></tr>");
            foreach (var entry in hourly)
            {
                if (entry is not JsonObject h) continue;
                sb.Append("<tr><td class=\"mono\">").Append(Esc(FormatTimestamp(h["hour"]))).Append("</td>")
                  .Append("<td class=\"num\">").Append(FormatLong(h["count"])).Append("</td></tr>");
            }
            sb.Append("</table>");
        }
    }

    private static void RenderIndex(StringBuilder sb, JsonNode? index)
    {
        if (index is not JsonObject obj) return;

        sb.Append("<h2>Index</h2>");

        var exists = obj["exists"]?.GetValue<bool>() ?? false;
        if (!exists)
        {
            sb.Append("<p class=\"muted\">Index database does not exist yet.</p>");
            RenderRefreshSubsection(sb, obj["refresh"]);
            return;
        }

        sb.Append("<div class=\"pair\"><span class=\"k\">files indexed</span><span class=\"v\">")
          .Append(FormatLong(obj["file_count"]))
          .Append("</span> · <span class=\"k\">indexed bytes</span><span class=\"v\">")
          .Append(FormatBytes(obj["total_indexed_bytes"]))
          .Append("</span> · <span class=\"k\">db file</span><span class=\"v\">")
          .Append(FormatBytes(obj["db_file_bytes"]))
          .Append("</span></div>");

        sb.Append("<div class=\"pair\"><span class=\"k\">last indexed row</span><span class=\"v\">")
          .Append(Esc(FormatTimestamp(obj["last_indexed_at"])))
          .Append("</span> · <span class=\"k\">db file mtime</span><span class=\"v\">")
          .Append(Esc(FormatTimestamp(obj["db_file_mtime"])))
          .Append("</span></div>");

        if (obj["by_source_folder"] is JsonArray bySrc && bySrc.Count > 0)
        {
            sb.Append("<h3>By source folder</h3>");
            sb.Append("<table><tr><th>source_folder_id</th><th class=\"num\">files</th></tr>");
            foreach (var entry in bySrc)
            {
                if (entry is not JsonObject e) continue;
                sb.Append("<tr><td class=\"mono\">").Append(Esc(e["source_folder_id"]?.GetValue<string>() ?? "")).Append("</td>")
                  .Append("<td class=\"num\">").Append(FormatLong(e["count"])).Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        if (obj["by_source_type"] is JsonArray byType && byType.Count > 0)
        {
            sb.Append("<h3>By source type</h3>");
            sb.Append("<table><tr><th>source_type</th><th class=\"num\">files</th></tr>");
            foreach (var entry in byType)
            {
                if (entry is not JsonObject e) continue;
                sb.Append("<tr><td class=\"mono\">").Append(Esc(e["source_type"]?.GetValue<string>() ?? "")).Append("</td>")
                  .Append("<td class=\"num\">").Append(FormatLong(e["count"])).Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        RenderRefreshSubsection(sb, obj["refresh"]);
    }

    private static void RenderRefreshSubsection(StringBuilder sb, JsonNode? refresh)
    {
        if (refresh is not JsonObject obj) return;

        sb.Append("<h3>Auto-refresh</h3>");
        sb.Append("<div class=\"pair\"><span class=\"k\">refreshes since start</span><span class=\"v\">")
          .Append(FormatLong(obj["total"]))
          .Append("</span> · <span class=\"k\">last run</span><span class=\"v\">")
          .Append(Esc(FormatTimestamp(obj["last_at"])))
          .Append("</span></div>");

        if (obj["last"] is JsonObject last)
        {
            sb.Append("<div class=\"pair\"><span class=\"k\">last run delta</span><span class=\"v\">")
              .Append("added ").Append(FormatLong(last["added"]))
              .Append(" · modified ").Append(FormatLong(last["modified"]))
              .Append(" · removed ").Append(FormatLong(last["removed"]))
              .Append(" · unchanged ").Append(FormatLong(last["unchanged"]))
              .Append(" · skipped ").Append(FormatLong(last["skipped"]))
              .Append(" (").Append(FormatDouble(last["elapsed_seconds"])).Append("s)")
              .Append("</span></div>");
        }
    }

    private static void RenderFiles(StringBuilder sb, JsonNode? files)
    {
        if (files is not JsonObject obj) return;

        sb.Append("<h2>Files</h2>");
        sb.Append("<div class=\"pair\"><span class=\"k\">total reads</span><span class=\"v\">")
          .Append(FormatLong(obj["total_reads"]))
          .Append("</span> · <span class=\"k\">distinct files</span><span class=\"v\">")
          .Append(FormatLong(obj["distinct_files"]))
          .Append("</span></div>");
    }

    private static void RenderMemory(StringBuilder sb, JsonNode? memory)
    {
        if (memory is not JsonObject obj) return;

        sb.Append("<h2>Memory</h2>");
        sb.Append("<table>");
        sb.Append("<tr><td>working set</td><td class=\"num\">")
          .Append(FormatDouble(obj["working_set_mb"])).Append(" MB</td></tr>");
        sb.Append("<tr><td>GC heap</td><td class=\"num\">")
          .Append(FormatDouble(obj["gc_heap_mb"])).Append(" MB</td></tr>");
        sb.Append("<tr><td>GC collections (gen 0 / 1 / 2)</td><td class=\"num\">")
          .Append(FormatLong(obj["gen0_collections"])).Append(" / ")
          .Append(FormatLong(obj["gen1_collections"])).Append(" / ")
          .Append(FormatLong(obj["gen2_collections"])).Append("</td></tr>");
        sb.Append("</table>");
    }

    // ── formatters ───────────────────────────────────────────────────────────

    private static string Esc(string? s) => HtmlEncoder.Default.Encode(s ?? "");

    private static string FormatLong(JsonNode? n)
    {
        if (n == null) return "0";
        try { return n.GetValue<long>().ToString("N0", CultureInfo.InvariantCulture); }
        catch { return Esc(n.ToString()); }
    }

    private static string FormatDouble(JsonNode? n)
    {
        if (n == null) return "0";
        try { return n.GetValue<double>().ToString("N1", CultureInfo.InvariantCulture); }
        catch { return Esc(n.ToString()); }
    }

    private static string FormatUsd(JsonNode? n)
    {
        if (n == null) return "$0.000000";
        try { return "$" + n.GetValue<decimal>().ToString("N6", CultureInfo.InvariantCulture); }
        catch
        {
            try { return "$" + n.GetValue<double>().ToString("N6", CultureInfo.InvariantCulture); }
            catch { return Esc(n.ToString()); }
        }
    }

    private static string FormatBytes(JsonNode? n)
    {
        if (n == null) return "0 B";
        long bytes;
        try { bytes = n.GetValue<long>(); }
        catch { return Esc(n.ToString()); }

        const double KB = 1024d, MB = KB * 1024, GB = MB * 1024;
        return bytes switch
        {
            >= (long)GB => $"{bytes / GB:N2} GB",
            >= (long)MB => $"{bytes / MB:N1} MB",
            >= (long)KB => $"{bytes / KB:N1} KB",
            _ => $"{bytes:N0} B",
        };
    }

    private static string FormatTimestamp(JsonNode? n)
    {
        if (n == null) return "";
        var raw = n.ToString();
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return raw;
    }
}
