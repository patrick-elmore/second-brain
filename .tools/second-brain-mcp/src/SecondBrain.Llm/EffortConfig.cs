using Anthropic.Models.Messages;
using ApiEffort = Anthropic.Models.Messages.Effort;

namespace SecondBrain.Llm;

/// <summary>
/// Maps effort tier (low/medium/high) to the API parameters that actually
/// vary behavior on Vertex AI: <c>Thinking</c> budget and <c>MaxTokens</c>.
///
/// The earlier mechanism used <c>OutputConfig.Effort</c>, but Vertex rejects
/// <c>output_config</c>, so the entire system was running at model defaults
/// regardless of the configured tier. The <c>thinking</c> field is part of
/// the standard Anthropic API and works on both Vertex and direct API.
///
/// Mapping:
/// <list type="bullet">
///   <item><description>low: no extended thinking; baseOutputTokens unchanged</description></item>
///   <item><description>medium: 4_096 thinking budget; MaxTokens = base + 4_096</description></item>
///   <item><description>high: 16_384 thinking budget; MaxTokens = base + 16_384</description></item>
/// </list>
///
/// Anthropic requires <c>max_tokens &gt;= budget_tokens + output_reserve</c>,
/// so the base output budget is preserved at every tier.
/// </summary>
public static class EffortConfig
{
    public const int MediumBudgetTokens = 4_096;
    public const int HighBudgetTokens = 16_384;

    public static (ThinkingConfigParam? thinking, int maxTokens) Resolve(
        ApiEffort effort,
        int baseOutputTokens = 8_192) => effort switch
        {
            ApiEffort.Medium => (
                new ThinkingConfigParam(new ThinkingConfigEnabled { BudgetTokens = MediumBudgetTokens }),
                baseOutputTokens + MediumBudgetTokens),
            ApiEffort.High => (
                new ThinkingConfigParam(new ThinkingConfigEnabled { BudgetTokens = HighBudgetTokens }),
                baseOutputTokens + HighBudgetTokens),
            _ => (null, baseOutputTokens),
        };

    public static (ThinkingConfigParam? thinking, int maxTokens) Resolve(
        string effort,
        int baseOutputTokens = 8_192) => Resolve(ResolveStringEffort(effort), baseOutputTokens);

    private static ApiEffort ResolveStringEffort(string effort) => effort?.ToLowerInvariant() switch
    {
        "medium" => ApiEffort.Medium,
        "high" => ApiEffort.High,
        _ => ApiEffort.Low,
    };
}
