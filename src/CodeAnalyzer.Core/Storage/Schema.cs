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
    /// <para>
    /// Version 11 (M16) adds <c>symbol.value_num</c> and <c>symbol.value_str</c>: what the
    /// stored verbatim <c>value</c> denotes, where the literal's own grammar settles it.
    /// They are stamped at write time rather than derived per query because SQL cannot read
    /// <c>0xA5</c> or <c>8'hA5</c>, and the detail pane's lookup runs on every selection —
    /// it has to be an index seek. Both are NULL wherever the parser cannot be certain.
    /// </para>
    /// <para>
    /// Version 22 (M21.1) adds <c>file.error_line</c> and <c>file.error_text</c>: where the
    /// parser first lost its footing and the text it could not read. Needed by the rule
    /// below — the parser now writes a value it never wrote before, and an unchanged file
    /// the gate skips would keep a NULL pair that reads as "this file parsed clean". The
    /// columns exist because five rounds of reporting reported a <em>count</em> of imperfect
    /// files and never once said which or why, which turned out to be hiding a duller answer
    /// than the number implied.
    /// </para>
    /// <para>
    /// Version 21 (M20.5) changes no DDL, and — by the rule below — is one that genuinely
    /// needs a number: the XAML pack now emits a handler reference for an attribute whose
    /// name reads like a routed event, so an unchanged <c>.xaml</c> file holds fewer
    /// reference rows than this build would write for it, and no resolve can invent a row
    /// the parser never stored.
    /// </para>
    /// <para>
    /// <b>What actually needs a version, established after 17, 19 and 20 were spent finding
    /// out.</b> A <em>parser or pack</em> change needs one: the incremental gate screens on
    /// size, timestamp and hash, so a file it judges unchanged keeps the symbol and reference
    /// rows an older analyzer wrote, and only the version can say those rows are stale. A
    /// <em>resolver</em> change does not, and this is the distinction the three versions
    /// below were bumped without: <see cref="Resolution.ReferenceResolver.ResolveAll"/> opens
    /// with <c>DELETE FROM edge</c> and rebuilds every edge from the reference rows, and
    /// every indexing entry point except the file watcher calls it unconditionally. A
    /// resolver change therefore lands in full on the next <c>index</c> run, on unchanged
    /// files included, and needs no help from the version. The rule is not "when the meaning
    /// of a stored value changes" but "when the meaning of a <em>parsed</em> row changes" —
    /// version 18 is the one below that qualifies. The three that did not are kept rather
    /// than reclaimed: they cost a rebuild each and nothing else, and a version number that
    /// once shipped is not worth reusing.
    /// </para>
    /// <para>
    /// Version 23 (M22.3) changes no DDL. The C#, C and C++ packs now capture typed
    /// parameters as <c>SymbolKind.Parameter</c> rows, so a file the gate judges unchanged
    /// is missing symbols the analyzer now produces — the same shape as version 18, from
    /// the parsed side rather than the stored-meaning side. The resolver half of the
    /// milestone (Parameter back in the referencable and receiver-typable sets) would have
    /// needed no version on its own.
    /// </para>
    /// <para>
    /// Versions 19 and 20 (M20.3) change no DDL, and — per the paragraph above — need not
    /// have existed. The resolver now reads a reference's receiver against the declared type
    /// of the field or property that names it, and prefers a candidate whose container is
    /// that type over one that is merely nearby. 20 followed 19 on the theory that a
    /// database 19 wrote would keep the ambiguity, by analogy with 15 and 16; the analogy
    /// was wrong, and the measurement that showed it was the incremental run after the
    /// <c>var x = new T()</c> inference landing on exactly the same 53,418 links as the full
    /// rebuild that followed it.
    /// </para>
    /// <para>
    /// Version 18 (M20.2) changes no DDL. A XAML <c>x:Key</c> is now its own symbol kind
    /// rather than a markup element, so rows an earlier build stored for an unchanged
    /// <c>.xaml</c> file record a resource key under the kind an element name uses — and a
    /// resource lookup would keep resolving to whichever it found first. A pack change that
    /// alters what a stored row means needs one of these; the gate screens on size,
    /// timestamp and hash and cannot see a <c>.scm</c> edit.
    /// </para>
    /// <para>
    /// Version 17 (M20.1) changes no DDL, and need not have existed either. A bare-identifier
    /// use may now bind to a member of a type — a class's constant, an enum's member — where
    /// before the rule that keeps loop counters out of the graph excluded every one of them
    /// along with the locals it was aimed at. This is a resolver change, so the next
    /// <c>index</c> run would have applied it to every file regardless; the version was
    /// bumped on the belief that edges, being derived, went stale the way parsed rows do.
    /// </para>
    /// <para>
    /// Versions 15 and 16 (M19.3) change no DDL. The XAML pack now reads markup
    /// extensions — <c>{Binding SearchQuery}</c> becomes a binding reference named by its
    /// first path segment, <c>{StaticResource PanelBrush}</c> a resource reference named
    /// by its key — so once again rows stored for an unchanged <c>.xaml</c> file mean
    /// something different than before. Two numbers for one milestone for the same reason
    /// version 6 exists: 15 was built and indexed with before the resource kind split
    /// landed, and a database it wrote records a resource lookup as a binding — the
    /// incremental gate would never revisit the unchanged files to correct it.
    /// </para>
    /// <para>
    /// Version 14 (M19.2) changes no DDL. The XAML pack now declares a compiled file's
    /// root element under its verbatim <c>x:Class</c> value, owns the <c>x:Class</c>
    /// reference with it, and the analyzer splits dotted type references at their last
    /// segment — so rows stored for an unchanged <c>.xaml</c> file by an earlier build
    /// say something different from what this build would store. Same reason as
    /// version 12: only a rebuild makes the index agree with the analyzer now running.
    /// </para>
    /// <para>
    /// Version 13 (M19) adds <c>ref.receiver_text</c>: the verbatim receiver expression at
    /// a member call or member access — the <c>orchestrator</c> in
    /// <c>orchestrator.IndexAsync(…)</c> — NULL where the reference is a bare name or the
    /// pack captures no receiver. Stored because its absence was producing false edges
    /// marked exact: the same-file resolution tier's justification — a call in this file
    /// probably means the definition in this file — is sound for <c>foo()</c> and unsound
    /// for <c>obj.foo()</c>, and without the receiver on the row the resolver could not
    /// tell the two apart.
    /// </para>
    /// <para>
    /// Version 12 changes no DDL at all. The C# pack now harvests the <c>record</c> keyword
    /// into <c>symbol.modifiers</c>, which changes what a stored row for an unchanged file
    /// should say — and the incremental gate screens on size, timestamp and content hash, so
    /// it would never revisit that file to find out. Same reason as the version 6 bump: when
    /// the meaning of a stored value changes, only a rebuild makes the index agree with the
    /// analyzer that is now running. A query-pack change that alters existing rows needs one
    /// of these; a pack for a language that was not indexed before does not, because those
    /// files are newly discovered rather than stale.
    /// </para>
    /// </summary>
    public const int Version = 23;

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
            error        TEXT,
            -- Where the parser first lost its footing, and the text it could not read.
            -- Independent of `error` above, and the common case is that this pair is set
            -- while that is NULL: the file recovered, indexed fully, and still holds a
            -- construct the bundled grammar predates. NULL text with a line set is a token
            -- the grammar expected and did not find, which has a position and no extent.
            error_line   INTEGER,
            error_text   TEXT
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
            -- What the verbatim `value` above denotes, where the literal's own grammar
            -- settles it: 0xA5 and 8'hA5 both store 165, "COM3" stores COM3. NULL when the
            -- slice is an expression, a float, a character, or anything else the parser
            -- cannot certify — the verbatim text stays the displayed fact either way.
            value_num     INTEGER,
            value_str     TEXT,
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
            -- Verbatim receiver expression at a member call or member access, truncated
            -- like arg_text. NULL where the reference is a bare name or the language pack
            -- captures no receiver — absence of a capture is not an assertion of bareness.
            receiver_text   TEXT,
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

        -- Partial: only a small minority of symbols carry a certifiable literal, and the
        -- value queries never ask about the rest. Keeps both the index and the write cost
        -- proportional to the constants rather than to the workspace.
        CREATE INDEX IF NOT EXISTS ix_symbol_value_num ON symbol(value_num) WHERE value_num IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_symbol_value_str ON symbol(value_str) WHERE value_str IS NOT NULL;
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
