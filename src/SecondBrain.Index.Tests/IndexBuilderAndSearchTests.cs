using System.Text;
using Microsoft.Data.Sqlite;
using SecondBrain.Index.Indexing;
using SecondBrain.Index.Search;

namespace SecondBrain.Index.Tests;

/// <summary>
/// Integration tests using a real temporary SQLite database.
/// </summary>
public sealed class IndexBuilderAndSearchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _ftsDb;
    private readonly string _configPath;

    public IndexBuilderAndSearchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _sourceDir = Path.Combine(_tempDir, "sources");
        _ftsDb = Path.Combine(_tempDir, "fts.db");
        _configPath = Path.Combine(_tempDir, "sources.json");
        Directory.CreateDirectory(_sourceDir);

        File.WriteAllText(_configPath,
            $$"""[{"id":"test-src","path":"{{_sourceDir.Replace("\\", "\\\\")}}" }]""",
            Encoding.UTF8);
    }

    public void Dispose()
    {
        // Release all pooled SQLite connections before deleting temp files
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteSource(string name, string content)
    {
        File.WriteAllText(Path.Combine(_sourceDir, name), content, new UTF8Encoding(false));
    }

    private IndexBuildSummary BuildIndex(int maxBytes = 5_000_000)
    {
        var builder = new IndexBuilder();
        return builder.Build(_configPath, _ftsDb, maxBytes);
    }

    private SearchResult Search(SearchParams p) => new SearchEngine(_ftsDb).Search(p);

    // ── IndexBuilder ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_EmptySourceFolder_ReturnsZeroIndexed()
    {
        var summary = BuildIndex();

        summary.IndexedCount.Should().Be(0);
        summary.SkippedCount.Should().Be(0);
        File.Exists(_ftsDb).Should().BeTrue();
    }

    [Fact]
    public void Build_TextFiles_AreIndexed()
    {
        WriteSource("note.md", "This is a test note about authentication.");
        WriteSource("standup.md", "Daily standup: worked on deployment pipeline.");

        var summary = BuildIndex();

        summary.IndexedCount.Should().Be(2);
        summary.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void Build_BinaryFile_IsSkipped()
    {
        WriteSource("note.md", "Some text content.");
        File.WriteAllBytes(Path.Combine(_sourceDir, "image.bin"), [0xFF, 0xFE, 0x00, 0xD8, 0xC0, 0x80]);

        var summary = BuildIndex();

        summary.IndexedCount.Should().Be(1);
        summary.SkippedCount.Should().Be(1);
    }

    [Fact]
    public void Build_OversizedFile_IsSkipped()
    {
        WriteSource("small.md", "small");
        WriteSource("big.md", new string('x', 200));

        // Oversized files are excluded by the scanner before reaching the builder,
        // so IndexedCount = 1 (small file) and SkippedCount = 0 (no binary/unreadable files).
        var summary = BuildIndex(maxBytes: 10);

        summary.IndexedCount.Should().Be(1);
        summary.SkippedCount.Should().Be(0);

        // Verify the big file is not in the index
        var result = Search(new SearchParams(Query: string.Join(" ", Enumerable.Repeat("x", 5))));
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Build_FullRebuild_ReplacesExistingIndex()
    {
        WriteSource("first.md", "First pass content.");
        BuildIndex();

        // Remove the first file, add a new one
        File.Delete(Path.Combine(_sourceDir, "first.md"));
        WriteSource("second.md", "Second pass content.");
        var summary = BuildIndex();

        summary.IndexedCount.Should().Be(1);

        // Search for first-pass content should return nothing
        var result = Search(new SearchParams(Query: "first pass"));
        result.Hits.Should().BeEmpty();

        // Second-pass content should be found
        var result2 = Search(new SearchParams(Query: "second pass"));
        result2.Hits.Should().HaveCount(1);
    }

    [Fact]
    public void Build_ExtractsSourceTypeFromYamlFrontmatter()
    {
        WriteSource("meeting.md", """
            ---
            type: transcript
            attendees:
              - alice@test.com
            ---
            Meeting content here.
            """);

        BuildIndex();

        var result = Search(new SearchParams(SourceType: ["transcript"]));
        result.Hits.Should().HaveCount(1);
    }

    // ── SearchEngine — text search ────────────────────────────────────────────

    [Fact]
    public void Search_MalformedFtsQuery_ReturnsEmptyHitsWithoutThrowing()
    {
        // FTS5 treats "vin AND" as a column reference and throws SqliteException.
        // The engine should swallow that and return empty so the LLM can retry.
        WriteSource("a.md", "some content here");
        BuildIndex();

        var act = () => Search(new SearchParams(Query: "vin AND"));

        var result = act.Should().NotThrow().Subject;
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Search_QueryWithSpecialChars_ReturnsEmptyInsteadOfCrashing()
    {
        WriteSource("a.md", "content");
        BuildIndex();

        // ".NET 10" produces SqliteException because FTS5 treats "NET" as a column ref
        var act = () => Search(new SearchParams(Query: ".NET 10 migration"));

        var result = act.Should().NotThrow().Subject;
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Search_TextQuery_MatchesContent()
    {
        WriteSource("auth.md", "Authentication tokens expire after 24 hours.");
        WriteSource("deploy.md", "Deployment pipeline runs on every commit.");
        BuildIndex();

        var result = Search(new SearchParams(Query: "authentication token"));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].RelativePath.Should().Contain("auth");
    }

    [Fact]
    public void Search_PathWeightedHigher_PathMatchRanksAboveContentMatch()
    {
        // File whose name contains the search term vs file whose content contains it
        WriteSource("deployment.md", "This is about something unrelated.");
        WriteSource("other.md", "This note discusses deployment extensively and provides many deployment details.");
        BuildIndex();

        var result = Search(new SearchParams(Query: "deployment"));

        result.Hits.Should().NotBeEmpty();
        // The file named "deployment.md" should rank first due to 10x path weight
        result.Hits[0].RelativePath.Should().Contain("deployment");
    }

    [Fact]
    public void Search_ReturnModeSnippets_PopulatesMatches()
    {
        WriteSource("note.md", "The quick brown fox jumps over the lazy dog authentication details here.");
        BuildIndex();

        var result = Search(new SearchParams(Query: "authentication", ReturnMode: "snippets"));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].Matches.Should().NotBeEmpty();
        result.Hits[0].Matches[0].Snippet.Should().Contain("authentication");
    }

    [Fact]
    public void Search_ReturnModePaths_EmptyMatches()
    {
        WriteSource("note.md", "Content about authentication.");
        BuildIndex();

        var result = Search(new SearchParams(Query: "authentication", ReturnMode: "paths"));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].Matches.Should().BeEmpty();
    }

    [Fact]
    public void Search_NoQuery_NoFilters_ReturnsEmpty()
    {
        WriteSource("note.md", "Content here.");
        BuildIndex();

        var result = Search(new SearchParams());
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Search_TopLimit_Respected()
    {
        for (var i = 0; i < 10; i++)
            WriteSource($"file{i}.md", "authentication is important for security.");
        BuildIndex();

        var result = Search(new SearchParams(Query: "authentication", Top: 3));
        result.Hits.Should().HaveCount(3);
    }

    // ── SearchEngine — structured filters ────────────────────────────────────

    [Fact]
    public void Search_SourceTypeFilter_ReturnsOnlyMatching()
    {
        WriteSource("transcript.md", """
            ---
            type: transcript
            created: 2026-04-01T10:00:00Z
            ---
            Content about authentication.
            """);
        WriteSource("note.md", """
            ---
            type: note
            created: 2026-04-01T10:00:00Z
            ---
            Content about authentication.
            """);
        BuildIndex();

        var result = Search(new SearchParams(SourceType: ["transcript"]));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].RelativePath.Should().Contain("transcript");
    }

    [Fact]
    public void Search_SourceFolderFilter_ReturnsOnlyFromFolder()
    {
        // Two source folders
        var dir2 = Path.Combine(_tempDir, "sources2");
        Directory.CreateDirectory(dir2);

        File.WriteAllText(Path.Combine(_sourceDir, "from-src1.md"), "content here", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir2, "from-src2.md"), "content here", new UTF8Encoding(false));

        File.WriteAllText(_configPath,
            $$"""
            [
              {"id":"src1","path":"{{_sourceDir.Replace("\\", "\\\\")}}"},
              {"id":"src2","path":"{{dir2.Replace("\\", "\\\\")}}"}
            ]
            """, Encoding.UTF8);

        BuildIndex();

        var result = Search(new SearchParams(SourceFolders: ["src1"]));
        result.Hits.Should().HaveCount(1);
        result.Hits[0].SourceFolderId.Should().Be("src1");
    }

    [Fact]
    public void Search_DateRangeFilter_FiltersCorrectly()
    {
        // ISO date in filename → path regex sets effective_date; no frontmatter config needed.
        WriteSource("2026-01-15-jan.md", "January note content.");
        WriteSource("2026-04-15-apr.md", "April note content.");
        BuildIndex();

        var result = Search(new SearchParams(
            DateStart: new DateOnly(2026, 4, 1),
            DateEnd: new DateOnly(2026, 4, 30)));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].RelativePath.Should().Contain("apr");
    }

    [Fact]
    public void Search_PeopleFilter_MatchesAttendee()
    {
        WriteSource("meeting.md", """
            ---
            type: transcript
            attendees:
              - alice@example.com
              - bob@example.com
            ---
            Meeting content.
            """);
        WriteSource("other.md", """
            ---
            type: note
            ---
            Other content.
            """);
        BuildIndex();

        var result = Search(new SearchParams(People: ["alice@example.com"]));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].RelativePath.Should().Contain("meeting");
    }

    [Fact]
    public void Search_FtsWithSourceTypeFilter_CombinesCorrectly()
    {
        WriteSource("t1.md", """
            ---
            type: transcript
            ---
            Authentication discussion happened here.
            """);
        WriteSource("n1.md", """
            ---
            type: note
            ---
            Authentication notes written down.
            """);
        BuildIndex();

        var result = Search(new SearchParams(Query: "authentication", SourceType: ["transcript"]));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].RelativePath.Should().Contain("t1");
    }

    [Fact]
    public void Search_ListSources_PopulatesSourcesSummary()
    {
        WriteSource("f1.md", "authentication content");
        WriteSource("f2.md", "authentication content again");
        BuildIndex();

        var result = Search(new SearchParams(Query: "authentication", ListSources: true));

        result.SourcesSummary.Should().NotBeNull();
        result.SourcesSummary!.Should().HaveCount(1);
        result.SourcesSummary[0].SourceFolderId.Should().Be("test-src");
        result.SourcesSummary[0].HitCount.Should().Be(2);
    }

    [Fact]
    public void Search_ListSources_NullWhenFalse()
    {
        WriteSource("f1.md", "authentication");
        BuildIndex();

        var result = Search(new SearchParams(Query: "authentication", ListSources: false));

        result.SourcesSummary.Should().BeNull();
    }

    [Fact]
    public void Search_NoIndex_ReturnsEmpty()
    {
        var result = new SearchEngine("/nonexistent/path/fts.db").Search(new SearchParams(Query: "anything"));
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Search_MetadataPopulatedInHits()
    {
        WriteSource("note.md", """
            ---
            type: note
            created: 2026-04-01T10:00:00Z
            attendees:
              - alice@example.com
            ---
            Content here.
            """);
        BuildIndex();

        var result = Search(new SearchParams(SourceType: ["note"]));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].Metadata.Should().NotBeNull();
        result.Hits[0].Metadata!.Value.GetProperty("type").GetString().Should().Be("note");
    }

    // ── Date columns ─────────────────────────────────────────────────────────

    private string? ReadColumn(string relativePath, string column)
    {
        var connStr = new SqliteConnectionStringBuilder
            { DataSource = _ftsDb, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM files WHERE relative_path = @p";
        cmd.Parameters.AddWithValue("@p", relativePath);
        return cmd.ExecuteScalar() as string;
    }

    private double? ReadRealColumn(string relativePath, string column)
    {
        var connStr = new SqliteConnectionStringBuilder
            { DataSource = _ftsDb, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM files WHERE relative_path = @p";
        cmd.Parameters.AddWithValue("@p", relativePath);
        var val = cmd.ExecuteScalar();
        return val == null || val == DBNull.Value ? null : Convert.ToDouble(val);
    }

    [Fact]
    public void Build_FileWithDateInFilename_EffectiveDateExtractedFromPath()
    {
        WriteSource("2026-03-15-daily-note.md", "Some content.");
        BuildIndex();

        var effectiveDate = ReadRealColumn("2026-03-15-daily-note.md", "effective_date");
        var expected = (double)new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        effectiveDate.Should().Be(expected);
    }

    [Fact]
    public void Build_FileWithFrontmatterFolder_EffectiveDateExtractedFromFrontmatter_FullPrecision()
    {
        var content = """
            ---
            created: 2026-04-27T18:39:34.838Z
            type: transcript
            ---
            Meeting content.
            """;
        WriteSource("meeting.txt", content);

        var builder = new IndexBuilder();
        builder.Build(_configPath, _ftsDb, 5_000_000, frontmatterDateFolders: [_sourceDir]);

        var effectiveDate = ReadRealColumn("meeting.txt", "effective_date");
        var expected = (double)DateTimeOffset.Parse("2026-04-27T18:39:34.838Z").ToUnixTimeSeconds();
        effectiveDate.Should().Be(expected);
    }

    [Fact]
    public void Build_FileWithNeitherFrontmatterNorPathDate_EffectiveDateIsNull()
    {
        WriteSource("plain-note.md", "No date anywhere.");
        BuildIndex();

        var effectiveDate = ReadRealColumn("plain-note.md", "effective_date");
        effectiveDate.Should().BeNull();
    }

    [Fact]
    public void Build_FileCreatedAt_AndModifiedAt_ArePopulated()
    {
        WriteSource("timed.md", "content");
        BuildIndex();

        var createdAt = ReadRealColumn("timed.md", "file_created_at");
        var modifiedAt = ReadRealColumn("timed.md", "file_modified_at");

        createdAt.Should().NotBeNull();
        createdAt!.Value.Should().BeGreaterThan(0);
        modifiedAt.Should().NotBeNull();
        modifiedAt!.Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Build_FileWithNoDateSignals_EffectiveDateIsNull()
    {
        WriteSource("a.md", "content");
        WriteSource("b.md", "other content");
        BuildIndex();

        var connStr = new SqliteConnectionStringBuilder
            { DataSource = _ftsDb, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Files with no date in filename and no frontmatter should have NULL effective_date
        cmd.CommandText = "SELECT COUNT(*) FROM files WHERE effective_date IS NOT NULL";
        var nonNullCount = (long)cmd.ExecuteScalar()!;
        nonNullCount.Should().Be(0);
    }

    [Fact]
    public void Build_AllFiles_HaveLocalDatePopulated()
    {
        WriteSource("dated.md", "content");
        WriteSource("2026-05-08-named.md", "dated by filename");
        BuildIndex();

        var connStr = new SqliteConnectionStringBuilder
            { DataSource = _ftsDb, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // local_date must always be populated — never NULL
        cmd.CommandText = "SELECT COUNT(*) FROM files WHERE local_date IS NULL";
        var nullCount = (long)cmd.ExecuteScalar()!;
        nullCount.Should().Be(0);
    }

    [Fact]
    public void Build_FileWithDateInFilename_LocalDateMatchesFilenameDate()
    {
        WriteSource("2026-03-15-daily-note.md", "Some content.");
        BuildIndex();

        var localDate = ReadColumn("2026-03-15-daily-note.md", "local_date");
        localDate.Should().Be("2026-03-15");
    }

    [Fact]
    public void Build_FileWithNoDateSignal_LocalDateFromCtime()
    {
        WriteSource("nodatefile.md", "No date anywhere in name or content.");
        BuildIndex();

        var localDate = ReadColumn("nodatefile.md", "local_date");
        // local_date must be a valid YYYY-MM-DD string (derived from filesystem ctime)
        localDate.Should().NotBeNullOrEmpty();
        DateOnly.TryParseExact(localDate!, "yyyy-MM-dd", out _).Should().BeTrue();
    }

    [Fact]
    public void Search_DateRangeFilter_UsesLocalDate_FiltersCorrectly()
    {
        // local_date column drives date filtering; filename-based dates set local_date directly.
        WriteSource("2026-01-15-jan.md", "January note content.");
        WriteSource("2026-04-15-apr.md", "April note content.");
        BuildIndex();

        var result = Search(new SearchParams(
            DateStart: new DateOnly(2026, 4, 1),
            DateEnd: new DateOnly(2026, 4, 30)));

        result.Hits.Should().HaveCount(1);
        result.Hits[0].RelativePath.Should().Contain("apr");
    }
}
