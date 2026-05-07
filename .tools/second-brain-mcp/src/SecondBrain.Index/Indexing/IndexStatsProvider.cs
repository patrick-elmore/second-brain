using Microsoft.Data.Sqlite;

namespace SecondBrain.Index.Indexing;

/// <summary>
/// Cheap read-only inspection of the FTS index for the /stats dashboard.
/// Opens a fresh read-only connection per snapshot; safe to call concurrently
/// with writers (WAL mode).
/// </summary>
public sealed class IndexStatsProvider
{
    private readonly string _dbPath;

    public IndexStatsProvider(string dbPath)
    {
        _dbPath = dbPath;
    }

    public IndexStatsSnapshot Snapshot()
    {
        var dbExists = File.Exists(_dbPath);
        long dbBytes = 0;
        DateTimeOffset? dbMTime = null;

        if (dbExists)
        {
            var info = new FileInfo(_dbPath);
            dbBytes = info.Length;
            dbMTime = info.LastWriteTimeUtc;
        }

        if (!dbExists)
            return Empty(dbBytes, dbMTime);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        try
        {
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            int fileCount = 0;
            long totalBytes = 0;
            DateTimeOffset? lastIndexed = null;
            int summarizedCount = 0;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0), MAX(indexed_at) FROM files";
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    fileCount = (int)r.GetInt64(0);
                    totalBytes = r.GetInt64(1);
                    if (!r.IsDBNull(2) && DateTimeOffset.TryParse(r.GetString(2), out var parsed))
                        lastIndexed = parsed;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM files WHERE summary IS NOT NULL";
                summarizedCount = (int)(long)cmd.ExecuteScalar()!;
            }

            var bySource = ReadBuckets(conn,
                "SELECT source_folder_id, COUNT(*) FROM files GROUP BY source_folder_id ORDER BY 2 DESC, 1");

            var byType = ReadBuckets(conn,
                "SELECT COALESCE(source_type, '(none)'), COUNT(*) FROM files GROUP BY source_type ORDER BY 2 DESC, 1");

            return new IndexStatsSnapshot(
                Exists: true,
                FileCount: fileCount,
                TotalIndexedBytes: totalBytes,
                LastIndexedAt: lastIndexed,
                DbFileSizeBytes: dbBytes,
                DbFileMTime: dbMTime,
                BySourceFolder: bySource,
                BySourceType: byType,
                SummarizedCount: summarizedCount);
        }
        catch
        {
            return Empty(dbBytes, dbMTime);
        }
    }

    private static IndexStatsSnapshot Empty(long dbBytes, DateTimeOffset? dbMTime) =>
        new(Exists: false, FileCount: 0, TotalIndexedBytes: 0, LastIndexedAt: null,
            DbFileSizeBytes: dbBytes, DbFileMTime: dbMTime, BySourceFolder: [], BySourceType: [], SummarizedCount: 0);

    private static IReadOnlyList<IndexBucket> ReadBuckets(SqliteConnection conn, string sql)
    {
        var result = new List<IndexBucket>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new IndexBucket(reader.GetString(0), reader.GetInt64(1)));
        return result;
    }
}

public sealed record IndexStatsSnapshot(
    bool Exists,
    int FileCount,
    long TotalIndexedBytes,
    DateTimeOffset? LastIndexedAt,
    long DbFileSizeBytes,
    DateTimeOffset? DbFileMTime,
    IReadOnlyList<IndexBucket> BySourceFolder,
    IReadOnlyList<IndexBucket> BySourceType,
    int SummarizedCount);

public sealed record IndexBucket(string Key, long Count);
