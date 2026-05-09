namespace SecondBrain.Index.RequestHistory;

public sealed record RequestRecord(
    string Id,
    DateTime Timestamp,
    string Tool,
    string? Query,
    string FiltersJson,
    int ResultCount,
    string? Synthesis);
