using System.Text;
using FluentAssertions;
using SecondBrain.Files;

namespace SecondBrain.Files.Tests;

public sealed class FileReaderTests : IDisposable
{
    private readonly string _allowedRoot;
    private readonly string _otherRoot;

    public FileReaderTests()
    {
        _allowedRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _otherRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_allowedRoot);
        Directory.CreateDirectory(_otherRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_allowedRoot)) Directory.Delete(_allowedRoot, recursive: true);
        if (Directory.Exists(_otherRoot)) Directory.Delete(_otherRoot, recursive: true);
    }

    private string WriteUtf8(string relativePath, string content)
    {
        var full = Path.Combine(_allowedRoot, relativePath);
        File.WriteAllText(full, content, Encoding.UTF8);
        return full;
    }

    private string WriteBinary(string relativePath, byte[] bytes)
    {
        var full = Path.Combine(_allowedRoot, relativePath);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private FileReader MakeReader() => new([_allowedRoot]);

    [Fact]
    public void Read_ValidUtf8File_ReturnsContent()
    {
        var path = WriteUtf8("note.md", "Hello, world!");
        var reader = MakeReader();

        var content = reader.Read(path);

        content.Should().Be("Hello, world!");
    }

    [Fact]
    public void Read_FileWithUnicodeContent_ReturnsContent()
    {
        var text = "日本語テスト — emoji: 🎉";
        var path = WriteUtf8("unicode.md", text);
        var reader = MakeReader();

        var content = reader.Read(path);

        content.Should().Be(text);
    }

    [Fact]
    public void Read_FileOutsideAllowedRoot_ThrowsUnauthorizedAccess()
    {
        var outsidePath = Path.Combine(_otherRoot, "secret.md");
        File.WriteAllText(outsidePath, "secret");
        var reader = MakeReader();

        var act = () => reader.Read(outsidePath);

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Read_PathTraversalAttempt_ThrowsUnauthorizedAccess()
    {
        // Construct a path that tries to escape via ..
        var escapedPath = Path.Combine(_allowedRoot, "..", "escape.md");
        var reader = MakeReader();

        var act = () => reader.Read(escapedPath);

        // Path.GetFullPath will resolve the .., putting it outside _allowedRoot
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Read_PrefixAttack_ThrowsUnauthorizedAccess()
    {
        // A path that starts with the allowed root string but isn't inside it
        // e.g., allowed = /tmp/abc, attack = /tmp/abc-evil/file.md
        var attackRoot = _allowedRoot + "-evil";
        Directory.CreateDirectory(attackRoot);
        var attackFile = Path.Combine(attackRoot, "file.md");
        File.WriteAllText(attackFile, "attack");

        try
        {
            var reader = MakeReader();
            var act = () => reader.Read(attackFile);

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(attackRoot, recursive: true);
        }
    }

    [Fact]
    public void Read_BinaryFile_ThrowsInvalidDataException()
    {
        // Write bytes with invalid UTF-8 sequences
        var bytes = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0xD8, 0x00 }; // invalid UTF-8
        var path = WriteBinary("binary.bin", bytes);
        var reader = MakeReader();

        var act = () => reader.Read(path);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*binary*");
    }

    [Fact]
    public void Read_NullByte_ThrowsInvalidDataException()
    {
        // A file with null bytes embedded (not valid UTF-8 text in practice)
        // Actually null bytes ARE valid UTF-8 (they're just U+0000).
        // Test actual invalid UTF-8 instead.
        var bytes = new byte[] { 0xC0, 0x80 }; // overlong encoding — invalid UTF-8
        var path = WriteBinary("overlong.bin", bytes);
        var reader = MakeReader();

        var act = () => reader.Read(path);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Read_FileNotFound_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(_allowedRoot, "missing.md");
        var reader = MakeReader();

        var act = () => reader.Read(missingPath);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Read_MultipleAllowedRoots_AllowsFilesInEither()
    {
        var secondRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(secondRoot);

        try
        {
            var fileInSecond = Path.Combine(secondRoot, "note.md");
            File.WriteAllText(fileInSecond, "in second root");

            var reader = new FileReader([_allowedRoot, secondRoot]);

            var content = reader.Read(fileInSecond);
            content.Should().Be("in second root");
        }
        finally
        {
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public void Read_EmptyFile_ReturnsEmptyString()
    {
        var path = WriteUtf8("empty.md", string.Empty);
        var reader = MakeReader();

        var content = reader.Read(path);

        content.Should().Be(string.Empty);
    }

    [Fact]
    public void Read_FileInSubdirectory_IsAllowed()
    {
        var subDir = Path.Combine(_allowedRoot, "sub", "nested");
        Directory.CreateDirectory(subDir);
        var path = Path.Combine(subDir, "note.md");
        File.WriteAllText(path, "nested content");

        var reader = MakeReader();
        var content = reader.Read(path);

        content.Should().Be("nested content");
    }
}
