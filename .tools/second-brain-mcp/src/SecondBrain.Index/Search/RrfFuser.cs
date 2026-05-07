namespace SecondBrain.Index.Search;

/// <summary>
/// Reciprocal Rank Fusion (Cormack et al. 2009).
/// Merges multiple ranked result lists into one. Documents appearing high
/// in several lists outscore documents appearing in only one.
/// </summary>
internal static class RrfFuser
{
    private const double K = 60.0;

    /// <summary>
    /// Fuses ranked result lists and returns a single ranked list.
    /// Score for each document = Σ 1/(60 + rank_i) across lists it appears in
    /// (1-based rank). Higher score = more relevant. Documents missing from a
    /// variant contribute 0. Snippet is taken from the first list the document
    /// appears in — pass variants in confidence order to control snippet selection.
    /// </summary>
    public static IReadOnlyList<SearchHit> Fuse(
        IReadOnlyList<IReadOnlyList<SearchHit>> rankedLists,
        int top)
    {
        if (rankedLists.Count == 0) return [];

        var fusedScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new Dictionary<string, SearchHit>(StringComparer.OrdinalIgnoreCase);

        foreach (var list in rankedLists)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var hit = list[i];
                var key = hit.AbsolutePath;
                fusedScores[key] = fusedScores.GetValueOrDefault(key) + 1.0 / (K + i + 1);
                firstSeen.TryAdd(key, hit);
            }
        }

        return fusedScores
            .OrderByDescending(kvp => kvp.Value)
            .Take(top)
            .Select(kvp => firstSeen[kvp.Key] with { Score = kvp.Value })
            .ToList();
    }
}
