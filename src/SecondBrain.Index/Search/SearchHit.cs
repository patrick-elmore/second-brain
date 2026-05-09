using System.Text.Json;

namespace SecondBrain.Index.Search;

public sealed record SearchHit(
    string SourceFolderId,
    string AbsolutePath,
    string RelativePath,
    double Score,
    JsonElement? Metadata,
    IReadOnlyList<SnippetMatch> Matches);
