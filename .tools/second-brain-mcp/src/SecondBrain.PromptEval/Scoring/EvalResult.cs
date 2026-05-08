using SecondBrain.PromptEval.TestCases;

namespace SecondBrain.PromptEval.Scoring;

/// <summary>One test case's outcome under a single variant.</summary>
public sealed record CaseResult
{
    public required string TestCaseId { get; init; }
    public required string Query { get; init; }
    public required IReadOnlyList<string> ExpectedPaths { get; init; }
    public required IReadOnlyList<string> ActualPaths { get; init; }
    public required string Synthesis { get; init; }
    public required CaseScore Score { get; init; }
    public required int ToolsCalled { get; init; }
    public required decimal CostUsd { get; init; }
    public required string DurationMs { get; init; }
}

/// <summary>Aggregate scoring across an entire test set under one variant.</summary>
public sealed record VariantEvalResult
{
    public required string VariantId { get; init; }
    public required string TestSetId { get; init; }
    public required IReadOnlyList<CaseResult> Cases { get; init; }
    public required AggregateScore Aggregate { get; init; }
    public required string EvaluatedAt { get; init; }
}

public sealed record AggregateScore
{
    public required double MeanF2 { get; init; }
    public required double MinF2 { get; init; }
    public required double MeanPrecision { get; init; }
    public required double MeanRecall { get; init; }
    public required double AcceptableRate { get; init; } // % of cases with F2 >= 0.5
    public required IReadOnlyDictionary<string, double> MeanF2BySourceType { get; init; }
    public required decimal TotalCostUsd { get; init; }

    public static AggregateScore FromCases(IReadOnlyList<CaseResult> cases, IReadOnlyList<TestCase> testCases)
    {
        if (cases.Count == 0)
            return new AggregateScore
            {
                MeanF2 = 0,
                MinF2 = 0,
                MeanPrecision = 0,
                MeanRecall = 0,
                AcceptableRate = 0,
                MeanF2BySourceType = new Dictionary<string, double>(),
                TotalCostUsd = 0m,
            };

        var byId = testCases.ToDictionary(t => t.Id, t => t.SourceType);

        var bySourceType = cases
            .GroupBy(c => byId.GetValueOrDefault(c.TestCaseId, "unknown"))
            .ToDictionary(g => g.Key, g => g.Average(c => c.Score.F2));

        return new AggregateScore
        {
            MeanF2 = cases.Average(c => c.Score.F2),
            MinF2 = cases.Min(c => c.Score.F2),
            MeanPrecision = cases.Average(c => c.Score.Precision),
            MeanRecall = cases.Average(c => c.Score.Recall),
            AcceptableRate = cases.Count(c => c.Score.F2 >= 0.5) / (double)cases.Count,
            MeanF2BySourceType = bySourceType,
            TotalCostUsd = cases.Sum(c => c.CostUsd),
        };
    }
}
