namespace SecondBrain.Llm;

/// <summary>
/// Receives runtime metrics from the LLM layer. The Mcp layer supplies the
/// concrete implementation; the Llm layer never deals with persistence or HTTP.
/// </summary>
public interface IStatsRecorder
{
    void RecordLlmCall(
        string model,
        long inputTokens,
        long outputTokens,
        long cacheCreationTokens,
        long cacheReadTokens);

    void RecordToolDispatch(string toolName);

    void RecordFileRead(string absolutePath);
}
