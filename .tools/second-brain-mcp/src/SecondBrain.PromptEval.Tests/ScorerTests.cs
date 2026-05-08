using SecondBrain.PromptEval.Scoring;

namespace SecondBrain.PromptEval.Tests;

public sealed class ScorerTests
{
    [Fact]
    public void Score_PerfectMatch_F2IsOne()
    {
        var s = Scorer.Score(["a.md"], ["a.md"]);

        s.Precision.Should().Be(1.0);
        s.Recall.Should().Be(1.0);
        s.F2.Should().Be(1.0);
    }

    [Fact]
    public void Score_NoMatch_F2IsZero()
    {
        var s = Scorer.Score(["a.md"], ["b.md"]);

        s.Precision.Should().Be(0.0);
        s.Recall.Should().Be(0.0);
        s.F2.Should().Be(0.0);
    }

    [Fact]
    public void Score_MissedAllExpected_RecallZero_F2Zero()
    {
        var s = Scorer.Score(["a.md", "b.md"], []);

        s.Precision.Should().Be(0.0);
        s.Recall.Should().Be(0.0);
        s.F2.Should().Be(0.0);
    }

    [Fact]
    public void Score_FoundExpectedPlusExtras_RecallBetterThanPrecision()
    {
        // Expected one doc, returned three (one correct, two extra)
        var s = Scorer.Score(["a.md"], ["a.md", "b.md", "c.md"]);

        s.Recall.Should().Be(1.0);
        s.Precision.Should().BeApproximately(1.0 / 3.0, 0.001);
        // F2 with recall=1, precision=1/3:
        // (1+4)*P*R / (4*P + R) = 5 * 1/3 * 1 / (4/3 + 1) = (5/3) / (7/3) = 5/7
        s.F2.Should().BeApproximately(5.0 / 7.0, 0.001);
    }

    [Fact]
    public void Score_FoundSomeExpected_PartialRecall()
    {
        // Expected two, found one
        var s = Scorer.Score(["a.md", "b.md"], ["a.md"]);

        s.Recall.Should().Be(0.5);
        s.Precision.Should().Be(1.0);
        // F2 with P=1, R=0.5: (1+4)*1*0.5 / (4*1 + 0.5) = 2.5 / 4.5 ≈ 0.556
        s.F2.Should().BeApproximately(2.5 / 4.5, 0.001);
    }

    [Fact]
    public void Score_RecallWeightsHeavier_LowerRecallHurtMore()
    {
        // A: high recall, low precision
        var a = Scorer.Score(["x.md", "y.md"], ["x.md", "y.md", "z1.md", "z2.md", "z3.md"]);
        // B: low recall, high precision
        var b = Scorer.Score(["x.md", "y.md"], ["x.md"]);

        // A has recall=1.0, precision=0.4
        // B has recall=0.5, precision=1.0
        // F1 would say B (P=1.0, R=0.5 → 0.667) beats A (P=0.4, R=1.0 → 0.571)
        // F2 should flip the ranking — A wins because recall counts more

        a.Recall.Should().Be(1.0);
        b.Recall.Should().Be(0.5);
        a.F2.Should().BeGreaterThan(b.F2);
    }

    [Fact]
    public void Score_CaseInsensitive()
    {
        var s = Scorer.Score(["/Path/To/A.md"], ["/path/to/a.md"]);

        s.F2.Should().Be(1.0);
    }
}
