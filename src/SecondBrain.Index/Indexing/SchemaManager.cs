using Microsoft.Data.Sqlite;

namespace SecondBrain.Index.Indexing;

public sealed class SchemaManager
{
    // Idempotent: creates the FTS schema only if it doesn't already exist.
    // Used by the incremental updater to guarantee tables are present without dropping data.
    // Also migrates pre-existing databases that lack the effective_date / file_created_at /
    // file_modified_at columns (added in the 2026-05-10 schema update).
    public void EnsureFtsSchema(SqliteConnection conn)
    {
        // Phase 1 — create tables and base indexes (none reference the new date columns).
        // If the table already exists with the old schema, CREATE TABLE IF NOT EXISTS
        // is a no-op; we migrate the columns in phase 2.
        using (var cmd = conn.CreateCommand())
        {
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
                    metadata          TEXT,
                    summary           TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_files_source   ON files(source_folder_id);
                CREATE INDEX IF NOT EXISTS idx_files_mtime    ON files(mtime);
                CREATE INDEX IF NOT EXISTS idx_files_type     ON files(source_type);
                CREATE INDEX IF NOT EXISTS idx_files_summary  ON files(summary);

                CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
                    path,
                    content,
                    summary,
                    tokenize='porter unicode61'
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // Phase 2 — add date columns if they're missing.
        // ALTER TABLE ADD COLUMN fails with "duplicate column name" when the column
        // already exists; we swallow that specific error to stay idempotent.
        AddColumnIfMissing(conn, "files", "effective_date",   "REAL");
        AddColumnIfMissing(conn, "files", "file_created_at",  "REAL");
        AddColumnIfMissing(conn, "files", "file_modified_at", "REAL");
        AddColumnIfMissing(conn, "files", "local_date",       "TEXT");

        // Phase 3 — create indexes (safe to run now that the columns exist).
        using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_files_effective_date ON files(effective_date);
                CREATE INDEX IF NOT EXISTS idx_files_local_date     ON files(local_date);
                """;
            idxCmd.ExecuteNonQuery();
        }
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string typeDef)
    {
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDef}";
            alter.ExecuteNonQuery();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Column already exists — nothing to do.
        }
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
                metadata          TEXT,
                summary           TEXT,
                effective_date    REAL,
                file_created_at   REAL,
                file_modified_at  REAL,
                local_date        TEXT
            );

            CREATE INDEX idx_files_source         ON files(source_folder_id);
            CREATE INDEX idx_files_mtime          ON files(mtime);
            CREATE INDEX idx_files_type           ON files(source_type);
            CREATE INDEX idx_files_summary        ON files(summary);
            CREATE INDEX idx_files_effective_date ON files(effective_date);
            CREATE INDEX idx_files_local_date     ON files(local_date);

            CREATE VIRTUAL TABLE files_fts USING fts5(
                path,
                content,
                summary,
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
