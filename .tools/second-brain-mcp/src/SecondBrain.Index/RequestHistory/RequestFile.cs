namespace SecondBrain.Index.RequestHistory;

public sealed record RequestFile(
    int Rank,
    string AbsolutePath,
    string RelativePath,
    string SourceFolderId,
    double? Score);
