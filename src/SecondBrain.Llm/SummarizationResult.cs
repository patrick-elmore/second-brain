namespace SecondBrain.Llm;

public enum SummarizationOutcome
{
    /// <summary>API returned a parseable summary for this doc.</summary>
    Summarized,

    /// <summary>Permanent skip — content too short, unreadable, or no summary parsed. Caller should retire the row.</summary>
    Skipped,

    /// <summary>Transient failure — API error or timeout. Caller should leave the row for a later retry.</summary>
    Failed,
}

public sealed record SummarizationResult(
    long Id,
    SummarizationOutcome Outcome,
    string? Summary,
    string? Reason,
    string? SourceType = null)
{
    public static SummarizationResult Ok(long id, string summary, string? sourceType = null) =>
        new(id, SummarizationOutcome.Summarized, summary, null, sourceType);

    public static SummarizationResult Skip(long id, string reason) =>
        new(id, SummarizationOutcome.Skipped, null, reason);

    public static SummarizationResult Fail(long id, string reason) =>
        new(id, SummarizationOutcome.Failed, null, reason);
}
