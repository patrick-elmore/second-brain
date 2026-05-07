using Anthropic;
using Anthropic.Models.Messages;

namespace SecondBrain.Llm;

/// <summary>
/// Abstraction over the Anthropic SDK's message-creation API.
/// Exists only to enable unit testing without hitting the network.
/// </summary>
public interface IMessageCreator
{
    Task<Message> CreateAsync(MessageCreateParams createParams, CancellationToken ct);
}

/// <summary>
/// Production adapter wrapping <see cref="IAnthropicClient"/>.
/// </summary>
public sealed class AnthropicMessageCreator : IMessageCreator
{
    private readonly IAnthropicClient _client;

    public AnthropicMessageCreator(IAnthropicClient client) => _client = client;

    public Task<Message> CreateAsync(MessageCreateParams createParams, CancellationToken ct)
        => _client.Messages.Create(createParams, ct);
}
