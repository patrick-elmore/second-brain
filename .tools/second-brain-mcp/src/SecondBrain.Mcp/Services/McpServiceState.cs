using SecondBrain.Mcp.Stats;

namespace SecondBrain.Mcp.Services;

public sealed class McpServiceState
{
    public IMcpRequestHandler? Handler { get; set; }
    public StatsTracker? StatsTracker { get; set; }
}
