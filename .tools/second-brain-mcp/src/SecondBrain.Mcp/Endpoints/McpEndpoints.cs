using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using SecondBrain.Mcp.Configuration;
using SecondBrain.Mcp.Services;
using SecondBrain.Mcp.Stats;

namespace SecondBrain.Mcp.Endpoints;

public static class McpEndpoints
{
    public static void MapMcpEndpoints(this WebApplication app)
    {
        app.MapPost("/mcp", async (
            HttpContext context,
            [FromBody] JsonNode request,
            [FromServices] McpServiceState state,
            ILogger<McpServiceState> logger) =>
        {
            if (state.Handler == null || !state.Handler.IsHealthy)
                return Results.Json(new { error = "Service not ready" }, statusCode: 503);

            try
            {
                var response = await state.Handler.HandleRequestAsync(request, context.RequestAborted);
                return Results.Json(response);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling MCP request");
                return Results.Json(
                    new { jsonrpc = "2.0", error = new { code = -32603, message = ex.Message } },
                    statusCode: 500);
            }
        });

        app.MapGet("/health", ([FromServices] McpServiceState state,
            [FromServices] Microsoft.Extensions.Options.IOptions<McpSettings> settings) =>
        {
            var healthy = state.Handler?.IsHealthy ?? false;
            return Results.Json(new
            {
                status = healthy ? "healthy" : "unhealthy",
                service = settings.Value.ServiceName,
                version = "1.0.0",
            }, statusCode: healthy ? 200 : 503);
        });

        app.MapGet("/.well-known/mcp", ([FromServices] Microsoft.Extensions.Options.IOptions<McpSettings> settings) =>
        {
            var s = settings.Value;
            return Results.Json(new
            {
                protocol = "MCP",
                version = "2024-11-05",
                transport = "HTTP",
                endpoint = "/mcp",
                server = new { name = s.ServiceName, version = "1.0.0" },
            });
        });

        app.MapGet("/stats", ([FromServices] McpServiceState state) =>
        {
            if (state.StatsTracker == null)
                return Results.Content("<h1>Stats not initialized</h1>", "text/html", statusCode: 503);
            var html = StatsHtmlRenderer.Render(state.StatsTracker.GetStats());
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/stats.json", ([FromServices] McpServiceState state) =>
        {
            if (state.StatsTracker == null)
                return Results.Json(new { error = "Stats not initialized" }, statusCode: 503);
            return Results.Json(state.StatsTracker.GetStats());
        });

        app.MapPost("/summarize/override", ([FromServices] McpServiceState state) =>
        {
            state.StatsTracker?.ClearAnomalousRefresh();
            state.Handler?.TryStartSummarization();
            return Results.Redirect("/stats");
        });
    }
}
