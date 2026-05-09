using SecondBrain.Mcp.Stats;

namespace SecondBrain.Mcp.Tests;

public sealed class PricingTableTests
{
    private static PricingTable Build(params (string id, decimal input, decimal output, decimal? cacheWrite, decimal? cacheHit)[] models)
    {
        var dict = new Dictionary<string, ModelPricing>();
        foreach (var (id, input, output, cacheWrite, cacheHit) in models)
        {
            dict[id] = new ModelPricing
            {
                Standard = new TierPricing
                {
                    Input = input,
                    Output = output,
                    CacheWrite5m = cacheWrite,
                    CacheHit = cacheHit,
                },
            };
        }
        return new PricingTable(dict);
    }

    private static PricingTable BuildWithLargeContext(string id,
        decimal stdInput, decimal stdOutput,
        decimal lcInput, decimal lcOutput)
    {
        return new PricingTable(new Dictionary<string, ModelPricing>
        {
            [id] = new ModelPricing
            {
                Standard = new TierPricing { Input = stdInput, Output = stdOutput },
                LargeContext = new TierPricing { Input = lcInput, Output = lcOutput },
            },
        });
    }

    [Fact]
    public void UnknownModel_ReturnZero()
    {
        var table = Build(("claude-known", 3m, 15m, null, null));

        var cost = table.CalculateCost("claude-unknown", 1_000_000, 1_000_000, 0, 0);

        cost.Should().Be(0m);
    }

    [Fact]
    public void KnownModel_StandardTier_ComputesCorrectly()
    {
        // $3/M input, $15/M output
        var table = Build(("claude-haiku", 3m, 15m, null, null));

        var cost = table.CalculateCost("claude-haiku", 1_000_000, 1_000_000, 0, 0);

        cost.Should().Be(18m); // $3 + $15
    }

    [Fact]
    public void InputAbove200K_LargeContextTierUsed()
    {
        var table = BuildWithLargeContext("claude-model", 3m, 15m, 6m, 30m);

        // 200,001 tokens → large context tier
        var cost = table.CalculateCost("claude-model", 200_001, 0, 0, 0);

        // 200001 / 1M * $6 input (large context)
        var expected = 200_001m / 1_000_000m * 6m;
        cost.Should().BeApproximately(expected, 0.000001m);
    }

    [Fact]
    public void InputExactly200K_StandardTierUsed()
    {
        var table = BuildWithLargeContext("claude-model", 3m, 15m, 6m, 30m);

        var cost = table.CalculateCost("claude-model", 200_000, 0, 0, 0);

        var expected = 200_000m / 1_000_000m * 3m;
        cost.Should().BeApproximately(expected, 0.000001m);
    }

    [Fact]
    public void ModelHasNoLargeContext_HighTokenCountUsesStandard()
    {
        var table = Build(("claude-model", 3m, 15m, null, null));

        var cost = table.CalculateCost("claude-model", 300_000, 0, 0, 0);

        var expected = 300_000m / 1_000_000m * 3m;
        cost.Should().BeApproximately(expected, 0.000001m);
    }

    [Fact]
    public void CacheWriteTokens_UseCacheWrite5mRate()
    {
        var table = Build(("m", 3m, 15m, 3.75m, 0.30m));

        var cost = table.CalculateCost("m", 0, 0, 1_000_000, 0);

        cost.Should().Be(3.75m);
    }

    [Fact]
    public void CacheWriteTokens_FallsBackToInputRateWhenNull()
    {
        var table = Build(("m", 3m, 15m, null, null));

        var cost = table.CalculateCost("m", 0, 0, 1_000_000, 0);

        cost.Should().Be(3m); // falls back to input price
    }

    [Fact]
    public void CacheReadTokens_UseCacheHitRate()
    {
        var table = Build(("m", 3m, 15m, 3.75m, 0.30m));

        var cost = table.CalculateCost("m", 0, 0, 0, 1_000_000);

        cost.Should().Be(0.30m);
    }

    [Fact]
    public void CacheReadTokens_FallsBackToInputRateWhenNull()
    {
        var table = Build(("m", 3m, 15m, null, null));

        var cost = table.CalculateCost("m", 0, 0, 0, 1_000_000);

        cost.Should().Be(3m);
    }

    [Fact]
    public void ModelIdWithAtSuffix_ResolvesToBaseName()
    {
        var table = Build(("claude-haiku-4-5", 3m, 15m, null, null));

        var cost = table.CalculateCost("claude-haiku-4-5@20251001", 1_000_000, 0, 0, 0);

        cost.Should().Be(3m);
    }

    [Fact]
    public void ModelIdWithDateSuffix_ResolvesToBaseName()
    {
        var table = Build(("claude-haiku-4-5", 3m, 15m, null, null));

        var cost = table.CalculateCost("claude-haiku-4-5-20251001", 1_000_000, 0, 0, 0);

        cost.Should().Be(3m);
    }

    [Fact]
    public void ModelIdWithShortSuffix_DoesNotStrip()
    {
        var table = Build(("claude-haiku-4-5", 3m, 15m, null, null));

        // "abc" is < 8 digits — should not be stripped → unknown model → $0
        var cost = table.CalculateCost("claude-haiku-4-5-abc", 1_000_000, 0, 0, 0);

        cost.Should().Be(0m);
    }

    [Fact]
    public void ModelLookupIsCaseInsensitive()
    {
        var table = Build(("Claude-Haiku", 3m, 15m, null, null));

        var cost = table.CalculateCost("claude-haiku", 1_000_000, 0, 0, 0);

        cost.Should().Be(3m);
    }

    [Fact]
    public void Load_MissingFile_ReturnsTableWithZeroCost()
    {
        var table = PricingTable.Load("/nonexistent/pricing.json");

        var cost = table.CalculateCost("any-model", 1_000_000, 1_000_000, 0, 0);

        cost.Should().Be(0m);
    }

    [Fact]
    public void Load_ValidJson_ParsesModels()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            File.WriteAllText(tempFile, """
                {
                  "models": {
                    "claude-test": {
                      "standard": { "input": 5.0, "output": 25.0 }
                    }
                  }
                }
                """);

            var table = PricingTable.Load(tempFile);
            var cost = table.CalculateCost("claude-test", 1_000_000, 0, 0, 0);

            cost.Should().Be(5m);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void AllTokenTypes_CombinedCostMatchesExpected()
    {
        long inputTokens = 1_000, outputTokens = 500, cacheWrite = 100, cacheRead = 200;
        decimal inputRate = 3m, outputRate = 15m, cacheWriteRate = 3.75m, cacheReadRate = 0.30m;

        var table = Build(("m", inputRate, outputRate, cacheWriteRate, cacheReadRate));

        var cost = table.CalculateCost("m", inputTokens, outputTokens, cacheWrite, cacheRead);

        var expected =
            inputTokens / 1_000_000m * inputRate +
            outputTokens / 1_000_000m * outputRate +
            cacheWrite / 1_000_000m * cacheWriteRate +
            cacheRead / 1_000_000m * cacheReadRate;

        cost.Should().BeApproximately(expected, 0.0000001m);
    }
}
