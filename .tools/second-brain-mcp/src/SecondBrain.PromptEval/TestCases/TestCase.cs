namespace SecondBrain.PromptEval.TestCases;

/// <summary>
/// One synthetic test case: a target document set, a plain-language query
/// that should retrieve those documents, and metadata about how it was generated.
/// </summary>
public sealed record TestCase
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> TargetPaths { get; init; }
    public required string Query { get; init; }
    public required string SourceType { get; init; }
    public string? Rationale { get; init; }
    public string? GeneratedAt { get; init; }
}

/// <summary>The full test suite, persisted as JSON.</summary>
public sealed record TestCaseSet
{
    public required string Id { get; init; }
    public required string GeneratedAt { get; init; }
    public required string IndexFingerprint { get; init; }
    public required IReadOnlyList<TestCase> Cases { get; init; }
}
