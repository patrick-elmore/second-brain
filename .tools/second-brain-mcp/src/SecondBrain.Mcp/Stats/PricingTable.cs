using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecondBrain.Mcp.Stats;

/// <summary>
/// Loads pricing.json and computes per-call estimated cost.
/// Tier picked per request based on input token count (≤200K vs >200K).
/// </summary>
public sealed class PricingTable
{
    private readonly Dictionary<string, ModelPricing> _models;

    public PricingTable(Dictionary<string, ModelPricing> models)
    {
        _models = new Dictionary<string, ModelPricing>(models, StringComparer.OrdinalIgnoreCase);
    }

    public static PricingTable Load(string pricingPath)
    {
        if (!File.Exists(pricingPath))
            return new PricingTable([]);

        using var stream = File.OpenRead(pricingPath);
        var doc = JsonSerializer.Deserialize<PricingFile>(stream, JsonOpts);
        return new PricingTable(doc?.Models ?? []);
    }

    /// <summary>
    /// Returns USD cost for a single API call. Returns 0 if the model isn't in the table.
    /// </summary>
    public decimal CalculateCost(
        string modelId,
        long inputTokens,
        long outputTokens,
        long cacheCreationTokens,
        long cacheReadTokens)
    {
        if (!TryResolveModel(modelId, out var pricing))
            return 0m;

        // Pick tier based on input token count
        var tier = (inputTokens > 200_000 && pricing.LargeContext != null)
            ? pricing.LargeContext
            : pricing.Standard;

        if (tier == null)
            return 0m;

        var cost = 0m;
        cost += (decimal)inputTokens / 1_000_000m * tier.Input;
        cost += (decimal)outputTokens / 1_000_000m * tier.Output;
        cost += (decimal)cacheCreationTokens / 1_000_000m * (tier.CacheWrite5m ?? tier.Input);
        cost += (decimal)cacheReadTokens / 1_000_000m * (tier.CacheHit ?? tier.Input);
        return cost;
    }

    private bool TryResolveModel(string modelId, out ModelPricing pricing)
    {
        // Direct match first (case-insensitive via dictionary comparer)
        if (_models.TryGetValue(modelId, out pricing!))
            return true;

        // Strip date suffix: claude-haiku-4-5-20251001 -> claude-haiku-4-5
        // Strip @ suffix: claude-haiku-4-5@20251001 -> claude-haiku-4-5
        var normalized = NormalizeModelId(modelId);
        if (_models.TryGetValue(normalized, out pricing!))
            return true;

        pricing = null!;
        return false;
    }

    private static string NormalizeModelId(string modelId)
    {
        // Strip @<anything>
        var atIdx = modelId.IndexOf('@');
        if (atIdx > 0) modelId = modelId[..atIdx];

        // Strip trailing -<8-or-more-digit-date>
        var lastDash = modelId.LastIndexOf('-');
        if (lastDash > 0)
        {
            var suffix = modelId[(lastDash + 1)..];
            if (suffix.Length >= 8 && suffix.All(char.IsDigit))
                modelId = modelId[..lastDash];
        }

        return modelId;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class PricingFile
    {
        [JsonPropertyName("models")]
        public Dictionary<string, ModelPricing> Models { get; set; } = [];
    }
}

public sealed class ModelPricing
{
    [JsonPropertyName("standard")]
    public TierPricing? Standard { get; set; }

    [JsonPropertyName("large_context")]
    public TierPricing? LargeContext { get; set; }
}

public sealed class TierPricing
{
    [JsonPropertyName("input")]
    public decimal Input { get; set; }

    [JsonPropertyName("output")]
    public decimal Output { get; set; }

    [JsonPropertyName("cache_write_5m")]
    public decimal? CacheWrite5m { get; set; }

    [JsonPropertyName("cache_write_1h")]
    public decimal? CacheWrite1h { get; set; }

    [JsonPropertyName("cache_hit")]
    public decimal? CacheHit { get; set; }
}
