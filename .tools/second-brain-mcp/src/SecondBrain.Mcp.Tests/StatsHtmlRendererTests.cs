using System.Text.Json.Nodes;
using SecondBrain.Mcp.Stats;

namespace SecondBrain.Mcp.Tests;

public sealed class StatsHtmlRendererTests
{
    private static object BuildMinimalSnapshot(long anomalousPending = 0) => new
    {
        uptime = "0.00:01:23",
        stats_since = DateTimeOffset.UtcNow,
        anomalous_pending_count = anomalousPending,
        tool_calls = new { last_24h = 0L, current_hour = 0L, by_tool = new { }, hourly = Array.Empty<object>() },
        llm = new { total_requests = 0L, total_estimated_cost_usd = 0m, by_model = new { } },
        files = new { total_reads = 0L, distinct_files = 0 },
        index = new { exists = false, refresh = new { total = 0L, last_at = (DateTimeOffset?)null, last = (object?)null } },
        memory = new { working_set_mb = 100.0, gc_heap_mb = 50.0, gen0_collections = 10, gen1_collections = 2, gen2_collections = 1 },
    };

    [Fact]
    public void Render_ProducesWellFormedHtml()
    {
        var html = StatsHtmlRenderer.Render(BuildMinimalSnapshot());

        html.Should().StartWith("<!doctype html>");
        html.Should().EndWith("</html>");
        html.Should().Contain("<title>second-brain stats</title>");
    }

    [Fact]
    public void Render_WithAnomalousCount_ShowsAlert()
    {
        var html = StatsHtmlRenderer.Render(BuildMinimalSnapshot(anomalousPending: 250));

        html.Should().Contain("class=\"alert\"");
        html.Should().Contain("250");
    }

    [Fact]
    public void Render_WithZeroAnomalousCount_NoAlert()
    {
        var html = StatsHtmlRenderer.Render(BuildMinimalSnapshot(anomalousPending: 0));

        html.Should().NotContain("class=\"alert\"");
    }

    [Fact]
    public void Render_EscapesHtmlSpecialChars()
    {
        // Model name containing angle brackets should be escaped
        var snapshot = new
        {
            uptime = "0.00:00:01",
            stats_since = DateTimeOffset.UtcNow,
            anomalous_pending_count = 0L,
            tool_calls = new { last_24h = 0L, current_hour = 0L, by_tool = new { }, hourly = Array.Empty<object>() },
            llm = new
            {
                total_requests = 1L,
                total_estimated_cost_usd = 0m,
                by_model = new Dictionary<string, object>
                {
                    ["<script>alert('xss')</script>"] = new { requests = 1L, input_tokens = 0L, output_tokens = 0L, cache_creation_tokens = 0L, cache_read_tokens = 0L, estimated_cost_usd = 0m },
                },
            },
            files = new { total_reads = 0L, distinct_files = 0 },
            index = new { exists = false, refresh = new { total = 0L, last_at = (DateTimeOffset?)null, last = (object?)null } },
            memory = new { working_set_mb = 10.0, gc_heap_mb = 5.0, gen0_collections = 0, gen1_collections = 0, gen2_collections = 0 },
        };

        var html = StatsHtmlRenderer.Render(snapshot);

        html.Should().NotContain("<script>alert('xss')</script>");
    }

    [Fact]
    public void Render_EmptySnapshot_DoesNotThrow()
    {
        var act = () => StatsHtmlRenderer.Render(new { });

        act.Should().NotThrow();
    }

    [Fact]
    public void Render_ContainsMajorSections()
    {
        var html = StatsHtmlRenderer.Render(BuildMinimalSnapshot());

        html.Should().Contain("LLM");
        html.Should().Contain("Index");
        html.Should().Contain("Tool calls");
    }
}
