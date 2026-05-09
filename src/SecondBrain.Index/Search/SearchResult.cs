namespace SecondBrain.Index.Search;

public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    IReadOnlyList<SourceSummary>? SourcesSummary);
