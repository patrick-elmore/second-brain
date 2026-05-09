using System.Text;

namespace SecondBrain.Files;

public sealed class FileReader
{
    private readonly IReadOnlyList<string> _allowedRoots;

    public FileReader(IReadOnlyList<string> allowedRoots)
    {
        _allowedRoots = allowedRoots
            .Select(r => NormalizePath(r))
            .ToList();
    }

    public string Read(string absolutePath)
    {
        var normalized = NormalizePath(absolutePath);

        if (!IsPathAllowed(normalized))
            throw new UnauthorizedAccessException(
                $"Path is outside allowed roots: {absolutePath}");

        if (!File.Exists(normalized))
            throw new FileNotFoundException($"File not found: {absolutePath}");

        var bytes = File.ReadAllBytes(normalized);

        if (!IsValidUtf8(bytes))
            throw new InvalidDataException($"File is not valid UTF-8 (binary file): {absolutePath}");

        // Strip UTF-8 BOM (EF BB BF) if present
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        return Encoding.UTF8.GetString(bytes);
    }

    private bool IsPathAllowed(string normalizedPath)
    {
        foreach (var root in _allowedRoots)
        {
            // Ensure the normalized path is under the root (with separator to prevent prefix attacks)
            if (normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                // Exact match OR the next char is a separator
                if (normalizedPath.Length == root.Length ||
                    normalizedPath[root.Length] == Path.DirectorySeparatorChar ||
                    normalizedPath[root.Length] == Path.AltDirectorySeparatorChar)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string NormalizePath(string path)
    {
        // Resolve . and .. and normalize separators
        return Path.GetFullPath(path);
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            // Use DecoderFallbackException to detect invalid sequences
            var decoder = Encoding.GetEncoding(
                "utf-8",
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback).GetDecoder();

            var charBuffer = new char[bytes.Length];
            decoder.GetChars(bytes, 0, bytes.Length, charBuffer, 0, flush: true);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
