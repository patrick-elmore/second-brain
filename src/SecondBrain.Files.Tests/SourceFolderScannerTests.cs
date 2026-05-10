using FluentAssertions;
using SecondBrain.Files;
using SecondBrain.Files.Models;

namespace SecondBrain.Files.Tests;

public sealed class SourceFolderScannerTests : IDisposable
{
    private readonly string _tempDir;

    public SourceFolderScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Write(string relativePath, string content = "hello")
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private SourceFolder Folder(IReadOnlySet<string>? excludes = null) =>
        new("test-id", _tempDir, excludes ?? new HashSet<string>());

    [Fact]
    public void Scan_EmptyFolder_ReturnsEmpty()
    {
        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 1_000_000).ToList();
        result.Should().BeEmpty();
    }

    [Fact]
    public void Scan_SingleFile_ReturnsIt()
    {
        Write("notes.md");
        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 1_000_000).ToList();

        result.Should().HaveCount(1);
        result[0].SourceFolderId.Should().Be("test-id");
        result[0].RelativePath.Should().Be("notes.md");
        result[0].SizeBytes.Should().Be(5); // "hello"
        result[0].MTime.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Scan_NestedFiles_ReturnsAll()
    {
        Write("a.md");
        Write("sub/b.md");
        Write("sub/deep/c.md");

        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 1_000_000).ToList();

        result.Should().HaveCount(3);
        result.Select(f => f.RelativePath).Should().BeEquivalentTo(
            ["a.md", Path.Combine("sub", "b.md"), Path.Combine("sub", "deep", "c.md")]);
    }

    [Fact]
    public void Scan_SkipsOversizedFiles()
    {
        Write("small.md", "small");
        Write("big.md", new string('x', 100));

        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 10).ToList();

        result.Should().HaveCount(1);
        result[0].RelativePath.Should().Be("small.md");
    }

    [Fact]
    public void Scan_ExactlyMaxBytes_IsIncluded()
    {
        var content = new string('a', 50);
        Write("exact.md", content);

        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 50).ToList();

        result.Should().HaveCount(1);
    }

    [Fact]
    public void Scan_OneBytOverMaxBytes_IsExcluded()
    {
        var content = new string('a', 51);
        Write("toobig.md", content);

        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 50).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void Scan_RespectsExcludeSubfolders()
    {
        Write("keep.md");
        Write(".obsidian/settings.json");
        Write(".obsidian/themes/dark.css");
        Write("notes/note.md");

        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".obsidian" };
        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(excludes), maxBytes: 1_000_000).ToList();

        result.Select(f => f.RelativePath).Should().BeEquivalentTo(
            ["keep.md", Path.Combine("notes", "note.md")]);
    }

    [Fact]
    public void Scan_ExcludeIsCaseInsensitive()
    {
        Write("Archive/old.md");
        Write("keep.md");

        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "archive" };
        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(excludes), maxBytes: 1_000_000).ToList();

        result.Should().HaveCount(1);
        result[0].RelativePath.Should().Be("keep.md");
    }

    [Fact]
    public void Scan_FolderDoesNotExist_ReturnsEmpty()
    {
        var missing = new SourceFolder("id", "/nonexistent/path/xyz", new HashSet<string>());
        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(missing, maxBytes: 1_000_000).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void Scan_RelativePathUsesOsSeparator()
    {
        Write(Path.Combine("sub", "nested", "file.md"));
        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 1_000_000).ToList();

        result.Should().HaveCount(1);
        result[0].RelativePath.Should().Be(Path.Combine("sub", "nested", "file.md"));
    }

    [Fact]
    public void Scan_SourceFolderIdPropagated()
    {
        Write("a.md");
        var folder = new SourceFolder("my-source", _tempDir, new HashSet<string>());
        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(folder, maxBytes: 1_000_000).ToList();

        result.Should().AllSatisfy(f => f.SourceFolderId.Should().Be("my-source"));
    }

    [Fact]
    public void Scan_DefaultExcludesSkipNodeModulesAndBin()
    {
        Write("keep.md");
        Write("node_modules/lib/index.js");
        Write("bin/Debug/app.dll.config");
        Write("src/code.ts");

        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 1_000_000).ToList();

        result.Select(f => f.RelativePath).Should().BeEquivalentTo(
            ["keep.md", Path.Combine("src", "code.ts")]);
    }

    [Fact]
    public void Scan_DefaultExcludesSkipMinifiedAndSourceMaps()
    {
        Write("app.js");
        Write("app.min.js");
        Write("styles.css");
        Write("styles.min.css");
        Write("bundle.js.map");
        Write("bundle.css.map");

        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 1_000_000).ToList();

        result.Select(f => f.RelativePath).Should().BeEquivalentTo(["app.js", "styles.css"]);
    }

    [Fact]
    public void Scan_DefaultExcludesAreCaseInsensitive()
    {
        Write("BIN/x.md");
        Write("Node_Modules/y.md");
        Write("APP.MIN.JS");
        Write("keep.md");

        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(), maxBytes: 1_000_000).ToList();

        result.Select(f => f.RelativePath).Should().BeEquivalentTo(["keep.md"]);
    }

    [Fact]
    public void Scan_PerSourceExcludesAddToDefaults()
    {
        Write("keep.md");
        Write("node_modules/lib.js"); // default exclude
        Write("Archive/old.md");      // per-source exclude

        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Archive" };
        var scanner = new SourceFolderScanner();
        var result = scanner.Scan(Folder(excludes), maxBytes: 1_000_000).ToList();

        result.Select(f => f.RelativePath).Should().BeEquivalentTo(["keep.md"]);
    }
}
