using SecondBrain.Files;

namespace SecondBrain.Files.Tests;

public sealed class DateDeriverTests
{
    // A ctime that falls on 2026-04-27 regardless of timezone
    // (noon UTC = well within any local day).
    private static readonly double CtimeNoon = (double)DateTimeOffset.Parse("2026-04-27T12:00:00Z").ToUnixTimeSeconds();

    // A ctime whose local date is the key: 2026-05-09 00:39 UTC = 2026-05-08 19:39 CDT
    private static readonly double CtimeLateNightUtc = (double)DateTimeOffset.Parse("2026-05-09T00:39:00Z").ToUnixTimeSeconds();

    // ── Frontmatter path ─────────────────────────────────────────────────────

    [Fact]
    public void Derive_FileInFrontmatterFolder_UsesYamlCreatedField_FullPrecision()
    {
        var folder = @"C:\data\obsidian\Granola\Transcripts";
        var deriver = Deriver(folder);

        var content = """
            ---
            created: 2026-04-27T18:39:34.838Z
            type: transcript
            ---
            Body text.
            """;

        var result = deriver.Derive(
            @"C:\data\obsidian\Granola\Transcripts\my-meeting.txt",
            content,
            CtimeNoon);

        var expectedEpoch = (double)DateTimeOffset.Parse("2026-04-27T18:39:34.838Z").ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expectedEpoch);

        // LocalDate derived from frontmatter timestamp (18:39 UTC = any local tz same day or next)
        var expectedLocal = TimeZoneInfo.ConvertTime(
            DateTimeOffset.Parse("2026-04-27T18:39:34.838Z"), TimeZoneInfo.Local)
            .Date.ToString("yyyy-MM-dd");
        result.LocalDate.Should().Be(expectedLocal);
    }

    [Fact]
    public void Derive_FileInFrontmatterFolder_FrontmatterMissingCreated_FallsToPathRegex()
    {
        var folder = @"C:\data\obsidian";
        var deriver = Deriver(folder);

        var content = """
            ---
            type: note
            title: My Note
            ---
            Body.
            """;

        var result = deriver.Derive(
            @"C:\data\obsidian\2026-05-08.md",
            content,
            CtimeNoon);

        var expectedEpoch = (double)new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expectedEpoch);
        result.LocalDate.Should().Be("2026-05-08");
    }

    [Fact]
    public void Derive_FileInFrontmatterFolder_MalformedCreated_FallsToPathRegex()
    {
        var folder = @"C:\data\obsidian";
        var deriver = Deriver(folder);

        var content = """
            ---
            created: not-a-date
            ---
            """;

        var result = deriver.Derive(
            @"C:\data\obsidian\2026-03-01.md",
            content,
            CtimeNoon);

        var expectedEpoch = (double)new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expectedEpoch);
        result.LocalDate.Should().Be("2026-03-01");
    }

    [Fact]
    public void Derive_FileInFrontmatterFolder_NoFrontmatterNoPathDate_FallsToCtimeLocalDate()
    {
        var folder = @"C:\data\obsidian";
        var deriver = Deriver(folder);

        var result = deriver.Derive(
            @"C:\data\obsidian\no-date-anywhere.md",
            "just some text",
            CtimeNoon);

        // No confident epoch when only ctime is available
        result.EffectiveDate.Should().BeNull();

        // LocalDate must be the ctime converted to local calendar day
        var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UnixEpoch.AddSeconds(CtimeNoon), TimeZoneInfo.Local)
            .Date.ToString("yyyy-MM-dd");
        result.LocalDate.Should().Be(expectedLocal);
    }

    [Fact]
    public void Derive_FileOutsideFrontmatterFolders_SkipsFrontmatterCheck_UsesPathDate()
    {
        var folder = @"C:\data\obsidian\Granola\Transcripts";
        var deriver = Deriver(folder);

        var content = """
            ---
            created: 2026-04-27T18:39:34.838Z
            ---
            Body.
            """;

        var result = deriver.Derive(
            @"C:\other\notes\2025-12-31-retrospective.md",
            content,
            CtimeNoon);

        var expectedEpoch = (double)new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expectedEpoch);
        result.LocalDate.Should().Be("2025-12-31");
    }

    // ── Filepath regex path ───────────────────────────────────────────────────

    [Fact]
    public void Derive_IsoDashInFilename_ExtractsDateAtMidnightUtc()
    {
        var result = NoFolderDeriver().Derive(
            @"C:\notes\2026-05-08-daily-standup.md",
            "content",
            CtimeNoon);

        var expected = (double)new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expected);
        result.LocalDate.Should().Be("2026-05-08");
    }

    [Fact]
    public void Derive_NoIsoPatternInPath_ReturnsNull()
    {
        // Path like "2026\05\08\meeting.md" has no single-token ISO date
        var result = NoFolderDeriver().Derive(
            @"C:\notes\2026\05\08\meeting.md",
            "content",
            CtimeNoon);

        result.EffectiveDate.Should().BeNull();
    }

    [Fact]
    public void Derive_IsoUnderscoreInFilename_ExtractsDateAtMidnightUtc()
    {
        var result = NoFolderDeriver().Derive(
            @"C:\notes\2026_03_22_retrospective.md",
            "content",
            CtimeNoon);

        var expected = (double)new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expected);
        result.LocalDate.Should().Be("2026-03-22");
    }

    [Fact]
    public void Derive_UsStyleDashInFilename_ExtractsDateAtMidnightUtc()
    {
        var result = NoFolderDeriver().Derive(
            @"C:\archive\12-31-2025-year-end.md",
            "content",
            CtimeNoon);

        var expected = (double)new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expected);
        result.LocalDate.Should().Be("2025-12-31");
    }

    [Fact]
    public void Derive_MultiplePatternCandidates_FirstValidWins()
    {
        // Path has two ISO-dash candidates; first one wins
        var result = NoFolderDeriver().Derive(
            @"C:\2026-01-01\2026-02-15-note.md",
            "content",
            CtimeNoon);

        var expected = (double)new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expected);
        result.LocalDate.Should().Be("2026-01-01");
    }

    [Fact]
    public void Derive_InvalidMonthInPath_FallsToCtimeLocalDate()
    {
        var result = NoFolderDeriver().Derive(
            @"C:\notes\2026-13-45-invalid.md",
            "content",
            CtimeNoon);

        result.EffectiveDate.Should().BeNull();

        var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UnixEpoch.AddSeconds(CtimeNoon), TimeZoneInfo.Local)
            .Date.ToString("yyyy-MM-dd");
        result.LocalDate.Should().Be(expectedLocal);
    }

    [Fact]
    public void Derive_NoDateInPath_FallsToCtimeLocalDate()
    {
        var result = NoFolderDeriver().Derive(
            @"C:\notes\some-random-note.md",
            "content",
            CtimeNoon);

        result.EffectiveDate.Should().BeNull();

        var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UnixEpoch.AddSeconds(CtimeNoon), TimeZoneInfo.Local)
            .Date.ToString("yyyy-MM-dd");
        result.LocalDate.Should().Be(expectedLocal);
    }

    [Fact]
    public void Derive_NoConfidentDate_LocalDateFromCtime()
    {
        var result = NoFolderDeriver().Derive(@"C:\no-date.md", "", CtimeNoon);

        result.EffectiveDate.Should().BeNull();

        var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UnixEpoch.AddSeconds(CtimeNoon), TimeZoneInfo.Local)
            .Date.ToString("yyyy-MM-dd");
        result.LocalDate.Should().Be(expectedLocal);
    }

    [Fact]
    public void Derive_EmptyContent_FallsThroughToPathDate()
    {
        var folder = @"C:\data\obsidian";
        var deriver = Deriver(folder);

        var result = deriver.Derive(
            @"C:\data\obsidian\2026-05-10.md",
            string.Empty,
            CtimeNoon);

        var expected = (double)new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        result.EffectiveDate.Should().Be(expected);
        result.LocalDate.Should().Be("2026-05-10");
    }

    [Fact]
    public void Derive_MultipleFrontmatterFolders_MatchesEitherFolder_FullPrecision()
    {
        var deriver = new DateDeriver([
            @"C:\data\obsidian\Granola\Transcripts",
            @"C:\data\obsidian",
        ]);

        var content = """
            ---
            created: 2026-03-15T09:00:00Z
            ---
            """;

        var result1 = deriver.Derive(
            @"C:\data\obsidian\Granola\Transcripts\meeting.txt",
            content,
            CtimeNoon);

        var result2 = deriver.Derive(
            @"C:\data\obsidian\2026-03-15.md",
            content,
            CtimeNoon);

        var expectedEpoch = (double)DateTimeOffset.Parse("2026-03-15T09:00:00Z").ToUnixTimeSeconds();
        result1.EffectiveDate.Should().Be(expectedEpoch);
        result2.EffectiveDate.Should().Be(expectedEpoch);
    }

    // ── Ctime fallback — the root-cause scenario ──────────────────────────────

    [Fact]
    public void Derive_CtimeCrossesMidnightUtc_LocalDateIsLocalCalendarDay()
    {
        // File created at 7:39 PM CDT Friday May 8 → UTC is 2026-05-09 00:39.
        // Without local_date, UTC epoch gives "2026-05-09" and the file is invisible
        // to a "May 8" search. LocalDate must be the actual local calendar day.
        var result = NoFolderDeriver().Derive(
            @"C:\notes\engineer-sync.md",
            "Engineer Sync notes",
            CtimeLateNightUtc);

        result.EffectiveDate.Should().BeNull();

        var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UnixEpoch.AddSeconds(CtimeLateNightUtc), TimeZoneInfo.Local)
            .Date.ToString("yyyy-MM-dd");
        result.LocalDate.Should().Be(expectedLocal);

        // The local date must NOT be the UTC date string "2026-05-09" in timezones
        // behind UTC (CDT, PDT, etc.). If running in UTC this assertion is skipped.
        if (TimeZoneInfo.Local.BaseUtcOffset < TimeSpan.Zero)
        {
            result.LocalDate.Should().NotBe("2026-05-09");
        }
    }

    [Fact]
    public void Derive_CtimeWithPathDate_LocalDateFromPath_NotCtime()
    {
        // When path date exists, local_date comes from the path, not ctime.
        // CtimeLateNightUtc crosses midnight so the path date should win cleanly.
        var result = NoFolderDeriver().Derive(
            @"C:\notes\2026-05-08-daily.md",
            "content",
            CtimeLateNightUtc);

        result.EffectiveDate.Should().NotBeNull();
        result.LocalDate.Should().Be("2026-05-08");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DateDeriver Deriver(string folder) => new([folder]);

    private static DateDeriver NoFolderDeriver() => new([]);
}
