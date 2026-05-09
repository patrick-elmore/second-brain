namespace SecondBrain.Llm;

public sealed record CompactResult(
    int MessagesBefore,
    int MessagesAfter,
    long ApproximateTokensBefore,
    long ApproximateTokensAfter,
    decimal EstimatedCostUsd);
