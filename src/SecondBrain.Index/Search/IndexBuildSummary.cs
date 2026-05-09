namespace SecondBrain.Index.Search;

public sealed record IndexBuildSummary(
    int IndexedCount,
    int SkippedCount,
    TimeSpan Elapsed,
    string DbPath);
