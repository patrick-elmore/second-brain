using System.Text.Json;
using Anthropic.Models.Messages;
using SecondBrain.Llm;

namespace SecondBrain.Llm.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IMessageCreator"/>. Enqueues scripted
/// <see cref="Message"/> responses and records all calls made.
/// </summary>
internal sealed class FakeMessageCreator : IMessageCreator
{
    private readonly Queue<Message> _responses = new();
    private readonly List<MessageCreateParams> _calls = new();

    public IReadOnlyList<MessageCreateParams> Calls => _calls;

    public void EnqueueResponse(string json)
        => _responses.Enqueue(BuildMessage(json));

    /// <summary>Enqueues a simple assistant text response.</summary>
    public void EnqueueText(string text, int inputTokens = 100, int outputTokens = 20)
        => EnqueueResponse(TextMessageJson(text, inputTokens, outputTokens));

    /// <summary>Enqueues a response with a single tool_use block.</summary>
    public void EnqueueToolUse(string toolId, string toolName, string inputJson)
        => EnqueueResponse(ToolUseMessageJson(toolId, toolName, inputJson));

    public Task<Message> CreateAsync(MessageCreateParams createParams, CancellationToken ct)
    {
        _calls.Add(createParams);
        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException(
                $"FakeMessageCreator: no response queued for call #{_calls.Count}");
        return Task.FromResult(response);
    }

    // ── JSON templates ────────────────────────────────────────────────────────

    public static string TextMessageJson(string text, int inputTokens = 100, int outputTokens = 20) => $$"""
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "model": "claude-haiku-4-5",
          "stop_reason": "end_turn",
          "stop_sequence": null,
          "content": [{"type": "text", "text": {{JsonSerializer.Serialize(text)}}}],
          "usage": {
            "input_tokens": {{inputTokens}},
            "output_tokens": {{outputTokens}},
            "cache_creation_input_tokens": null,
            "cache_read_input_tokens": null
          }
        }
        """;

    public static string ToolUseMessageJson(string toolId, string toolName, string inputJson) => $$"""
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "model": "claude-haiku-4-5",
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "content": [{
            "type": "tool_use",
            "id": {{JsonSerializer.Serialize(toolId)}},
            "name": {{JsonSerializer.Serialize(toolName)}},
            "input": {{inputJson}}
          }],
          "usage": {
            "input_tokens": 100,
            "output_tokens": 50,
            "cache_creation_input_tokens": null,
            "cache_read_input_tokens": null
          }
        }
        """;

    private static Message BuildMessage(string json)
    {
        var msg = JsonSerializer.Deserialize<Message>(json);
        return msg ?? throw new InvalidOperationException("Failed to deserialize Message from JSON");
    }
}
