using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SecondBrain.Mcp.Stats;

namespace SecondBrain.Mcp.Tests;

public sealed class StatsTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statsFile;

    public StatsTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _statsFile = Path.Combine(_tempDir, "stats.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private StatsTracker MakeTracker(string? filePath = null) =>
        new(new PricingTable([]), filePath ?? _statsFile, NullLogger<StatsTracker>.Instance);

    private static T GetProp<T>(object obj, string path)
    {
        var json = JsonSerializer.SerializeToNode(obj)!;
        foreach (var seg in path.Split('.'))
            json = json[seg]!;
        return json.GetValue<T>();
    }

    private static JsonElement GetElement(object obj) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj));

    [Fact]
    public void NewTracker_GetStats_HasZeroToolCalls()
    {
        var tracker = MakeTracker();
        var stats = GetElement(tracker.GetStats());

        stats.GetProperty("tool_calls").GetProperty("last_24h").GetInt64().Should().Be(0);
        stats.GetProperty("tool_calls").GetProperty("current_hour").GetInt64().Should().Be(0);
    }

    [Fact]
    public void RecordMcpToolCall_IncrementsByToolNameAndHourly()
    {
        var tracker = MakeTracker();

        tracker.RecordMcpToolCall("search");
        tracker.RecordMcpToolCall("search");
        tracker.RecordMcpToolCall("ask");

        var stats = GetElement(tracker.GetStats());
        var byTool = stats.GetProperty("tool_calls").GetProperty("by_tool");
        byTool.GetProperty("search").GetInt64().Should().Be(2);
        byTool.GetProperty("ask").GetInt64().Should().Be(1);
        stats.GetProperty("tool_calls").GetProperty("last_24h").GetInt64().Should().Be(3);
    }

    [Fact]
    public void RecordLlmCall_AccumulatesPerModel()
    {
        var table = new PricingTable(new Dictionary<string, ModelPricing>
        {
            ["haiku"] = new ModelPricing
            {
                Standard = new TierPricing { Input = 1m, Output = 5m },
            },
        });
        var tracker = new StatsTracker(table, _statsFile, NullLogger<StatsTracker>.Instance);

        tracker.RecordLlmCall("haiku", 1_000_000, 1_000_000, 0, 0);
        tracker.RecordLlmCall("haiku", 500_000, 0, 0, 0);

        var stats = GetElement(tracker.GetStats());
        var haiku = stats.GetProperty("llm").GetProperty("by_model").GetProperty("haiku");
        haiku.GetProperty("requests").GetInt64().Should().Be(2);
        haiku.GetProperty("input_tokens").GetInt64().Should().Be(1_500_000);
        haiku.GetProperty("output_tokens").GetInt64().Should().Be(1_000_000);
        // cost: (1M * $1 + 1M * $5) + (0.5M * $1) = $6.5
        haiku.GetProperty("estimated_cost_usd").GetDecimal().Should().BeApproximately(6.5m, 0.001m);
    }

    [Fact]
    public void RecordLlmCall_ReturnsCost()
    {
        var table = new PricingTable(new Dictionary<string, ModelPricing>
        {
            ["m"] = new ModelPricing { Standard = new TierPricing { Input = 2m, Output = 0m } },
        });
        var tracker = new StatsTracker(table, _statsFile, NullLogger<StatsTracker>.Instance);

        var cost = tracker.RecordLlmCall("m", 1_000_000, 0, 0, 0);

        cost.Should().Be(2m);
    }

    [Fact]
    public void RecordToolDispatch_StoresUnderInternalPrefix()
    {
        var tracker = MakeTracker();

        tracker.RecordToolDispatch("search");
        tracker.RecordToolDispatch("read_file");
        tracker.RecordToolDispatch("search");

        var stats = GetElement(tracker.GetStats());
        var byTool = stats.GetProperty("tool_calls").GetProperty("by_tool");
        byTool.GetProperty("internal:search").GetInt64().Should().Be(2);
        byTool.GetProperty("internal:read_file").GetInt64().Should().Be(1);
    }

    [Fact]
    public void RecordFileRead_IncrementsCountAndDistinctSet()
    {
        var tracker = MakeTracker();

        tracker.RecordFileRead("/a/file.md");
        tracker.RecordFileRead("/b/other.md");
        tracker.RecordFileRead("/a/file.md"); // duplicate

        var stats = GetElement(tracker.GetStats());
        stats.GetProperty("files").GetProperty("total_reads").GetInt64().Should().Be(3);
        stats.GetProperty("files").GetProperty("distinct_files").GetInt32().Should().Be(2);
    }

    [Fact]
    public void RecordFileRead_CaseInsensitiveDedup()
    {
        var tracker = MakeTracker();

        tracker.RecordFileRead("/A/File.md");
        tracker.RecordFileRead("/a/file.md");

        var stats = GetElement(tracker.GetStats());
        stats.GetProperty("files").GetProperty("distinct_files").GetInt32().Should().Be(1);
    }

    [Fact]
    public void SetAnomalousRefresh_ShowsInStats()
    {
        var tracker = MakeTracker();

        tracker.SetAnomalousRefresh(250);

        var stats = GetElement(tracker.GetStats());
        stats.GetProperty("anomalous_pending_count").GetInt64().Should().Be(250);
    }

    [Fact]
    public void ClearAnomalousRefresh_ResetsToZero()
    {
        var tracker = MakeTracker();
        tracker.SetAnomalousRefresh(250);

        tracker.ClearAnomalousRefresh();

        var stats = GetElement(tracker.GetStats());
        stats.GetProperty("anomalous_pending_count").GetInt64().Should().Be(0);
    }

    [Fact]
    public void RecordIndexRefresh_PopulatesLastRefreshAndTotal()
    {
        var tracker = MakeTracker();

        tracker.RecordIndexRefresh(10, 5, 2, 100, 0, TimeSpan.FromSeconds(1.5));
        tracker.RecordIndexRefresh(0, 0, 0, 50, 0, TimeSpan.FromSeconds(0.3));

        var stats = GetElement(tracker.GetStats());
        var refresh = stats.GetProperty("index").GetProperty("refresh");
        refresh.GetProperty("total").GetInt64().Should().Be(2);
        // last_at should be set
        refresh.GetProperty("last_at").ValueKind.Should().NotBe(JsonValueKind.Null);
        // last summary should reflect most-recent call (RefreshSummary record → PascalCase)
        var last = refresh.GetProperty("last");
        last.GetProperty("Added").GetInt32().Should().Be(0);
        last.GetProperty("Unchanged").GetInt32().Should().Be(50);
    }

    [Fact]
    public void PersistToDisk_ThenLoad_RestoresState()
    {
        var tracker = MakeTracker();
        tracker.RecordMcpToolCall("search");
        tracker.RecordMcpToolCall("ask");
        tracker.RecordFileRead("/some/file.md");

        tracker.PersistToDisk();

        var restored = MakeTracker(); // loads from same file path
        var stats = GetElement(restored.GetStats());
        var byTool = stats.GetProperty("tool_calls").GetProperty("by_tool");
        byTool.GetProperty("search").GetInt64().Should().Be(1);
        byTool.GetProperty("ask").GetInt64().Should().Be(1);
        stats.GetProperty("files").GetProperty("distinct_files").GetInt32().Should().Be(1);
    }

    [Fact]
    public void PersistToDisk_CorruptFile_StartsFreshOnLoad()
    {
        File.WriteAllText(_statsFile, "not valid json {{{{");

        // Should not throw; starts fresh
        var tracker = MakeTracker();
        var stats = GetElement(tracker.GetStats());
        stats.GetProperty("tool_calls").GetProperty("last_24h").GetInt64().Should().Be(0);
    }

    [Fact]
    public void Persist_CreatesParentDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempDir, "sub", "stats.json");
        var tracker = new StatsTracker(new PricingTable([]), nested, NullLogger<StatsTracker>.Instance);

        tracker.PersistToDisk();

        File.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void GetStats_MemorySection_IsPresent()
    {
        var tracker = MakeTracker();
        var stats = GetElement(tracker.GetStats());

        stats.GetProperty("memory").GetProperty("working_set_mb").GetDouble().Should().BeGreaterThan(0);
        stats.GetProperty("memory").TryGetProperty("gen0_collections", out _).Should().BeTrue();
    }

    [Fact]
    public void GetStats_UptimeAndStatsSince_ArePresent()
    {
        var tracker = MakeTracker();
        var stats = GetElement(tracker.GetStats());

        stats.GetProperty("uptime").GetString().Should().NotBeNullOrEmpty();
        stats.GetProperty("stats_since").ValueKind.Should().NotBe(JsonValueKind.Null);
    }
}
