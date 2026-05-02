namespace SecondBrain.Index.RequestHistory;

public sealed record RequestFileEntity(
    int Rank,
    string AbsolutePath,
    string RelativePath,
    string SourceFolderId,
    double? Score);
