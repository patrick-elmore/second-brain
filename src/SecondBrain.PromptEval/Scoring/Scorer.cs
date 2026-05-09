namespace SecondBrain.PromptEval.Scoring;

/// <summary>
/// File-overlap scoring with F2 (recall weighted 4x over precision).
/// Missing an expected doc hurts the synthesis far more than an extra doc.
/// </summary>
public static class Scorer
{
    /// <summary>F-beta with beta=2 (recall weighted 4x).</summary>
    public const double Beta = 2.0;

    public static CaseScore Score(
        IReadOnlyList<string> expectedPaths,
        IReadOnlyList<string> actualPaths)
    {
        // Case-insensitive comparison (matches FilesReferenced HashSet behavior in production).
        var expected = new HashSet<string>(expectedPaths, StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(actualPaths, StringComparer.OrdinalIgnoreCase);

        var hit = expected.Intersect(actual, StringComparer.OrdinalIgnoreCase).Count();

        var precision = actual.Count == 0 ? 0.0 : (double)hit / actual.Count;
        var recall = expected.Count == 0 ? 0.0 : (double)hit / expected.Count;

        var f2 = ComputeFBeta(precision, recall, Beta);
        return new CaseScore(precision, recall, f2, hit, expected.Count, actual.Count);
    }

    public static double ComputeFBeta(double precision, double recall, double beta)
    {
        if (precision == 0.0 && recall == 0.0) return 0.0;
        var b2 = beta * beta;
        return (1 + b2) * precision * recall / (b2 * precision + recall);
    }
}

public sealed record CaseScore(
    double Precision,
    double Recall,
    double F2,
    int Hit,
    int Expected,
    int Actual);
