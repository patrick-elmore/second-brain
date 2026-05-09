namespace SecondBrain.Index.Search;

public sealed record SearchParams(
    string? Query = null,
    DateOnly? DateStart = null,
    DateOnly? DateEnd = null,
    IReadOnlyList<string>? People = null,
    IReadOnlyList<string>? SourceType = null,
    IReadOnlyList<string>? SourceFolders = null,
    int Top = 30,
    int SnippetTokens = 32,
    string ReturnMode = "snippets",
    bool ListSources = false);
