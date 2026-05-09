using System.Text;
using FluentAssertions;
using SecondBrain.Files;

namespace SecondBrain.Files.Tests;

public sealed class SourceConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public SourceConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_tempDir, "sources.json");
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void Load_StaticPath_ReturnsSingleFolder()
    {
        var path = WriteConfig("""
            [
              { "id": "my-notes", "path": "/some/path" }
            ]
            """);

        var loader = new SourceConfigLoader();
        var result = loader.Load(path);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("my-notes");
        result[0].AbsolutePath.Should().Be("/some/path");
        result[0].ExcludeSubfolders.Should().BeEmpty();
    }

    [Fact]
    public void Load_StaticPathWithExcludes_PopulatesExcludes()
    {
        var path = WriteConfig("""
            [
              { "id": "obsidian", "path": "/data/obsidian", "exclude_subfolders": [".obsidian", "Archive"] }
            ]
            """);

        var loader = new SourceConfigLoader();
        var result = loader.Load(path);

        result.Should().HaveCount(1);
        result[0].ExcludeSubfolders.Should().BeEquivalentTo([".obsidian", "Archive"]);
    }

    [Fact]
    public void Load_DiscoverEntry_ExpandsMatchingDirectories()
    {
        // Create structure: root/repo-a/.context, root/repo-b/.context, root/repo-b/sub/.context
        var root = Path.Combine(_tempDir, "repos");
        Directory.CreateDirectory(Path.Combine(root, "repo-a", ".context"));
        Directory.CreateDirectory(Path.Combine(root, "repo-b", ".context"));
        Directory.CreateDirectory(Path.Combine(root, "repo-b", "sub", ".context"));

        var configPath = WriteConfig($$"""
            [
              {
                "id": "repos-context",
                "discover": {
                  "root": "{{root.Replace("\\", "\\\\")}}",
                  "directory_name": ".context",
                  "max_depth": 4
                }
              }
            ]
            """);

        var loader = new SourceConfigLoader();
        var result = loader.Load(configPath);

        result.Should().HaveCount(3);
        result.Select(f => f.Id).Should().AllBe("repos-context");
        result.Select(f => f.AbsolutePath).Should().Contain(Path.Combine(root, "repo-a", ".context"));
        result.Select(f => f.AbsolutePath).Should().Contain(Path.Combine(root, "repo-b", ".context"));
        result.Select(f => f.AbsolutePath).Should().Contain(Path.Combine(root, "repo-b", "sub", ".context"));
    }

    [Fact]
    public void Load_DiscoverEntry_RespectsMaxDepth()
    {
        // depth 0=root, 1=repo-a, 2=.context. max_depth=1 means scan 1 level into root.
        var root = Path.Combine(_tempDir, "repos2");
        Directory.CreateDirectory(Path.Combine(root, "repo-a", ".context"));
        Directory.CreateDirectory(Path.Combine(root, "repo-a", "nested", ".context"));

        var configPath = WriteConfig($$"""
            [
              {
                "id": "shallow",
                "discover": {
                  "root": "{{root.Replace("\\", "\\\\")}}",
                  "directory_name": ".context",
                  "max_depth": 1
                }
              }
            ]
            """);

        var loader = new SourceConfigLoader();
        var result = loader.Load(configPath);

        // repo-a/.context is at depth 1 — should be found
        // repo-a/nested/.context is at depth 2 — should be excluded
        result.Should().HaveCount(1);
        result[0].AbsolutePath.Should().Be(Path.Combine(root, "repo-a", ".context"));
    }

    [Fact]
    public void Load_DiscoverRootDoesNotExist_ReturnsEmpty()
    {
        var configPath = WriteConfig("""
            [
              {
                "id": "missing",
                "discover": {
                  "root": "/nonexistent/path/that/does/not/exist",
                  "directory_name": ".context",
                  "max_depth": 4
                }
              }
            ]
            """);

        var loader = new SourceConfigLoader();
        var result = loader.Load(configPath);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Load_MixedEntries_ReturnsBoth()
    {
        var staticPath = Path.Combine(_tempDir, "static");
        var discoverRoot = Path.Combine(_tempDir, "discover-root");
        Directory.CreateDirectory(Path.Combine(discoverRoot, "repo", ".context"));

        var configPath = WriteConfig($$"""
            [
              { "id": "static-src", "path": "{{staticPath.Replace("\\", "\\\\")}}" },
              {
                "id": "dynamic",
                "discover": {
                  "root": "{{discoverRoot.Replace("\\", "\\\\")}}",
                  "directory_name": ".context",
                  "max_depth": 3
                }
              }
            ]
            """);

        var loader = new SourceConfigLoader();
        var result = loader.Load(configPath);

        result.Should().HaveCount(2);
        result.Should().Contain(f => f.Id == "static-src");
        result.Should().Contain(f => f.Id == "dynamic");
    }

    [Fact]
    public void Load_ConfigNotFound_ThrowsFileNotFoundException()
    {
        var loader = new SourceConfigLoader();

        var act = () => loader.Load("/nonexistent/sources.json");

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Load_EntryMissingId_ThrowsInvalidDataException()
    {
        var path = WriteConfig("""[ { "path": "/some/path" } ]""");

        var loader = new SourceConfigLoader();

        var act = () => loader.Load(path);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Load_EntryMissingPathAndDiscover_ThrowsInvalidDataException()
    {
        var path = WriteConfig("""[ { "id": "bad-entry" } ]""");

        var loader = new SourceConfigLoader();

        var act = () => loader.Load(path);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Load_DoesNotRecurseIntoMatchedDirectories()
    {
        // .context folder that itself contains a .context subfolder — should not be found
        var root = Path.Combine(_tempDir, "repos3");
        Directory.CreateDirectory(Path.Combine(root, "repo", ".context", ".context"));

        var configPath = WriteConfig($$"""
            [
              {
                "id": "no-recurse",
                "discover": {
                  "root": "{{root.Replace("\\", "\\\\")}}",
                  "directory_name": ".context",
                  "max_depth": 4
                }
              }
            ]
            """);

        var loader = new SourceConfigLoader();
        var result = loader.Load(configPath);

        // Only the outer .context should be found, not the inner one
        result.Should().HaveCount(1);
        result[0].AbsolutePath.Should().Be(Path.Combine(root, "repo", ".context"));
    }
}
