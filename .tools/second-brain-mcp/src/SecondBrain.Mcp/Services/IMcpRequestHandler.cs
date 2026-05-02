using System.Text.Json.Nodes;

namespace SecondBrain.Mcp.Services;

public interface IMcpRequestHandler
{
    Task<JsonNode> HandleRequestAsync(JsonNode request, CancellationToken ct = default);
    bool IsHealthy { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
