using SecondBrain.Index.Search;

namespace SecondBrain.Index.Tests;

public sealed class RrfFuserTests
{
    private static SearchHit Hit(string path, double score = 0) =>
        new("src", path, path, score, null, []);

    [Fact]
    public void Fuse_EmptyInput_ReturnsEmpty()
    {
        var result = RrfFuser.Fuse([], top: 10);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Fuse_SingleList_PreservesOrderWithRrfScores()
    {
        var list = new[] { Hit("a"), Hit("b"), Hit("c") };

        var result = RrfFuser.Fuse([list], top: 3);

        result.Select(h => h.RelativePath).Should().Equal("a", "b", "c");
        result[0].Score.Should().BeGreaterThan(result[1].Score);
        result[1].Score.Should().BeGreaterThan(result[2].Score);
    }

    [Fact]
    public void Fuse_ConsensusDocumentRanksFirst()
    {
        // List A: [X, Y, Z]   List B: [Y, X, W]
        // X: 1/61 + 1/62 ≈ 0.03246
        // Y: 1/62 + 1/61 ≈ 0.03246  (same as X, tied)
        // Z: 1/63 ≈ 0.01587  (only in A)
        // W: 1/63 ≈ 0.01587  (only in B)
        var listA = new[] { Hit("X"), Hit("Y"), Hit("Z") };
        var listB = new[] { Hit("Y"), Hit("X"), Hit("W") };

        var result = RrfFuser.Fuse([listA, listB], top: 10);

        // Both X and Y have the same total score; Z and W are lower
        result[0].Score.Should().BeApproximately(result[1].Score, precision: 1e-10);
        result[0].Score.Should().BeGreaterThan(result[2].Score);
        result[2].Score.Should().BeApproximately(result[3].Score, precision: 1e-10);
    }

    [Fact]
    public void Fuse_DocOnlyInOneList_RanksBelowConsensus()
    {
        var listA = new[] { Hit("consensus"), Hit("solo-a") };
        var listB = new[] { Hit("consensus"), Hit("solo-b") };

        var result = RrfFuser.Fuse([listA, listB], top: 10);

        result[0].RelativePath.Should().Be("consensus");
        result[0].Score.Should().BeGreaterThan(result[1].Score);
    }

    [Fact]
    public void Fuse_TopParameter_TruncatesResults()
    {
        var list = Enumerable.Range(0, 20).Select(i => Hit($"doc{i}")).ToArray();

        var result = RrfFuser.Fuse([list], top: 5);

        result.Should().HaveCount(5);
    }

    [Fact]
    public void Fuse_ScoreFormula_MatchesExpected()
    {
        // Rank 0 (1-based rank 1): 1/(60+1) ≈ 0.016393
        var list = new[] { Hit("only") };

        var result = RrfFuser.Fuse([list], top: 1);

        result[0].Score.Should().BeApproximately(1.0 / 61.0, precision: 1e-10);
    }

    [Fact]
    public void Fuse_SnippetFromFirstList()
    {
        var hitWithSnippet = new SearchHit("src", "doc", "doc", 0, null,
            [new SnippetMatch("the snippet")]);
        var hitWithoutSnippet = new SearchHit("src", "doc", "doc", 0, null, []);

        var listA = new[] { hitWithSnippet };
        var listB = new[] { hitWithoutSnippet };

        var result = RrfFuser.Fuse([listA, listB], top: 1);

        result[0].Matches.Should().HaveCount(1);
        result[0].Matches[0].Snippet.Should().Be("the snippet");
    }
}
