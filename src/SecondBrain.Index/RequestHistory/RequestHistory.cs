using Microsoft.Data.Sqlite;
using SecondBrain.Index.Indexing;

namespace SecondBrain.Index.RequestHistory;

public sealed class RequestHistory
{
    private readonly string _dbPath;

    public RequestHistory(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    public void PersistRequest(RequestRecord record, IReadOnlyList<RequestFile> files)
    {
        using var conn = OpenWrite();
        using var txn = conn.BeginTransaction();
        try
        {
            using var insertReq = conn.CreateCommand();
            insertReq.Transaction = txn;
            insertReq.CommandText = """
                INSERT INTO requests (id, timestamp, tool, query, filters_json, result_count, synthesis)
                VALUES (@id, @ts, @tool, @query, @filters, @count, @synthesis)
                """;
            insertReq.Parameters.AddWithValue("@id", record.Id);
            insertReq.Parameters.AddWithValue("@ts", record.Timestamp.ToString("o"));
            insertReq.Parameters.AddWithValue("@tool", record.Tool);
            insertReq.Parameters.AddWithValue("@query", (object?)record.Query ?? DBNull.Value);
            insertReq.Parameters.AddWithValue("@filters", (object?)record.FiltersJson ?? DBNull.Value);
            insertReq.Parameters.AddWithValue("@count", record.ResultCount);
            insertReq.Parameters.AddWithValue("@synthesis", (object?)record.Synthesis ?? DBNull.Value);
            insertReq.ExecuteNonQuery();

            using var insertFile = conn.CreateCommand();
            insertFile.Transaction = txn;
            insertFile.CommandText = """
                INSERT INTO request_files (request_id, rank, absolute_path, relative_path, source_folder_id, score)
                VALUES (@rid, @rank, @abspath, @relpath, @sfid, @score)
                """;
            insertFile.Parameters.Add("@rid", SqliteType.Text);
            insertFile.Parameters.Add("@rank", SqliteType.Integer);
            insertFile.Parameters.Add("@abspath", SqliteType.Text);
            insertFile.Parameters.Add("@relpath", SqliteType.Text);
            insertFile.Parameters.Add("@sfid", SqliteType.Text);
            insertFile.Parameters.Add("@score", SqliteType.Real);

            foreach (var f in files)
            {
                insertFile.Parameters["@rid"].Value = record.Id;
                insertFile.Parameters["@rank"].Value = f.Rank;
                insertFile.Parameters["@abspath"].Value = f.AbsolutePath;
                insertFile.Parameters["@relpath"].Value = f.RelativePath;
                insertFile.Parameters["@sfid"].Value = f.SourceFolderId;
                insertFile.Parameters["@score"].Value = f.Score.HasValue ? (object)f.Score.Value : DBNull.Value;
                insertFile.ExecuteNonQuery();
            }

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    public RequestEntity? Get(string requestId, IReadOnlyList<string>? fields)
    {
        using var conn = OpenRead();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, tool, query, filters_json, result_count, synthesis FROM requests WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", requestId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var id = reader.GetString(0);
        var timestamp = reader.IsDBNull(1) ? null : reader.GetString(1);
        var tool = reader.IsDBNull(2) ? null : reader.GetString(2);
        var query = reader.IsDBNull(3) ? null : reader.GetString(3);
        var filtersJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        var resultCount = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        var synthesis = reader.IsDBNull(6) ? null : reader.GetString(6);
        reader.Close();

        IReadOnlyList<RequestFileEntity>? fileEntities = null;
        if (ShouldInclude(fields, "files"))
        {
            fileEntities = GetFilesForRequest(conn, requestId);
        }

        // Apply field projection
        return new RequestEntity(
            RequestId: id,
            Timestamp: ShouldInclude(fields, "timestamp") ? timestamp : null,
            Tool: ShouldInclude(fields, "tool") ? tool : null,
            Query: ShouldInclude(fields, "query") ? query : null,
            FiltersJson: ShouldInclude(fields, "filters") ? filtersJson : null,
            ResultCount: ShouldInclude(fields, "result_count") ? resultCount : null,
            Synthesis: ShouldInclude(fields, "synthesis") ? synthesis : null,
            Files: fileEntities);
    }

    private static IReadOnlyList<RequestFileEntity> GetFilesForRequest(SqliteConnection conn, string requestId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rank, absolute_path, relative_path, source_folder_id, score
            FROM request_files
            WHERE request_id = @id
            ORDER BY rank
            """;
        cmd.Parameters.AddWithValue("@id", requestId);

        using var reader = cmd.ExecuteReader();
        var files = new List<RequestFileEntity>();
        while (reader.Read())
        {
            files.Add(new RequestFileEntity(
                Rank: reader.GetInt32(0),
                AbsolutePath: reader.GetString(1),
                RelativePath: reader.GetString(2),
                SourceFolderId: reader.GetString(3),
                Score: reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }
        return files;
    }

    private static bool ShouldInclude(IReadOnlyList<string>? fields, string field)
    {
        if (fields == null || fields.Count == 0)
            return true; // no projection = return all fields
        return fields.Any(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureSchema()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var conn = OpenWrite();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        new SchemaManager().EnsureRequestsSchema(conn);
    }

    private SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();
        return conn;
    }

    private SqliteConnection OpenWrite()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        conn.Open();
        return conn;
    }
}
