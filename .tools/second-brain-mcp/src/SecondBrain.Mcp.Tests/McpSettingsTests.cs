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
    public void SecondBrainSettings_DefaultIndexMaxBytes_Is5M()
    {
        var settings = new SecondBrainSettings();

        settings.IndexMaxBytes.Should().Be(5_000_000);
    }
}
