namespace SecondBrain.Index.RequestHistory;

public sealed record RequestEntity(
    string RequestId,
    string? Timestamp,
    string? Tool,
    string? Query,
    string? FiltersJson,
    int? ResultCount,
    string? Synthesis,
    IReadOnlyList<RequestFileEntity>? Files);
