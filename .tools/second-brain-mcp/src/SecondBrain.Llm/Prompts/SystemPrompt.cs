using System.Reflection;

namespace SecondBrain.Llm.Prompts;

/// <summary>
/// Loads the system prompt from the embedded <c>Prompts/system_prompt.md</c>
/// resource at first access. The file is the single source of truth for the
/// prompt text — edit it, rebuild, redeploy.
/// </summary>
internal static class SystemPrompt
{
    private const string ResourceName = "SecondBrain.Llm.Prompts.system_prompt.md";

    public static string Text { get; } = LoadEmbeddedResource(ResourceName);

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
