using System.Text.RegularExpressions;

namespace SecondBrain.Files;

/// <summary>
/// Combined result of date derivation for a single file.
/// </summary>
/// <param name="EffectiveDate">
/// UTC Unix epoch seconds from the best available confident date signal
/// (frontmatter or filepath regex), or <c>null</c> when neither is present.
/// </param>
/// <param name="LocalDate">
/// The local calendar day (YYYY-MM-DD) computed from whichever source won —
/// frontmatter, filepath regex, or filesystem ctime fallback. Always populated.
/// </param>
public sealed record DateResult(double? EffectiveDate, string LocalDate);

/// <summary>
/// Derives a best-effort ISO 8601 date string (date-only, e.g. "2026-05-10")
/// for a given file. Pure function — no I/O side effects.
/// </summary>
public sealed class DateDeriver
{
    // Ordered list of regex patterns that capture a date from a file path.
    // Each must contain named groups: year (4-digit), month (2-digit), day (2-digit).
    private static readonly Regex[] PathDatePatterns =
    [
        // YYYY-MM-DD  (ISO)
        new Regex(
            @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})",
            RegexOptions.Compiled),

        // YYYY_MM_DD
        new Regex(
            @"(?<year>\d{4})_(?<month>\d{2})_(?<day>\d{2})",
            RegexOptions.Compiled),

        // MM-DD-YYYY  (US style)
        new Regex(
            @"(?<month>\d{2})-(?<day>\d{2})-(?<year>\d{4})",
            RegexOptions.Compiled),

        // MM_DD_YYYY
        new Regex(
            @"(?<month>\d{2})_(?<day>\d{2})_(?<year>\d{4})",
            RegexOptions.Compiled),
    ];

    private readonly IReadOnlyList<string> _frontmatterFolders;

    /// <param name="frontmatterFolders">
    /// Absolute paths of folders whose files should have YAML frontmatter
    /// consulted for a <c>created</c> field before falling back to the filepath
    /// regex and filesystem ctime.  On Windows these are compared
    /// case-insensitively.
    /// </param>
    public DateDeriver(IReadOnlyList<string> frontmatterFolders)
    {
        _frontmatterFolders = frontmatterFolders;
    }

    /// <summary>
    /// Derives date information for the file using the best available signal.
    /// </summary>
    /// <param name="absolutePath">Absolute path to the file.</param>
    /// <param name="content">Full text content of the file (may be empty).</param>
    /// <param name="ctime">
    /// Filesystem creation time as UTC Unix epoch seconds. Used as the final
    /// fallback for <see cref="DateResult.LocalDate"/> when neither frontmatter
    /// nor filepath regex produces a date.
    /// </param>
    /// <returns>
    /// A <see cref="DateResult"/> where <c>EffectiveDate</c> is the UTC epoch from
    /// frontmatter or filepath regex (null when neither is present) and
    /// <c>LocalDate</c> is always the local calendar day (YYYY-MM-DD) derived from
    /// whichever source won, falling back to the ctime in local timezone.
    /// </returns>
    public DateResult Derive(string absolutePath, string content, double ctime)
    {
        // Step 1 — frontmatter path (only for configured folders)
        if (IsInFrontmatterFolder(absolutePath))
        {
            var fromFm = TryExtractFrontmatterDateResult(content);
            if (fromFm != null)
                return fromFm;
        }

        // Step 2 — filepath regex
        var fromPath = TryExtractPathDateResult(absolutePath);
        if (fromPath != null)
            return fromPath;

        // Step 3 — ctime fallback: no confident epoch, but local_date from ctime
        var ctimeLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UnixEpoch.AddSeconds(ctime),
            TimeZoneInfo.Local);
        return new DateResult(null, ctimeLocal.Date.ToString("yyyy-MM-dd"));
    }

    private bool IsInFrontmatterFolder(string absolutePath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var folder in _frontmatterFolders)
        {
            // Normalize: strip trailing separator so prefix check is consistent.
            var normalized = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (absolutePath.StartsWith(normalized + Path.DirectorySeparatorChar, comparison) ||
                absolutePath.StartsWith(normalized + Path.AltDirectorySeparatorChar, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static DateResult? TryExtractFrontmatterDateResult(string content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        var fm = new FrontmatterParser().Parse(content);
        if (!fm.Metadata.HasValue)
            return null;

        if (!fm.Metadata.Value.TryGetProperty("created", out var createdEl))
            return null;

        var raw = createdEl.GetString();
        if (string.IsNullOrEmpty(raw))
            return null;

        // Full timestamp precision — callers get the exact moment, not just midnight.
        if (DateTimeOffset.TryParse(raw, out var dto))
        {
            var epoch = (double)dto.ToUnixTimeSeconds();
            var local = TimeZoneInfo.ConvertTime(dto, TimeZoneInfo.Local);
            return new DateResult(epoch, local.Date.ToString("yyyy-MM-dd"));
        }

        return null;
    }

    private static DateResult? TryExtractPathDateResult(string absolutePath)
    {
        // Use the full path so folder names (e.g. "2026-05-10/notes.md") are candidates.
        foreach (var pattern in PathDatePatterns)
        {
            foreach (Match m in pattern.Matches(absolutePath))
            {
                if (!m.Success)
                    continue;

                if (!int.TryParse(m.Groups["year"].Value, out var year) ||
                    !int.TryParse(m.Groups["month"].Value, out var month) ||
                    !int.TryParse(m.Groups["day"].Value, out var day))
                    continue;

                if (!IsValidDate(year, month, day))
                    continue;

                // Date-only source — filepath components are calendar dates, not timestamps.
                // effective_date stored at midnight UTC for consistency.
                var epoch = (double)new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
                var localDate = $"{year:D4}-{month:D2}-{day:D2}";
                return new DateResult(epoch, localDate);
            }
        }

        return null;
    }

    private static bool IsValidDate(int year, int month, int day)
    {
        if (year < 1900 || year > 2200) return false;
        if (month < 1 || month > 12) return false;
        if (day < 1 || day > 31) return false;

        // Use DateOnly to validate the actual calendar date (e.g. rejects 2026-13-45)
        return DateOnly.TryParseExact(
            $"{year:D4}-{month:D2}-{day:D2}",
            "yyyy-MM-dd",
            out _);
    }

}
