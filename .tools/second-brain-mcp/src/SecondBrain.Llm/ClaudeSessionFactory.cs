using Anthropic;
using Anthropic.Core;
using Anthropic.Vertex;
using Microsoft.Extensions.Logging;
using SecondBrain.Files;
using SecondBrain.Index.Search;

namespace SecondBrain.Llm;

public static class ClaudeSessionFactory
{
    public static ClaudeSession Create(
        string apiKey,
        SearchEngine searchEngine,
        FileReader fileReader,
        string statePath,
        int stateBackupCount = 5,
        string defaultModel = "claude-haiku-4-5",
        string escalationModel = "claude-sonnet-4-6",
        long compactThresholdTokens = 150_000,
        int persistEveryNMessages = 5,
        ILogger? logger = null,
        IStatsRecorder? stats = null)
    {
        var client = BuildClient(apiKey);
        var compactor = new Compactor(client, escalationModel, stats);
        var statePersistence = new StatePersistence(statePath, stateBackupCount);

        return new ClaudeSession(
            client: client,
            searchEngine: searchEngine,
            fileReader: fileReader,
            compactor: compactor,
            statePersistence: statePersistence,
            defaultModel: defaultModel,
            escalationModel: escalationModel,
            compactThresholdTokens: compactThresholdTokens,
            persistEveryNMessages: persistEveryNMessages,
            logger: logger,
            stats: stats);
    }

    private static IAnthropicClient BuildClient(string apiKey)
    {
        var useVertex = string.Equals(
            Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX"),
            "1",
            StringComparison.Ordinal);

        if (useVertex)
        {
            var projectId = Environment.GetEnvironmentVariable("ANTHROPIC_VERTEX_PROJECT_ID")
                ?? throw new InvalidOperationException(
                    "CLAUDE_CODE_USE_VERTEX=1 but ANTHROPIC_VERTEX_PROJECT_ID is not set");

            var region = Environment.GetEnvironmentVariable("CLOUD_ML_REGION");

            // AnthropicVertexClient uses Google Application Default Credentials
            return new AnthropicVertexClient(new AnthropicVertexCredentials(region, projectId));
        }

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "No API key set. Either set ANTHROPIC_API_KEY or set CLAUDE_CODE_USE_VERTEX=1 with ANTHROPIC_VERTEX_PROJECT_ID.");

        return new AnthropicClient(new ClientOptions { ApiKey = apiKey });
    }
}
