namespace SecondBrain.Llm;

/// <summary>
/// Receives runtime metrics from the LLM layer. The Mcp layer supplies the
/// concrete implementation; the Llm layer never deals with persistence or HTTP.
/// </summary>
public interface IStatsRecorder
{
    /// <summary>
    /// Records token usage for a single LLM API call and returns the estimated
    /// USD cost the recorder computed from its pricing table. Callers can sum
    /// the returned values to attribute total cost to a higher-level operation
    /// (e.g. a single <c>ask</c> may make multiple API calls).
    /// </summary>
    decimal RecordLlmCall(
        string model,
        long inputTokens,
        long outputTokens,
        long cacheCreationTokens,
        long cacheReadTokens);

    void RecordToolDispatch(string toolName);

    void RecordFileRead(string absolutePath);
}
