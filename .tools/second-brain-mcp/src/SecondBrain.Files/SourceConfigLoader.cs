using System.Text.Json;
using SecondBrain.Files.Models;

namespace SecondBrain.Files;

public sealed class SourceConfigLoader
{
    public IReadOnlyList<SourceFolder> Load(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Sources config not found: {configPath}");

        using var stream = File.OpenRead(configPath);
        var entries = JsonSerializer.Deserialize<JsonElement[]>(stream)
            ?? throw new InvalidDataException("sources.json must be a JSON array");

        var result = new List<SourceFolder>();

        foreach (var entry in entries)
        {
            if (!entry.TryGetProperty("id", out var idProp))
                throw new InvalidDataException("Each source entry must have an 'id' field");

            var id = idProp.GetString()
                ?? throw new InvalidDataException("Source 'id' must be a non-null string");

            if (entry.TryGetProperty("path", out var pathProp))
            {
                var path = pathProp.GetString()
                    ?? throw new InvalidDataException($"Source '{id}': 'path' must be a non-null string");

                var excludes = ReadExcludeSubfolders(entry);
                result.Add(new SourceFolder(id, path, excludes));
            }
            else if (entry.TryGetProperty("discover", out var discoverProp))
            {
                result.AddRange(ExpandDiscover(id, discoverProp));
            }
            else
            {
                throw new InvalidDataException($"Source '{id}': must have either 'path' or 'discover'");
            }
        }

        return result;
    }

    private static IReadOnlySet<string> ReadExcludeSubfolders(JsonElement entry)
    {
        if (!entry.TryGetProperty("exclude_subfolders", out var excludeProp))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in excludeProp.EnumerateArray())
        {
            var name = item.GetString();
            if (name != null)
                set.Add(name);
        }
        return set;
    }

    private static IEnumerable<SourceFolder> ExpandDiscover(string id, JsonElement discoverProp)
    {
        var root = discoverProp.TryGetProperty("root", out var rootProp)
            ? rootProp.GetString() ?? throw new InvalidDataException($"Source '{id}': discover.root must be a string")
            : throw new InvalidDataException($"Source '{id}': discover requires 'root'");

        var directoryName = discoverProp.TryGetProperty("directory_name", out var dnProp)
            ? dnProp.GetString() ?? throw new InvalidDataException($"Source '{id}': discover.directory_name must be a string")
            : throw new InvalidDataException($"Source '{id}': discover requires 'directory_name'");

        var maxDepth = discoverProp.TryGetProperty("max_depth", out var mdProp)
            ? mdProp.GetInt32()
            : 4;

        if (!Directory.Exists(root))
            yield break;

        foreach (var dir in FindDirectoriesByName(root, directoryName, maxDepth))
        {
            yield return new SourceFolder(id, dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<string> FindDirectoriesByName(string root, string targetName, int maxDepth)
    {
        return WalkForName(root, targetName, maxDepth, currentDepth: 0);
    }

    private static IEnumerable<string> WalkForName(string dir, string targetName, int maxDepth, int currentDepth)
    {
        if (currentDepth > maxDepth)
            yield break;

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var subdir in subdirs)
        {
            var name = Path.GetFileName(subdir);

            if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
            {
                yield return subdir;
                // don't recurse into a matched directory
            }
            else
            {
                foreach (var found in WalkForName(subdir, targetName, maxDepth, currentDepth + 1))
                    yield return found;
            }
        }
    }
}
