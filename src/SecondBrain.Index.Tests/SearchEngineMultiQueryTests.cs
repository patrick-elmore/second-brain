using System.Text;
using Microsoft.Data.Sqlite;
using SecondBrain.Index.Indexing;
using SecondBrain.Index.Search;

namespace SecondBrain.Index.Tests;

public sealed class SearchEngineMultiQueryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _ftsDb;
    private readonly string _configPath;

    public SearchEngineMultiQueryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _sourceDir = Path.Combine(_tempDir, "sources");
        _ftsDb = Path.Combine(_tempDir, "fts.db");
        _configPath = Path.Combine(_tempDir, "sources.json");
        Directory.CreateDirectory(_sourceDir);
        File.WriteAllText(_configPath,
            $$"""[{"id":"src","path":"{{_sourceDir.Replace("\\", "\\\\")}}"}]""",
            Encoding.UTF8);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteSource(string name, string content)
        => File.WriteAllText(Path.Combine(_sourceDir, name), content, new UTF8Encoding(false));

    private void BuildIndex()
        => new IndexBuilder().Build(_configPath, _ftsDb, 5_000_000);

    private SearchEngine Engine() => new(_ftsDb);

    [Fact]
    public void SearchMulti_EmptyQueryList_FallsThroughToFilterOnly()
    {
        // Filter-only path with no filters returns empty; ensure no crash
        var result = Engine().SearchMulti([], new SearchParams());
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void SearchMulti_WhitespaceOnlyQueries_TreatedAsEmpty()
    {
        WriteSource("a.md", "content");
        BuildIndex();

        var result = Engine().SearchMulti(["  ", "\t", ""], new SearchParams());

        // Should fall through to filter-only; no FTS query fired
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void SearchMulti_SingleQuery_ReturnsHits()
    {
        WriteSource("alpha.md", "authentication and authorization system");
        WriteSource("beta.md", "unrelated content about widgets");
        BuildIndex();

        var result = Engine().SearchMulti(["authentication"], new SearchParams(Top: 10));

        result.Hits.Should().ContainSingle(h => h.RelativePath == "alpha.md");
    }

    [Fact]
    public void SearchMulti_TwoQueries_ConsensusDocRanksFirst()
    {
        // "consensus.md" matches both queries; others match only one
        WriteSource("consensus.md", "deployment pipeline authentication");
        WriteSource("only-deploy.md", "deployment build artifacts");
        WriteSource("only-auth.md", "authentication tokens");
        BuildIndex();

        var result = Engine().SearchMulti(
            ["deployment", "authentication"],
            new SearchParams(Top: 10));

        result.Hits.Should().NotBeEmpty();
        result.Hits[0].RelativePath.Should().Be("consensus.md");
    }

    [Fact]
    public void SearchMulti_TopParameter_HonoredAfterFusion()
    {
        for (var i = 0; i < 10; i++)
            WriteSource($"doc{i}.md", $"search keyword result {i}");
        BuildIndex();

        var result = Engine().SearchMulti(["search keyword"], new SearchParams(Top: 3));

        result.Hits.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void SearchMulti_FilterAppliedToAllVariants()
    {
        WriteSource("transcript.md", "---\ntype: transcript\n---\ndeployment discussion");
        WriteSource("note.md", "---\ntype: note\n---\ndeployment notes");
        BuildIndex();

        var result = Engine().SearchMulti(
            ["deployment"],
            new SearchParams(Top: 10, SourceType: ["transcript"]));

        result.Hits.Should().OnlyContain(h => h.RelativePath == "transcript.md");
    }

    [Fact]
    public void SearchMulti_ListSources_PopulatesSourcesSummary()
    {
        WriteSource("a.md", "unique term xyz");
        BuildIndex();

        var result = Engine().SearchMulti(
            ["unique term xyz"],
            new SearchParams(Top: 10, ListSources: true));

        result.SourcesSummary.Should().NotBeNull();
        result.SourcesSummary!.Should().ContainSingle(s => s.SourceFolderId == "src");
    }

    [Fact]
    public void SearchMulti_DbDoesNotExist_ReturnsEmpty()
    {
        var engine = new SearchEngine(Path.Combine(_tempDir, "nonexistent.db"));

        var result = engine.SearchMulti(["anything"], new SearchParams());

        result.Hits.Should().BeEmpty();
        result.SourcesSummary.Should().BeNull();
    }

    [Fact]
    public void SearchMulti_HitsHaveRrfScoresPositive()
    {
        WriteSource("a.md", "machine learning model training");
        WriteSource("b.md", "machine learning inference serving");
        BuildIndex();

        var result = Engine().SearchMulti(
            ["machine learning"],
            new SearchParams(Top: 10));

        // RRF scores are positive (unlike BM25 which is negative)
        result.Hits.Should().AllSatisfy(h => h.Score.Should().BeGreaterThan(0));
    }

    [Fact]
    public void SearchMulti_ResultsOrdered_HigherScoreFirst()
    {
        WriteSource("consensus.md", "kubernetes deployment scale");
        WriteSource("partial.md", "deployment only");
        BuildIndex();

        var result = Engine().SearchMulti(
            ["kubernetes", "deployment"],
            new SearchParams(Top: 10));

        if (result.Hits.Count > 1)
        {
            for (var i = 0; i < result.Hits.Count - 1; i++)
                result.Hits[i].Score.Should().BeGreaterThanOrEqualTo(result.Hits[i + 1].Score);
        }
    }

    [Fact]
    public void Search_MaxSnippetTokensOverride_ClampsCallerRequest()
    {
        // Long content so the snippet can be measurable.
        WriteSource("long.md", string.Join(' ', Enumerable.Repeat("alpha", 200)));
        BuildIndex();

        // Engine constructed with maxSnippetTokens=8; caller asks for 100.
        var engine = new SearchEngine(_ftsDb, maxSnippetTokens: 8);
        var result = engine.Search(new SearchParams(Query: "alpha", SnippetTokens: 100, Top: 1));

        result.Hits.Should().HaveCount(1);
        var snippet = result.Hits[0].Matches[0].Snippet;
        // SQLite's snippet() returns the requested tokens trimmed; ensure the
        // trimmed snippet has roughly the cap-many tokens (allowing ellipsis).
        var tokenCount = snippet.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        tokenCount.Should().BeLessThanOrEqualTo(10); // 8 + a couple of marker tokens
    }
}
