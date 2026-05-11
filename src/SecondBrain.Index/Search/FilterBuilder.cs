using Microsoft.Data.Sqlite;

namespace SecondBrain.Index.Search;

internal sealed class FilterBuilder
{
    private readonly List<string> _clauses = new();
    private readonly List<(string name, object? value, SqliteType type)> _params = new();
    private int _paramIndex;

    public bool HasFilters => _clauses.Count > 0;

    public void AddDateStart(DateOnly date)
    {
        var p = NextParam(SqliteType.Text);
        // local_date is always populated at index time (YYYY-MM-DD in server local timezone).
        // Simple string comparison is correct and avoids epoch arithmetic across timezone boundaries.
        _clauses.Add($"f.local_date >= {p}");
        _params.Add((p, date.ToString("yyyy-MM-dd"), SqliteType.Text));
    }

    public void AddDateEnd(DateOnly date)
    {
        var p = NextParam(SqliteType.Text);
        _clauses.Add($"f.local_date <= {p}");
        _params.Add((p, date.ToString("yyyy-MM-dd"), SqliteType.Text));
    }

    public void AddPeople(IReadOnlyList<string> people)
    {
        // Match if any of the people appear in the attendees array or string
        var parts = new List<string>();
        foreach (var person in people)
        {
            var p = NextParam(SqliteType.Text);
            // Handle both array and scalar attendees in metadata
            parts.Add($"""
                (
                  (json_type(json_extract(f.metadata, '$.attendees')) = 'array'
                   AND EXISTS (SELECT 1 FROM json_each(json_extract(f.metadata, '$.attendees'))
                               WHERE LOWER(value) LIKE LOWER({p})))
                  OR
                  (json_type(json_extract(f.metadata, '$.attendees')) != 'array'
                   AND LOWER(json_extract(f.metadata, '$.attendees')) LIKE LOWER({p}))
                )
                """);
            _params.Add((p, $"%{person}%", SqliteType.Text));
        }
        _clauses.Add($"({string.Join(" OR ", parts)})");
    }

    public void AddSourceTypes(IReadOnlyList<string> types)
    {
        var placeholders = types.Select(_ =>
        {
            var p = NextParam(SqliteType.Text);
            return p;
        }).ToList();

        for (var i = 0; i < types.Count; i++)
            _params.Add((placeholders[i], types[i], SqliteType.Text));

        _clauses.Add($"f.source_type IN ({string.Join(", ", placeholders)})");
    }

    public void AddSourceFolders(IReadOnlyList<string> folders)
    {
        var placeholders = folders.Select(_ =>
        {
            var p = NextParam(SqliteType.Text);
            return p;
        }).ToList();

        for (var i = 0; i < folders.Count; i++)
            _params.Add((placeholders[i], folders[i], SqliteType.Text));

        _clauses.Add($"f.source_folder_id IN ({string.Join(", ", placeholders)})");
    }

    public string BuildWhereClause() =>
        _clauses.Count > 0 ? "AND " + string.Join(" AND ", _clauses) : string.Empty;

    public void ApplyParameters(SqliteCommand cmd)
    {
        foreach (var (name, value, type) in _params)
        {
            var param = cmd.Parameters.Add(name, type);
            param.Value = value ?? DBNull.Value;
        }
    }

    private string NextParam(SqliteType _) => $"@f{_paramIndex++}";
}
