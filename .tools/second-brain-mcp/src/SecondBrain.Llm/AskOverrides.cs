using Anthropic.Models.Messages;

namespace SecondBrain.Llm;

/// <summary>
/// Per-call overrides for tunable surfaces. Used by the prompt-eval harness
/// to swap prompts and tool definitions at call time without rebuilding.
/// All fields default to null, in which case the production embedded values
/// (system prompt, tool definitions, identity wrapper) are used.
/// </summary>
public sealed record AskOverrides(
    string? SystemPromptOverride = null,
    IReadOnlyList<ToolUnion>? ToolsOverride = null,
    string? UserMessageWrapperTemplate = null);
