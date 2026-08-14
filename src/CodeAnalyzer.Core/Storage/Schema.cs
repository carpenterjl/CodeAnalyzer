using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// Index database schema and migrations.
/// </summary>
public static class Schema
{
    /// <summary>
    /// Bump when the DDL below changes, or when the meaning of a stored value does.
    /// A mismatch drops and rebuilds the index, which is safe because the database is a
    /// cache derived entirely from source files.
    /// <para>
    /// Version 6 has the same DDL as 5. It exists because <c>DependencyProbe</c> learned to
    /// collapse <c>..</c> segments, so a <c>dep_probe</c> written by an earlier build says
    /// something different from one written now — and incremental indexing would never
    /// revisit the unchanged file that holds it.
    /// </para>
    /// <para>
    /// Version 8 (M8) adds <c>file.error</c>, so the error pane can say why rather than
    /// just that, and denormalises <c>edge</c> with <c>src_file_id</c>, <c>dst_file_id</c>
    /// and <c>to_own_member</c> — all three are already known when the resolver inserts the
    /// row, and carrying them saves the treemap and the wheel two index seeks per edge on
    /// queries that touch every edge in the workspace.
    /// </para>
    /// <para>
    /// Version 9 (M9) adds <c>symbol.modifiers</c> — the declaration's verbatim modifier
    /// keywords in source order — and <c>ref.arg_text</c>, the verbatim argument list at a
    /// call site, capped at parse time so the largest table stays bounded.
    /// </para>
    /// <para>
    /// Version 10 (M12) adds <c>symbol.param_text</c>, the declaration's verbatim parameter
    /// list. The graph draws it on the node so two overloads of one method can be told
    /// apart, and storing the slice is what keeps that a fact: <c>signature</c> already
    /// contains the parameters, but finding them inside it means parsing source text.
    /// </para>
    /// </summary>
    public const int Version = 10;

    public const string MetaSchemaVersion = "schema_version";
    public const string MetaRootPath = "root_path";
    public const string MetaLastIndexUtc = "last_index_utc";

    /// <summary>
    /// Applied to every connection. WAL lets readers run while the writer commits, which
    /// is what keeps search responsive during an index.
    /// </summary>
    public const string ConnectionPragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA temp_store = MEMORY;
        PRAGMA cache_size = -64000;
        """;

    private const string CreateTables = """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE IF NOT EXISTS selected_dir (
            rel_path TEXT PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS file (
            id           INTEGER PRIMARY KEY,
            rel_path     TEXT NOT NULL UNIQUE,
            -- First path segment, precomputed. Reference resolution compares this for
            -- every candidate pair, and doing the substring work per row there is
            -- prohibitively slow on a large workspace.
            top_dir      TEXT NOT NULL DEFAULT '',
            -- Final path segment, precomputed so include resolution can seek by filename
            -- instead of running a trailing-wildcard LIKE against every file.
            base_name    TEXT NOT NULL DEFAULT '',
            -- Everything before the final segment, empty at the workspace root. An include
            -- is looked up relative to the including file's own directory first, and this
            -- is what turns that into an exact seek on rel_path.
            dir_path     TEXT NOT NULL DEFAULT '',
            language     TEXT NOT NULL,
            content_hash BLOB NOT NULL,
            size         INTEGER NOT NULL,
            mtime        INTEGER NOT NULL,
            status       INTEGER NOT NULL DEFAULT 0,
            -- What went wrong, when status says something did. NULL for a routine syntax
            -- error (tree-sitter recovers and reports no message); set for hard failures.
            error        TEXT
        );

        CREATE TABLE IF NOT EXISTS symbol (
            id            INTEGER PRIMARY KEY,
            file_id       INTEGER NOT NULL REFERENCES file(id) ON DELETE CASCADE,
            name          TEXT NOT NULL,
            kind          INTEGER NOT NULL,
            container_id  INTEGER REFERENCES symbol(id) ON DELETE SET NULL,
            scope_path    TEXT NOT NULL DEFAULT '',
            signature     TEXT,
            value         TEXT,
            type_text     TEXT,
            -- Verbatim modifier keywords in source order ("public sealed override").
            -- NULL where the language pack captures none; a fact, never an inference.
            modifiers     TEXT,
            language      TEXT NOT NULL,
            start_line    INTEGER NOT NULL,
            start_col     INTEGER NOT NULL,
            end_line      INTEGER NOT NULL,
            end_col       INTEGER NOT NULL,
            start_offset  INTEGER NOT NULL,
            end_offset    INTEGER NOT NULL,
            is_definition INTEGER NOT NULL DEFAULT 1,
            -- Number of named parameter nodes, and the parameter list exactly as written.
            -- The count drives the resolver's arity filter; the text is what the graph
            -- node and the overload list display. Both NULL for a declaration that has no
            -- parameter list at all, which is how a non-callable is told from a callable
            -- that happens to take nothing.
            param_count   INTEGER,
            param_text    TEXT
        );

        CREATE TABLE IF NOT EXISTS ref (
            id              INTEGER PRIMARY KEY,
            file_id         INTEGER NOT NULL REFERENCES file(id) ON DELETE CASCADE,
            from_symbol_id  INTEGER REFERENCES symbol(id) ON DELETE CASCADE,
            name            TEXT NOT NULL,
            kind            INTEGER NOT NULL,
            arg_count       INTEGER,
            -- Verbatim argument list at a call site, truncated with a trailing ellipsis
            -- past 200 characters. NULL for references that carry no argument list.
            arg_text        TEXT,
            line            INTEGER NOT NULL,
            col             INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS edge (
            ref_id           INTEGER NOT NULL REFERENCES ref(id) ON DELETE CASCADE,
            target_symbol_id INTEGER NOT NULL REFERENCES symbol(id) ON DELETE CASCADE,
            confidence       INTEGER NOT NULL,
            -- Denormalised from ref.file_id and symbol.file_id at insert time. The treemap
            -- and the wheel aggregate every edge in the workspace, and reading these here
            -- instead of seeking through ref and symbol per row is what keeps them flat.
            src_file_id      INTEGER NOT NULL DEFAULT 0,
            dst_file_id      INTEGER NOT NULL DEFAULT 0,
            -- 1 when the target is contained by the edge's own source symbol (a method
            -- reaching its own local, a class touching its own field). Computed with the
            -- same expression the graph queries use: target.container_id IS from_symbol_id.
            -- Display rule only — the reference stays in the index and on the member's card.
            to_own_member    INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (ref_id, target_symbol_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS file_dep (
            file_id      INTEGER NOT NULL REFERENCES file(id) ON DELETE CASCADE,
            -- The include or import exactly as it is written in the source: the fact.
            dep_path     TEXT NOT NULL,
            -- The workspace path to go looking for, which is not always the same text:
            -- a Python `import pkg.mod` is searched for as pkg/mod.py. Empty when the
            -- dependency cannot name a file at all, such as a C# namespace.
            dep_probe    TEXT NOT NULL DEFAULT '',
            -- Final segment of dep_probe, matched against file.base_name.
            dep_base     TEXT NOT NULL DEFAULT '',
            dep_file_id  INTEGER REFERENCES file(id) ON DELETE SET NULL,
            PRIMARY KEY (file_id, dep_path)
        ) WITHOUT ROWID;
        """;

    private const string CreateIndexes = """
        CREATE INDEX IF NOT EXISTS ix_symbol_name      ON symbol(name);
        CREATE INDEX IF NOT EXISTS ix_symbol_file      ON symbol(file_id);
        CREATE INDEX IF NOT EXISTS ix_symbol_container ON symbol(container_id);
        CREATE INDEX IF NOT EXISTS ix_symbol_lookup    ON symbol(name, is_definition, kind);
        -- Same-file resolution seeks by (file, name); this is the selective path that
        -- keeps common member names from exploding the candidate join.
        CREATE INDEX IF NOT EXISTS ix_symbol_file_name ON symbol(file_id, name, is_definition);

        CREATE INDEX IF NOT EXISTS ix_ref_name         ON ref(name);
        CREATE INDEX IF NOT EXISTS ix_ref_from         ON ref(from_symbol_id);
        CREATE INDEX IF NOT EXISTS ix_ref_file         ON ref(file_id);

        CREATE INDEX IF NOT EXISTS ix_edge_target      ON edge(target_symbol_id);
        -- Lets a deep treemap level drive from its own small file set instead of scanning
        -- every edge in the workspace.
        CREATE INDEX IF NOT EXISTS ix_edge_src_file    ON edge(src_file_id);

        CREATE INDEX IF NOT EXISTS ix_file_dep_target  ON file_dep(dep_file_id);
        CREATE INDEX IF NOT EXISTS ix_file_base_name   ON file(base_name);
        """;

    /// <summary>
    /// Creates the schema if absent, or wipes and recreates it when the stored version
    /// does not match. Returns true when a rebuild is needed (i.e. the index is empty).
    /// </summary>
    public static bool EnsureCreated(SqliteConnection connection)
    {
        ExecuteScript(connection, ConnectionPragmas);

        var existingVersion = ReadSchemaVersion(connection);

        if (existingVersion is not null && existingVersion != Version)
        {
            DropAll(connection);
            existingVersion = null;
        }

        ExecuteScript(connection, CreateTables);
        ExecuteScript(connection, CreateIndexes);

        if (existingVersion is null)
        {
            WriteMeta(connection, MetaSchemaVersion, Version.ToString());
            return true;
        }

        return false;
    }

    private static int? ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value FROM meta WHERE key = $key
            """;
        command.Parameters.AddWithValue("$key", MetaSchemaVersion);

        try
        {
            var value = command.ExecuteScalar() as string;
            return int.TryParse(value, out var parsed) ? parsed : null;
        }
        catch (SqliteException)
        {
            // meta does not exist yet: a fresh database.
            return null;
        }
    }

    private static void DropAll(SqliteConnection connection) => ExecuteScript(connection, """
        DROP TABLE IF EXISTS edge;
        DROP TABLE IF EXISTS ref;
        DROP TABLE IF EXISTS file_dep;
        DROP TABLE IF EXISTS symbol;
        DROP TABLE IF EXISTS file;
        DROP TABLE IF EXISTS selected_dir;
        DROP TABLE IF EXISTS meta;
        """);

    public static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public static void ExecuteScript(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
