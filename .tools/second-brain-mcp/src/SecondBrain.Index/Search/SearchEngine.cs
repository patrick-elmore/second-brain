using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SecondBrain.Index.Search;

public sealed class SearchEngine
{
    private readonly string _dbPath;

    public SearchEngine(string dbPath)
    {
        _dbPath = dbPath;
    }

    public SearchResult Search(SearchParams p)
    {
        if (!File.Exists(_dbPath))
            return new SearchResult([], null);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        var snipTokens = Math.Clamp(p.SnippetTokens, 1, 64);
        var filter = BuildFilter(p);
        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = p.Query != null
                ? RunFtsSearch(conn, p, filter, snipTokens)
                : RunFilterOnlySearch(conn, p, filter);
        }
        catch (SqliteException)
        {
            // FTS5 syntax errors come from queries the LLM constructs that contain
            // unquoted multi-word phrases, special chars, or terms that look like
            // column references (e.g. "vin AND ...", "cache-first", ".NET").
            // Return empty hits rather than throwing — the model can retry with
            // different syntax or fall back to broader queries.
            return new SearchResult([], null);
        }

        IReadOnlyList<SourceSummary>? sources = null;
        if (p.ListSources && hits.Count > 0)
        {
            sources = hits
                .GroupBy(h => h.SourceFolderId)
                .Select(g => new SourceSummary(g.Key, g.Count()))
                .OrderByDescending(s => s.HitCount)
                .ToList();
        }

        return new SearchResult(hits, sources);
    }

    /// <summary>
    /// Runs multiple FTS5 query variants and fuses the per-variant rankings via
    /// Reciprocal Rank Fusion. Filters in <paramref name="baseParams"/> apply to
    /// every variant. Empty/whitespace queries are ignored. Returns RRF scores
    /// (higher = more relevant, positive) rather than BM25 scores.
    /// </summary>
    public SearchResult SearchMulti(IReadOnlyList<string> queries, SearchParams baseParams)
    {
        if (!File.Exists(_dbPath))
            return new SearchResult([], null);

        var cleaned = queries.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
        if (cleaned.Count == 0)
            return Search(baseParams); // fall through to filter-only path

        // Overfetch per variant so the fuser has enough material to find consensus.
        var perVariantTop = Math.Clamp(baseParams.Top * 2, 30, 50);

        var perVariantLists = new List<IReadOnlyList<SearchHit>>(cleaned.Count);
        foreach (var q in cleaned)
        {
            var p = baseParams with { Query = q, Top = perVariantTop };
            perVariantLists.Add(Search(p).Hits);
        }

        var fused = RrfFuser.Fuse(perVariantLists, baseParams.Top);

        IReadOnlyList<SourceSummary>? sources = null;
        if (baseParams.ListSources && fused.Count > 0)
        {
            sources = fused
                .GroupBy(h => h.SourceFolderId)
                .Select(g => new SourceSummary(g.Key, g.Count()))
                .OrderByDescending(s => s.HitCount)
                .ToList();
        }

        return new SearchResult(fused, sources);
    }

    private static FilterBuilder BuildFilter(SearchParams p)
    {
        var fb = new FilterBuilder();

        if (p.DateStart.HasValue) fb.AddDateStart(p.DateStart.Value);
        if (p.DateEnd.HasValue) fb.AddDateEnd(p.DateEnd.Value);
        if (p.People is { Count: > 0 }) fb.AddPeople(p.People);
        if (p.SourceType is { Count: > 0 }) fb.AddSourceTypes(p.SourceType);
        if (p.SourceFolders is { Count: > 0 }) fb.AddSourceFolders(p.SourceFolders);

        return fb;
    }

    private static IReadOnlyList<SearchHit> RunFtsSearch(
        SqliteConnection conn,
        SearchParams p,
        FilterBuilder filter,
        int snipTokens)
    {
        var whereExtra = filter.BuildWhereClause();
        var returnSnippets = p.ReturnMode != "paths";
        var snippetExpr = returnSnippets
            ? $"snippet(files_fts, 1, '<<', '>>', '...', {snipTokens})"
            : "''";

        var sql = $"""
            SELECT f.source_folder_id,
                   f.absolute_path,
                   f.relative_path,
                   bm25(files_fts, 10.0, 1.0, 5.0) AS score,
                   {snippetExpr} AS snip,
                   f.metadata
            FROM files_fts
            JOIN files f ON f.id = files_fts.rowid
            WHERE files_fts MATCH @query
            {whereExtra}
            ORDER BY score
            LIMIT @top
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@query", p.Query!);
        cmd.Parameters.AddWithValue("@top", p.Top);
        filter.ApplyParameters(cmd);

        return ReadHits(cmd, returnSnippets);
    }

    private static IReadOnlyList<SearchHit> RunFilterOnlySearch(
        SqliteConnection conn,
        SearchParams p,
        FilterBuilder filter)
    {
        if (!filter.HasFilters)
            return [];

        var whereExtra = filter.BuildWhereClause();
        // Strip the leading "AND " for the WHERE clause when there's no FTS
        var whereClause = whereExtra.StartsWith("AND ")
            ? "WHERE " + whereExtra[4..]
            : string.Empty;

        var sql = $"""
            SELECT f.source_folder_id,
                   f.absolute_path,
                   f.relative_path,
                   0.0 AS score,
                   '' AS snip,
                   f.metadata
            FROM files f
            {whereClause}
            ORDER BY f.mtime DESC
            LIMIT @top
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@top", p.Top);
        filter.ApplyParameters(cmd);

        return ReadHits(cmd, returnSnippets: false);
    }

    private static IReadOnlyList<SearchHit> ReadHits(SqliteCommand cmd, bool returnSnippets)
    {
        using var reader = cmd.ExecuteReader();
        var results = new List<SearchHit>();

        while (reader.Read())
        {
            var sourceFolderId = reader.GetString(0);
            var absolutePath = reader.GetString(1);
            var relativePath = reader.GetString(2);
            var score = reader.GetDouble(3);
            var snippet = reader.IsDBNull(4) ? null : reader.GetString(4);
            var metadataJson = reader.IsDBNull(5) ? null : reader.GetString(5);

            JsonElement? metadata = null;
            if (metadataJson != null)
            {
                try
                {
                    var doc = JsonDocument.Parse(metadataJson);
                    metadata = doc.RootElement.Clone();
                }
                catch (JsonException) { }
            }

            var matches = (returnSnippets && !string.IsNullOrEmpty(snippet))
                ? new List<SnippetMatch> { new(snippet) }
                : (IReadOnlyList<SnippetMatch>)[];

            results.Add(new SearchHit(sourceFolderId, absolutePath, relativePath, score, metadata, matches));
        }

        return results;
    }
}
