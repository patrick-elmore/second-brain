namespace SecondBrain.Files.Models;

public sealed record SourceFile(
    string SourceFolderId,
    string AbsolutePath,
    string RelativePath,
    long SizeBytes,
    double MTime);
