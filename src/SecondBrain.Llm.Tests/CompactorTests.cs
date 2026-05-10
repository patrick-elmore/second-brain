using System.Text.Json;
using Anthropic.Models.Messages;
using SecondBrain.Llm;
using SecondBrain.Llm.Tests.Fakes;

namespace SecondBrain.Llm.Tests;

public sealed class CompactorTests
{
    private static Compactor MakeCompactor(FakeMessageCreator fake, IStatsRecorder? stats = null)
        => new(fake, "claude-sonnet-4-6", stats: stats);

    private static List<MessageParam> TwoMessages() =>
    [
        new() { Role = Role.User, Content = "What is the plan?" },
        new() { Role = Role.Assistant, Content = "We will deploy on Friday." },
    ];

    // Extract the system prompt text by serializing the captured params to JSON
    private static string GetSystemText(MessageCreateParams call)
    {
        var json = JsonSerializer.Serialize(call);
        // System can be a string or a list of blocks; the raw JSON contains the text either way
        return json;
    }

    [Fact]
    public async Task CompactAsync_DefaultInstruction_MakesOneApiCall()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Here is the compacted summary.");
        var compactor = MakeCompactor(fake);

        await compactor.CompactAsync(TwoMessages(), null, CancellationToken.None);

        fake.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task CompactAsync_DefaultInstruction_SystemDoesNotContainAdditionalInstructionMarker()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Summary.");
        var compactor = MakeCompactor(fake);

        await compactor.CompactAsync(TwoMessages(), null, CancellationToken.None);

        var paramsJson = GetSystemText(fake.Calls[0]);
        paramsJson.Should().NotContain("Additional instruction:");
    }

    [Fact]
    public async Task CompactAsync_CustomInstruction_AppendsToDefaultPrompt()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Compact summary.");
        var compactor = MakeCompactor(fake);

        await compactor.CompactAsync(TwoMessages(), "Focus on decisions only.", CancellationToken.None);

        var paramsJson = GetSystemText(fake.Calls[0]);
        paramsJson.Should().Contain("Focus on decisions only.");
        paramsJson.Should().Contain("Additional instruction:");
    }

    [Fact]
    public async Task CompactAsync_WhitespaceInstruction_TreatedAsDefault()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Summary.");
        var compactor = MakeCompactor(fake);

        await compactor.CompactAsync(TwoMessages(), "   ", CancellationToken.None);

        var paramsJson = GetSystemText(fake.Calls[0]);
        paramsJson.Should().NotContain("Additional instruction:");
    }

    [Fact]
    public async Task CompactAsync_ReturnsJoinedTextFromResponse()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("The team is deploying Friday.");
        var compactor = MakeCompactor(fake);

        var result = await compactor.CompactAsync(TwoMessages(), null, CancellationToken.None);

        result.Summary.Should().Be("The team is deploying Friday.");
    }

    [Fact]
    public async Task CompactAsync_NoStats_CostIsZero()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Summary.");
        var compactor = MakeCompactor(fake, stats: null);

        var result = await compactor.CompactAsync(TwoMessages(), null, CancellationToken.None);

        result.EstimatedCostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task CompactAsync_ConversationTextIncludedInUserMessage()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Summary.");
        var compactor = MakeCompactor(fake);
        var messages = new List<MessageParam>
        {
            new() { Role = Role.User, Content = "Tell me about cats." },
            new() { Role = Role.Assistant, Content = "Cats are independent animals." },
        };

        await compactor.CompactAsync(messages, null, CancellationToken.None);

        // The conversation should appear somewhere in the serialized call
        var paramsJson = GetSystemText(fake.Calls[0]);
        paramsJson.Should().Contain("cats");
        paramsJson.Should().Contain("Cats are independent animals");
    }

    [Fact]
    public async Task CompactAsync_UsesConfiguredModel()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueText("Summary.");
        var compactor = new Compactor(fake, "claude-haiku-4-5");

        await compactor.CompactAsync(TwoMessages(), null, CancellationToken.None);

        fake.Calls[0].Model.Json.GetString().Should().Be("claude-haiku-4-5");
    }
}
