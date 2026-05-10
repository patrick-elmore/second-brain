using SecondBrain.Mcp.Configuration;

namespace SecondBrain.Mcp.Tests;

public sealed class McpSettingsTests : IDisposable
{
    private readonly string _tempDir;

    public McpSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = McpSettings.Load(Path.Combine(_tempDir, "nonexistent.json"));

        settings.HttpPort.Should().Be(9998);
        settings.LogLevel.Should().Be("INFO");
        settings.SecondBrain.DefaultModel.Should().Be("claude-haiku-4-5");
        settings.SecondBrain.FtsDbPath.Should().Be("index/fts.db");
    }

    [Fact]
    public void Load_ValidJson_OverridesDefaults()
    {
        var path = WriteConfig("""
            {
              "http_port": 9999,
              "log_level": "DEBUG",
              "second_brain": {
                "default_model": "claude-sonnet-4-6",
                "fts_db_path": "custom/fts.db"
              }
            }
            """);

        var settings = McpSettings.Load(path);

        settings.HttpPort.Should().Be(9999);
        settings.LogLevel.Should().Be("DEBUG");
        settings.SecondBrain.DefaultModel.Should().Be("claude-sonnet-4-6");
        settings.SecondBrain.FtsDbPath.Should().Be("custom/fts.db");
    }

    [Fact]
    public void Load_PartialJson_UnspecifiedPropertiesUseDefaults()
    {
        var path = WriteConfig("""{ "http_port": 8080 }""");

        var settings = McpSettings.Load(path);

        settings.HttpPort.Should().Be(8080);
        settings.LogLevel.Should().Be("INFO"); // default
        settings.SecondBrain.DefaultModel.Should().Be("claude-haiku-4-5"); // default
    }

    [Fact]
    public void ResolveApiKey_ReturnsEnvVarValue()
    {
        var envVarName = "SB_TEST_API_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVarName, "test-key-123");
        try
        {
            var settings = new SecondBrainSettings { AnthropicApiKeyEnv = envVarName };

            settings.ResolveApiKey().Should().Be("test-key-123");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [Fact]
    public void ResolveApiKey_UnsetEnvVar_ReturnsEmptyString()
    {
        var envVarName = "SB_TEST_API_KEY_UNSET_" + Guid.NewGuid().ToString("N");
        var settings = new SecondBrainSettings { AnthropicApiKeyEnv = envVarName };

        settings.ResolveApiKey().Should().Be(string.Empty);
    }

    [Fact]
    public void SecondBrainSettings_DefaultCompactThreshold_Is150K()
    {
        var settings = new SecondBrainSettings();

        settings.CompactThresholdTokens.Should().Be(150_000);
    }

    [Fact]
    public void SecondBrainSettings_DefaultIndexMaxBytes_Is500K()
    {
        var settings = new SecondBrainSettings();

        settings.IndexMaxBytes.Should().Be(500_000);
    }

    [Fact]
    public void SecondBrainSettings_DefaultMaxToolTurns_Is25()
    {
        var settings = new SecondBrainSettings();

        settings.MaxToolTurns.Should().Be(25);
    }

    [Fact]
    public void SecondBrainSettings_DefaultMaxReadFileBytes_Is128K()
    {
        var settings = new SecondBrainSettings();

        settings.MaxReadFileBytes.Should().Be(131_072);
    }

    [Fact]
    public void Load_ValidJson_OverridesGroupATunables()
    {
        var path = WriteConfig("""
            {
              "second_brain": {
                "max_tool_turns": 50,
                "max_read_file_bytes": 65536
              }
            }
            """);

        var settings = McpSettings.Load(path);

        settings.SecondBrain.MaxToolTurns.Should().Be(50);
        settings.SecondBrain.MaxReadFileBytes.Should().Be(65_536);
    }

    [Fact]
    public void SecondBrainSettings_DefaultBaseOutputTokens_Is8K()
    {
        var settings = new SecondBrainSettings();

        settings.BaseOutputTokens.Should().Be(8_192);
    }

    [Fact]
    public void SecondBrainSettings_DefaultCompactorMaxOutputTokens_Is8K()
    {
        var settings = new SecondBrainSettings();

        settings.CompactorMaxOutputTokens.Should().Be(8_192);
    }

    [Fact]
    public void SecondBrainSettings_DefaultSummarizerContentBudgetChars_Is80K()
    {
        var settings = new SecondBrainSettings();

        settings.SummarizerContentBudgetChars.Should().Be(80_000);
    }

    [Fact]
    public void SecondBrainSettings_DefaultSummarizerInputCharLimits_MatchesHistoricalSwitch()
    {
        var settings = new SecondBrainSettings();

        settings.SummarizerInputCharLimits.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["1on1"] = 24_000,
            ["transcript"] = 20_000,
            ["standup"] = 6_000,
            ["planning"] = 16_000,
            ["note"] = 8_000,
            ["default"] = 12_000,
        });
    }

    [Fact]
    public void Load_ValidJson_OverridesSummarizerLimits()
    {
        var path = WriteConfig("""
            {
              "second_brain": {
                "base_output_tokens": 4096,
                "compactor_max_output_tokens": 16384,
                "summarizer_content_budget_chars": 100000,
                "summarizer_input_char_limits": {
                  "transcript": 30000,
                  "default": 5000
                }
              }
            }
            """);

        var settings = McpSettings.Load(path);

        settings.SecondBrain.BaseOutputTokens.Should().Be(4096);
        settings.SecondBrain.CompactorMaxOutputTokens.Should().Be(16384);
        settings.SecondBrain.SummarizerContentBudgetChars.Should().Be(100_000);
        settings.SecondBrain.SummarizerInputCharLimits["transcript"].Should().Be(30_000);
        settings.SecondBrain.SummarizerInputCharLimits["default"].Should().Be(5_000);
    }

    [Fact]
    public void SecondBrainSettings_DefaultSearchMaxSnippetTokens_Is64()
    {
        var settings = new SecondBrainSettings();

        settings.SearchMaxSnippetTokens.Should().Be(64);
    }

    [Fact]
    public void SecondBrainSettings_DefaultSearchPerVariantOverfetchBounds_Are30And50()
    {
        var settings = new SecondBrainSettings();

        (settings.SearchPerVariantOverfetchMin, settings.SearchPerVariantOverfetchMax)
            .Should().Be((30, 50));
    }

    [Fact]
    public void SecondBrainSettings_DefaultIndexAnomalyChangeThreshold_Is200()
    {
        var settings = new SecondBrainSettings();

        settings.IndexAnomalyChangeThreshold.Should().Be(200);
    }

    [Fact]
    public void Load_ValidJson_OverridesSearchAndIndexAnomalyTunables()
    {
        var path = WriteConfig("""
            {
              "second_brain": {
                "search_max_snippet_tokens": 16,
                "search_per_variant_overfetch_min": 10,
                "search_per_variant_overfetch_max": 100,
                "index_anomaly_change_threshold": 500
              }
            }
            """);

        var settings = McpSettings.Load(path);

        settings.SecondBrain.SearchMaxSnippetTokens.Should().Be(16);
        settings.SecondBrain.SearchPerVariantOverfetchMin.Should().Be(10);
        settings.SecondBrain.SearchPerVariantOverfetchMax.Should().Be(100);
        settings.SecondBrain.IndexAnomalyChangeThreshold.Should().Be(500);
    }
}
