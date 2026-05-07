using System.Reflection;

namespace SecondBrain.Llm.Prompts;

/// <summary>
/// Loads the system prompt from the embedded <c>Prompts/system_prompt.md</c>
/// resource at first access, then splices in <c>Prompts/aliases.md</c> at
/// the <c>{ALIASES}</c> marker. The markdown files are the single source of
/// truth — edit them, rebuild, redeploy.
/// </summary>
internal static class SystemPrompt
{
    private const string PromptResource = "SecondBrain.Llm.Prompts.system_prompt.md";
    private const string AliasResource = "SecondBrain.Llm.Prompts.aliases.md";
    private const string AliasMarker = "{ALIASES}";

    public static string Text { get; } = BuildText();

    private static string BuildText()
    {
        var prompt = LoadEmbeddedResource(PromptResource);
        var aliases = LoadEmbeddedResource(AliasResource);
        return prompt.Replace(AliasMarker, aliases);
    }

    private static string LoadEmbeddedResource(string name)
    {
        var assembly = typeof(SystemPrompt).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' not found. Available resources: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
