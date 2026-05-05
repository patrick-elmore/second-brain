using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using SecondBrain.Llm.Prompts;

namespace SecondBrain.Llm;

public sealed class Compactor
{
    private readonly IAnthropicClient _client;
    private readonly string _compactionModel;
    private readonly IStatsRecorder? _stats;

    public Compactor(IAnthropicClient client, string compactionModel = "claude-sonnet-4-6", IStatsRecorder? stats = null)
    {
        _client = client;
        _compactionModel = compactionModel;
        _stats = stats;
    }

    public async Task<CompactorOutput> CompactAsync(
        IReadOnlyList<MessageParam> messages,
        string? customInstruction,
        CancellationToken ct)
    {
        var instruction = BuildInstruction(customInstruction);

        // Build a one-shot call: compaction prompt as system, conversation as user
        var conversationText = SerializeConversation(messages);

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _compactionModel,
            MaxTokens = 8192,
            System = instruction,
            Messages =
            [
                new MessageParam
                {
                    Role = Role.User,
                    Content = $"Here is the conversation to compact:\n\n{conversationText}",
                },
            ],
        }, ct);

        var cost = _stats?.RecordLlmCall(
            _compactionModel,
            response.Usage.InputTokens,
            response.Usage.OutputTokens,
            response.Usage.CacheCreationInputTokens ?? 0,
            response.Usage.CacheReadInputTokens ?? 0) ?? 0m;

        return new CompactorOutput(ExtractText(response), cost);
    }

    public sealed record CompactorOutput(string Summary, decimal EstimatedCostUsd);

    private static string BuildInstruction(string? customInstruction)
    {
        if (string.IsNullOrWhiteSpace(customInstruction))
            return DefaultCompactionPrompt.Text;

        return DefaultCompactionPrompt.Text + "\n\nAdditional instruction:\n" + customInstruction;
    }

    private static string SerializeConversation(IReadOnlyList<MessageParam> messages)
    {
        var parts = new List<string>();
        foreach (var msg in messages)
        {
            var role = msg.Role.ToString().ToUpperInvariant();
            var content = msg.Content.Match(
                @string: s => s,
                contentBlockParams: blocks => string.Join("\n", blocks.Select(b =>
                    b.Json.GetRawText())));
            parts.Add($"[{role}]\n{content}");
        }
        return string.Join("\n\n---\n\n", parts);
    }

    private static string ExtractText(Message response)
    {
        var texts = new List<string>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock))
                texts.Add(textBlock.Text);
        }
        return string.Join("\n", texts);
    }
}
