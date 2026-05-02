namespace SecondBrain.Files.Models;

public sealed record SourceFolder(
    string Id,
    string AbsolutePath,
    IReadOnlySet<string> ExcludeSubfolders);
