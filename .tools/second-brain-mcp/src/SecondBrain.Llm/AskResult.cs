namespace SecondBrain.Llm;

public sealed record AskResult(
    string RequestId,
    string Synthesis,
    string ModelUsed,
    int ToolsCalled,
    IReadOnlyList<string> FilesReferenced,
    decimal EstimatedCostUsd);
