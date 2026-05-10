namespace SecondBrain.Files;

/// <summary>
/// Universal-noise exclusion patterns applied to every source folder, in addition
/// to per-source <c>exclude_subfolders</c> from <c>config/sources.json</c>.
///
/// These patterns are informed by typical .gitignore content for the kinds of
/// repos a personal-knowledge index sees (C#, Node, Python). The intent is to
/// keep build outputs, vendor trees, and minified bundles out of the FTS index
/// regardless of where they sit on disk.
/// </summary>
public static class ScanDefaults
{
    /// <summary>
    /// Directory names skipped at any depth during scan. Case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludeSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Build outputs
        "bin", "obj", "dist", "build",
        // Vendor
        "node_modules", "bower_components", "vendor",
        // Caches
        "__pycache__", ".pytest_cache", ".mypy_cache",
        ".sass-cache", ".parcel-cache", ".next", ".nuxt",
        // IDE
        ".vs", ".idea",
        // VCS metadata
        ".git", ".hg", ".svn",
        // Test outputs
        "coverage", "TestResults",
    };

    /// <summary>
    /// File-name suffixes skipped during scan. Case-insensitive. Minified bundles,
    /// source maps, and Python bytecode are noise. Native binaries (.dll/.exe/.so)
    /// are already filtered by the UTF-8 detector in FileReader, so listing them
    /// here would be redundant.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludeFileExtensions = new[]
    {
        ".min.js", ".min.css", ".js.map", ".css.map",
        ".pyc", ".pyo", ".pyd",
        ".jsonl",
        ".svg",
        ".xml", ".json", ".backup",
    };
}
