using SecondBrain.PromptEval.Scoring;

namespace SecondBrain.PromptEval.Tuning;

public sealed record PromptVariant
{
    public required int Id { get; init; }
    public required string VariantId { get; init; } // hash-based, used as cache key
    public required string Surface { get; init; }
    public required string Value { get; init; }
    public int? ParentId { get; init; }
    public bool IsBaseline { get; init; }
    public string? Rationale { get; init; }
    public AggregateScore? Score { get; init; }
    public IReadOnlyList<CaseResult>? Cases { get; init; }
    public string? CreatedAt { get; init; }
}
