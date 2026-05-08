using System.Reflection;

namespace SecondBrain.Llm.Prompts;

/// <summary>
/// Loads the system prompt from the embedded <c>Prompts/system_prompt.md</c>
/// resource at first access, then splices in <c>Prompts/aliases.md</c> at
/// the <c>{ALIASES}</c> marker. The markdown files are the single source of
/// truth — edit them, rebuild, redeploy.
/// </summary>
public static class SystemPrompt
{
    private const string PromptResource = "SecondBrain.Llm.Prompts.system_prompt.md";
    private const string AliasResource = "SecondBrain.Llm.Prompts.aliases.md";
    public const string AliasMarker = "{ALIASES}";

    /// <summary>The raw template with {ALIASES} unsubstituted. Tunable surface.</summary>
    public static string Template { get; } = LoadEmbeddedResource(PromptResource);

    /// <summary>The aliases content that gets substituted at runtime.</summary>
    public static string Aliases { get; } = LoadEmbeddedResource(AliasResource);

    /// <summary>The fully-resolved system prompt with aliases substituted.</summary>
    public static string Text { get; } = SubstituteAliases(Template);

    /// <summary>Apply alias substitution to an arbitrary template string.</summary>
    public static string SubstituteAliases(string template) => template.Replace(AliasMarker, Aliases);

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
