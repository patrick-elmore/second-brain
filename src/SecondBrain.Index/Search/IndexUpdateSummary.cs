namespace SecondBrain.Index.Search;

public sealed record IndexUpdateSummary(
    int Added,
    int Modified,
    int Removed,
    int Unchanged,
    int Skipped,
    bool FullRebuild,
    TimeSpan Elapsed,
    string DbPath);
