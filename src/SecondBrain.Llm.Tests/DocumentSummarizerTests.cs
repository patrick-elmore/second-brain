using SecondBrain.Llm;
using SecondBrain.Llm.Tests.Fakes;

namespace SecondBrain.Llm.Tests;

public sealed class DocumentSummarizerTests : IDisposable
{
    private readonly string _tempDir;

    public DocumentSummarizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteDoc(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private BatchDocEntry MakeEntry(string name, string content, string? sourceType = null, long? id = null)
    {
        var path = WriteDoc(name, content);
        return new BatchDocEntry(id ?? 1L, path, name, sourceType, null);
    }

    private static DocumentSummarizer MakeSummarizer(FakeMessageCreator fake)
        => new(fake);

    private static string SummaryResponseJson(int seqId, string summaryText) =>
        FakeMessageCreator.TextMessageJson(
            $"=====BEGIN:SUMMARY:{seqId}=====\n{summaryText}\n=====END:SUMMARY:{seqId}=====");

    // ── pre-flight filtering ──────────────────────────────────────────────────

    [Fact]
    public async Task SummarizeBatch_ContentTooShort_SkipsWithoutApiCall()
    {
        var fake = new FakeMessageCreator();
        var entry = MakeEntry("short.md", "Too short.");
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([entry], CancellationToken.None);

        fake.Calls.Should().BeEmpty();
        results.Should().HaveCount(1);
        results[0].Outcome.Should().Be(SummarizationOutcome.Skipped);
        results[0].Reason.Should().Contain("content too short");
    }

    [Fact]
    public async Task SummarizeBatch_UnreadableFile_SkipsWithoutApiCall()
    {
        var fake = new FakeMessageCreator();
        var entry = new BatchDocEntry(1L, "/nonexistent/path.md", "path.md", null, null);
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([entry], CancellationToken.None);

        fake.Calls.Should().BeEmpty();
        results.Should().HaveCount(1);
        results[0].Outcome.Should().Be(SummarizationOutcome.Skipped);
        results[0].Reason.Should().Contain("unreadable");
    }

    [Fact]
    public async Task SummarizeBatch_AllSkipped_NoApiCall()
    {
        var fake = new FakeMessageCreator();
        var e1 = MakeEntry("a.md", "Short");
        var e2 = new BatchDocEntry(2L, "/bad/path.md", "bad.md", null, null);
        var summarizer = MakeSummarizer(fake);

        await summarizer.SummarizeBatchAsync([e1, e2], CancellationToken.None);

        fake.Calls.Should().BeEmpty();
    }

    // ── successful summarization ──────────────────────────────────────────────

    [Fact]
    public async Task SummarizeBatch_SingleReadableDoc_ApiCalledOnce()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueResponse(SummaryResponseJson(1, "This document covers deployment pipelines."));
        var entry = MakeEntry("doc.md", new string('x', 200));
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([entry], CancellationToken.None);

        fake.Calls.Should().HaveCount(1);
        results.Should().HaveCount(1);
        results[0].Outcome.Should().Be(SummarizationOutcome.Summarized);
        results[0].Summary.Should().Contain("deployment pipelines");
    }

    [Fact]
    public async Task SummarizeBatch_ParsedSummaryAppendedToResult()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueResponse(SummaryResponseJson(1, "Quarterly review meeting."));
        var entry = MakeEntry("review.md", new string('a', 200));
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([entry], CancellationToken.None);

        results[0].Summary.Should().Contain("Quarterly review meeting.");
    }

    [Fact]
    public async Task SummarizeBatch_DocUserMessageContainsDocBlock()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueResponse(SummaryResponseJson(1, "Summary text."));
        var entry = MakeEntry("note.md", new string('b', 200), "note");
        var summarizer = MakeSummarizer(fake);

        await summarizer.SummarizeBatchAsync([entry], CancellationToken.None);

        // The user message should contain a DOC:1 block
        var callJson = System.Text.Json.JsonSerializer.Serialize(fake.Calls[0]);
        callJson.Should().Contain("BEGIN:DOC:1");
        callJson.Should().Contain("END:DOC:1");
    }

    [Fact]
    public async Task SummarizeBatch_BatchOfTwo_SequenceIdsAscending()
    {
        var fake = new FakeMessageCreator();
        var response = FakeMessageCreator.TextMessageJson(
            "=====BEGIN:SUMMARY:1=====\nFirst doc summary.\n=====END:SUMMARY:1=====\n" +
            "=====BEGIN:SUMMARY:2=====\nSecond doc summary.\n=====END:SUMMARY:2=====");
        fake.EnqueueResponse(response);

        var e1 = MakeEntry("a.md", new string('a', 200), id: 1);
        var e2 = MakeEntry("b.md", new string('b', 200), id: 2);
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([e1, e2], CancellationToken.None);

        fake.Calls.Should().HaveCount(1); // single API call for both
        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Outcome == SummarizationOutcome.Summarized);
    }

    // ── failure outcomes ──────────────────────────────────────────────────────

    [Fact]
    public async Task SummarizeBatch_ApiThrows_AllPreparedDocsFail()
    {
        var fake = new FakeMessageCreator();
        // Don't enqueue a response — any call will throw
        var e1 = MakeEntry("a.md", new string('a', 200), id: 1);
        var e2 = MakeEntry("b.md", new string('b', 200), id: 2);
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([e1, e2], CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Outcome == SummarizationOutcome.Failed);
    }

    [Fact]
    public async Task SummarizeBatch_MissingSummaryBlockForDoc_Skipped()
    {
        var fake = new FakeMessageCreator();
        // Response contains summary for doc 1, but NOT doc 2
        var response = FakeMessageCreator.TextMessageJson(
            "=====BEGIN:SUMMARY:1=====\nOnly first.\n=====END:SUMMARY:1=====");
        fake.EnqueueResponse(response);
        var e1 = MakeEntry("a.md", new string('a', 200), id: 1);
        var e2 = MakeEntry("b.md", new string('b', 200), id: 2);
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([e1, e2], CancellationToken.None);

        var r1 = results.Single(r => r.Id == 1);
        var r2 = results.Single(r => r.Id == 2);
        r1.Outcome.Should().Be(SummarizationOutcome.Summarized);
        r2.Outcome.Should().Be(SummarizationOutcome.Skipped);
    }

    [Fact]
    public async Task SummarizeBatch_EmptySummaryBlock_Skipped()
    {
        var fake = new FakeMessageCreator();
        var response = FakeMessageCreator.TextMessageJson(
            "=====BEGIN:SUMMARY:1=====\n   \n=====END:SUMMARY:1=====");
        fake.EnqueueResponse(response);
        var entry = MakeEntry("doc.md", new string('c', 200), id: 1);
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([entry], CancellationToken.None);

        results[0].Outcome.Should().Be(SummarizationOutcome.Skipped);
    }

    // ── InputCharLimit ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1on1", 24_000)]
    [InlineData("transcript", 20_000)]
    [InlineData("standup", 6_000)]
    [InlineData("planning", 16_000)]
    [InlineData("note", 8_000)]
    [InlineData(null, 12_000)]
    [InlineData("unknown_type", 12_000)]
    public void InputCharLimit_DefaultDict_ReturnsDocumentedValues(string? sourceType, int expectedLimit)
    {
        var summarizer = MakeSummarizer(new FakeMessageCreator());

        summarizer.InputCharLimit(sourceType).Should().Be(expectedLimit);
    }

    [Fact]
    public void InputCharLimit_OverrideDict_HonoredForKnownAndUnknownTypes()
    {
        var fake = new FakeMessageCreator();
        var customLimits = new Dictionary<string, int>
        {
            ["1on1"] = 1234,
            ["default"] = 5678,
        };
        var summarizer = new DocumentSummarizer(fake, inputCharLimits: customLimits);

        summarizer.InputCharLimit("1on1").Should().Be(1234);
        summarizer.InputCharLimit("anything-else").Should().Be(5678);
    }

    // ── prefix construction ───────────────────────────────────────────────────

    [Fact]
    public async Task SummarizeBatch_DateInFilename_PresentInSummary()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueResponse(SummaryResponseJson(1, "Content of the note."));
        // File name contains a date
        var entry = MakeEntry("2025-03-15 meeting.md", new string('x', 200), "note");
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([entry], CancellationToken.None);

        // The resulting summary should include the date prefix
        results[0].Summary.Should().Contain("2025-03-15");
    }

    [Fact]
    public async Task SummarizeBatch_Transcript_SummaryContainsSourceType()
    {
        var fake = new FakeMessageCreator();
        fake.EnqueueResponse(SummaryResponseJson(1, "Meeting about deployment."));
        var metadataJson = """{"created":"2025-03-15","attendees":["Alice","Bob"]}""";
        var path = WriteDoc("transcript.md", new string('t', 200));
        var entry = new BatchDocEntry(1L, path, "transcript.md", "transcript", metadataJson);
        var summarizer = MakeSummarizer(fake);

        var results = await summarizer.SummarizeBatchAsync([entry], CancellationToken.None);

        results[0].Outcome.Should().Be(SummarizationOutcome.Summarized);
        results[0].Summary.Should().Contain("transcript");
        results[0].Summary.Should().Contain("Alice");
    }

    // ── SummarizationResult factories ─────────────────────────────────────────

    [Fact]
    public void SummarizationResult_Ok_HasCorrectOutcome()
    {
        var r = SummarizationResult.Ok(42L, "the summary");
        r.Id.Should().Be(42L);
        r.Outcome.Should().Be(SummarizationOutcome.Summarized);
        r.Summary.Should().Be("the summary");
        r.Reason.Should().BeNull();
    }

    [Fact]
    public void SummarizationResult_Skip_HasCorrectOutcome()
    {
        var r = SummarizationResult.Skip(7L, "too short");
        r.Outcome.Should().Be(SummarizationOutcome.Skipped);
        r.Reason.Should().Be("too short");
        r.Summary.Should().BeNull();
    }

    [Fact]
    public void SummarizationResult_Fail_HasCorrectOutcome()
    {
        var r = SummarizationResult.Fail(3L, "network error");
        r.Outcome.Should().Be(SummarizationOutcome.Failed);
        r.Reason.Should().Be("network error");
        r.Summary.Should().BeNull();
    }
}
