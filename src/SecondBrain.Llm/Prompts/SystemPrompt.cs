namespace SecondBrain.Llm.Prompts;

/// <summary>
/// Loads the system prompt from <c>Prompts.local/system_prompt.md</c> and the
/// alias map from <c>Prompts.local/aliases.md</c> (both gitignored), then
/// splices the aliases into the prompt at the <c>{ALIASES}</c> marker.
///
/// On first access, if a live file does not exist, it is bootstrapped from
/// the corresponding <c>Prompts/&lt;name&gt;-template.md</c> that ships with
/// the binary. Subsequent edits to the live file take effect on next process
/// start. The templates are committed to source control as generic examples;
/// the live files are personal and never committed.
/// </summary>
public static class SystemPrompt
{
    public const string AliasMarker = "{ALIASES}";

    private static readonly string PromptsDir =
        Path.Combine(AppContext.BaseDirectory, "Prompts");

    // Single source of truth across all binaries: repo-root Prompts.local in dev,
    // BaseDirectory/Prompts.local in production deploys. Walk up from the binary
    // looking for the solution file marker; if found we are in a dev tree.
    private static readonly string LocalDir = ResolveLocalDir();

    private static string ResolveLocalDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "second-brain-mcp.slnx")))
                return Path.Combine(dir.FullName, "Prompts.local");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Prompts.local");
    }

    /// <summary>The raw template with {ALIASES} unsubstituted. Tunable surface.</summary>
    public static string Template { get; } = LoadOrBootstrap("system_prompt");

    /// <summary>The aliases content that gets substituted at runtime.</summary>
    public static string Aliases { get; } = LoadOrBootstrap("aliases");

    /// <summary>The fully-resolved system prompt with aliases substituted.</summary>
    public static string Text { get; } = SubstituteAliases(Template);

    /// <summary>Apply alias substitution to an arbitrary template string.</summary>
    public static string SubstituteAliases(string template) => template.Replace(AliasMarker, Aliases);

    private static string LoadOrBootstrap(string name)
    {
        var livePath = Path.Combine(LocalDir, $"{name}.md");
        var templatePath = Path.Combine(PromptsDir, $"{name}-template.md");

        if (!File.Exists(livePath))
        {
            if (!File.Exists(templatePath))
                throw new FileNotFoundException(
                    $"Neither live prompt ('{livePath}') nor template ('{templatePath}') was found. " +
                    "Templates ship with the binary via CopyToOutputDirectory; check the build output.");
            Directory.CreateDirectory(LocalDir);
            File.Copy(templatePath, livePath);
        }

        return File.ReadAllText(livePath);
    }
}
