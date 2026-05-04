using Microsoft.Data.Sqlite;

namespace SecondBrain.Index.Indexing;

public sealed class SchemaManager
{
    // Idempotent: creates the FTS schema only if it doesn't already exist.
    // Used by the incremental updater to guarantee tables are present without dropping data.
    public void EnsureFtsSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS files (
                id                INTEGER PRIMARY KEY,
                source_folder_id  TEXT NOT NULL,
                absolute_path     TEXT NOT NULL UNIQUE,
                relative_path     TEXT NOT NULL,
                size_bytes        INTEGER NOT NULL,
                mtime             REAL NOT NULL,
                indexed_at        TEXT NOT NULL,
                source_type       TEXT,
                metadata          TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_files_source ON files(source_folder_id);
            CREATE INDEX IF NOT EXISTS idx_files_mtime  ON files(mtime);
            CREATE INDEX IF NOT EXISTS idx_files_type   ON files(source_type);

            CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
                path,
                content,
                tokenize='porter unicode61'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // Creates fts.db from scratch (always drops and recreates — full rebuild).
    public void CreateFtsSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS files_fts;
            DROP TABLE IF EXISTS files;

            CREATE TABLE files (
                id                INTEGER PRIMARY KEY,
                source_folder_id  TEXT NOT NULL,
                absolute_path     TEXT NOT NULL UNIQUE,
                relative_path     TEXT NOT NULL,
                size_bytes        INTEGER NOT NULL,
                mtime             REAL NOT NULL,
                indexed_at        TEXT NOT NULL,
                source_type       TEXT,
                metadata          TEXT
            );

            CREATE INDEX idx_files_source ON files(source_folder_id);
            CREATE INDEX idx_files_mtime  ON files(mtime);
            CREATE INDEX idx_files_type   ON files(source_type);

            CREATE VIRTUAL TABLE files_fts USING fts5(
                path,
                content,
                tokenize='porter unicode61'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // Creates requests.db tables if they don't exist. Idempotent — never drops.
    public void EnsureRequestsSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS requests (
                id            TEXT PRIMARY KEY,
                timestamp     TEXT NOT NULL,
                tool          TEXT NOT NULL,
                query         TEXT,
                filters_json  TEXT,
                result_count  INTEGER NOT NULL,
                synthesis     TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_requests_timestamp ON requests(timestamp);

            CREATE TABLE IF NOT EXISTS request_files (
                request_id        TEXT NOT NULL REFERENCES requests(id) ON DELETE CASCADE,
                rank              INTEGER NOT NULL,
                absolute_path     TEXT NOT NULL,
                relative_path     TEXT NOT NULL,
                source_folder_id  TEXT NOT NULL,
                score             REAL,
                PRIMARY KEY (request_id, rank)
            );

            CREATE INDEX IF NOT EXISTS idx_request_files_path ON request_files(absolute_path);
            """;
        cmd.ExecuteNonQuery();
    }
}
