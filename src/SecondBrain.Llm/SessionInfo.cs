namespace SecondBrain.Llm;

public sealed record SessionInfo(
    int Messages,
    long ApproximateTokens,
    string CurrentDefaultModel,
    DateTime? LastCompacted,
    DateTime? LastActivity,
    DateTime? StatePersistedAt);
