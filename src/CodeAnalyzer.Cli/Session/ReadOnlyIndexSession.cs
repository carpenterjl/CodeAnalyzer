using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Cli.Session;

internal enum IndexOpenStatus
{
    Ok,
    NoCache,
    SchemaMismatch,
    Failed,
}

/// <summary>
/// The outcome of trying to open a workspace's cached index without touching it.
/// <see cref="Problem"/> carries the user-facing sentence for every non-Ok status.
/// </summary>
internal sealed record IndexOpenResult(IndexOpenStatus Status, ReadOnlyIndexSession? Session, string? Problem);

/// <summary>
/// A workspace index opened for reading only. The GUI's <c>WorkspaceSession.Open</c> runs
/// <c>Schema.EnsureCreated</c> — a potential DROP/recreate plus meta writes — which a query
/// command must never do, so this type opens its own connection and validates instead of
/// migrating: a cache this build cannot read is reported, not rebuilt behind the user's back.
/// <para>
/// Verified empirically (M15 spike): a <c>Mode=ReadOnly</c> connection opens a cleanly-closed
/// WAL database, runs beside the GUI's writer, and supports the TEMP tables
/// <see cref="IoBoundaryService"/> builds. <c>PRAGMA query_only</c> is deliberately NOT used —
/// it blocks TEMP table creation, so the ReadWrite fallback below relies on this type simply
/// issuing no writes.
/// </para>
/// </summary>
internal sealed class ReadOnlyIndexSession : IDisposable
{
    private string? _searchLoadedForStamp;

    private ReadOnlyIndexSession(string rootPath, SqliteConnection connection)
    {
        RootPath = rootPath;
        Connection = connection;

        Search = new SymbolSearchService(connection);
        Graph = new GraphQueryService(connection);
        Paths = new PathQueryService(connection);
        IoBoundaries = new IoBoundaryService(connection);
        Values = new ValueQueryService(connection);
        Settings = SqliteIndexStore.ReadSettings(connection);
    }

    public string RootPath { get; }

    public SqliteConnection Connection { get; }

    /// <summary>
    /// One lock in front of the connection, same rule as the GUI: the MCP server can
    /// dispatch tool calls concurrently, and a SQLite connection is not thread-safe.
    /// </summary>
    public DatabaseGate Gate { get; } = new();

    public SymbolSearchService Search { get; }

    public GraphQueryService Graph { get; }

    public PathQueryService Paths { get; }

    public IoBoundaryService IoBoundaries { get; }

    /// <summary>Definitions found by what their literal denotes, across languages.</summary>
    public ValueQueryService Values { get; }

    /// <summary>Crawl settings stored with the index; carries the user's I/O boundary marks.</summary>
    public WorkspaceSettings Settings { get; private set; }

    /// <summary>When the index was last built, ISO-8601, null for a cache that never completed a run.</summary>
    public string? LastIndexUtc { get; private set; }

    /// <summary>Definitions in the index, for the header line.</summary>
    public int DefinitionCount { get; private set; }

    /// <summary>
    /// Files the parser could not fully read. On the header line because every count this
    /// tool prints is drawn from what parsed, and until M28.4 no per-symbol answer said so:
    /// `stats` and `errors` both reported the number, and `get_callers` — the command whose
    /// answer is a count — did not. The JGraph field report is what that costs. A reader saw
    /// 22 callers formatted exactly like a complete answer, had already read "136 imperfect
    /// parses" in another command's output, and had nothing in front of them joining the two.
    /// </summary>
    public int ImperfectParseCount { get; private set; }

    public static IndexOpenResult TryOpen(string rootPath)
    {
        var fullRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var databasePath = WorkspacePaths.GetDatabasePath(fullRoot);
        if (!File.Exists(databasePath))
        {
            return new IndexOpenResult(
                IndexOpenStatus.NoCache,
                null,
                $"no index for {fullRoot} — run: codeanalyzer index \"{fullRoot}\"");
        }

        SqliteConnection connection;
        try
        {
            connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        }
        catch (SqliteException)
        {
            // A WAL database whose -shm needs (re)creating can refuse a read-only open.
            // Fall back to ReadWrite (never Create); this session still issues no writes.
            try
            {
                connection = Open(databasePath, SqliteOpenMode.ReadWrite);
            }
            catch (SqliteException e)
            {
                return new IndexOpenResult(
                    IndexOpenStatus.Failed,
                    null,
                    $"could not open the index at {databasePath}: {e.Message}");
            }
        }

        try
        {
            var storedVersion = Schema.ReadMeta(connection, Schema.MetaSchemaVersion);
            if (storedVersion != Schema.Version.ToString())
            {
                connection.Dispose();
                return new IndexOpenResult(
                    IndexOpenStatus.SchemaMismatch,
                    null,
                    $"the cached index is schema v{storedVersion ?? "?"}, this build reads v{Schema.Version} — "
                    + $"run: codeanalyzer index \"{fullRoot}\"");
            }

            var storedRoot = Schema.ReadMeta(connection, Schema.MetaRootPath);
            if (!string.Equals(storedRoot, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                connection.Dispose();
                return new IndexOpenResult(
                    IndexOpenStatus.Failed,
                    null,
                    $"the cache at {databasePath} says it was built for {storedRoot ?? "(unknown)"}, not {fullRoot}");
            }

            var session = new ReadOnlyIndexSession(fullRoot, connection);
            session.RefreshSnapshot();
            return new IndexOpenResult(IndexOpenStatus.Ok, session, null);
        }
        catch (SqliteException e)
        {
            connection.Dispose();
            return new IndexOpenResult(
                IndexOpenStatus.Failed,
                null,
                $"could not read the index at {databasePath}: {e.Message}");
        }
    }

    /// <summary>
    /// Reloads the in-memory search table when another process has re-indexed since the
    /// last load. Cheap when nothing changed: one meta read under the gate. Long-lived MCP
    /// sessions call this before every search so results follow the GUI's live updates.
    /// </summary>
    public void EnsureSearchCurrent() => Gate.Run(() =>
    {
        var stamp = Schema.ReadMeta(Connection, Schema.MetaLastIndexUtc);
        if (stamp != _searchLoadedForStamp)
        {
            RefreshSnapshotCore();
        }
    });

    private void RefreshSnapshot() => Gate.Run(RefreshSnapshotCore);

    private void RefreshSnapshotCore()
    {
        LastIndexUtc = Schema.ReadMeta(Connection, Schema.MetaLastIndexUtc);
        Settings = SqliteIndexStore.ReadSettings(Connection);
        Search.Reload();
        _searchLoadedForStamp = LastIndexUtc;

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbol WHERE is_definition = 1";
        DefinitionCount = Convert.ToInt32(command.ExecuteScalar() ?? 0);

        using var imperfect = Connection.CreateCommand();
        imperfect.CommandText = "SELECT COUNT(*) FROM file WHERE error_line IS NOT NULL";
        ImperfectParseCount = Convert.ToInt32(imperfect.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// How long one staleness answer is reused. A long-lived MCP session prints provenance
    /// on every tool call, and re-statting every indexed file that often would cost more
    /// than the queries themselves.
    /// </summary>
    private static readonly TimeSpan StalenessCacheFor = TimeSpan.FromSeconds(10);

    private IndexStaleness? _staleness;
    private long _stalenessAt;

    private IndexStaleness? CurrentStaleness()
    {
        var now = Environment.TickCount64;
        if (_staleness is not null && now - _stalenessAt < StalenessCacheFor.TotalMilliseconds)
        {
            return _staleness;
        }

        try
        {
            IndexStaleness? measured = null;
            Gate.Run(() => measured = IndexStalenessProbe.Compare(Connection, RootPath));
            _staleness = measured;
            _stalenessAt = now;
        }
        catch (Exception e) when (e is SqliteException or IOException)
        {
            // Unknown is a state of its own: the line below then says only what it knows.
            return null;
        }

        return _staleness;
    }

    /// <summary>
    /// The one-line provenance every command prints: what index, how big, how old — and how
    /// far it has drifted since, because a build date is a fact the reader has to interpret
    /// while a count of moved files is one they can act on.
    /// </summary>
    public string DescribeIndex()
    {
        var built = "never completed";
        if (LastIndexUtc is not null)
        {
            built = DateTimeOffset.TryParse(LastIndexUtc, out var stamp)
                ? stamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm'Z'")
                : LastIndexUtc;
        }

        // The word "indexed" is load-bearing in every branch: this probe compares the files
        // already in the index and does not go looking for new ones, so no sentence here may
        // claim anything about the workspace as a whole.
        //
        // The advice is attached to drift rather than printed unconditionally (M28.1). It
        // used to follow every header, so the first `stats` after a successful `reindex` read
        // "997 indexed files unchanged on disk — call reindex to refresh": an instruction to
        // repeat the thing just done, in the same breath as the evidence that it worked. A
        // reader who follows it once and sees nothing change learns to skip the whole line,
        // and the line is where drift gets reported.
        var (drift, advice) = CurrentStaleness() switch
        {
            // Nothing measurable: say only what is known rather than implying a clean disk.
            null => (string.Empty, string.Empty),
            { IsStale: false } clean => ($", {clean.Examined:N0} indexed files unchanged on disk", string.Empty),
            var stale => ($", {Describe(stale)}", " — run 'codeanalyzer index' to refresh"),
        };

        // Deliberately a count and a pointer, not a per-symbol claim (M28.4). The specific
        // form the field report asked for — "N of the files referencing this were truncated"
        // — cannot be said honestly: a truncated file's references are not in the index, so
        // nothing knows they would have referenced anything. Approximating it by searching
        // truncated files for the symbol's name was measured and rejected: it would have
        // labelled 452 of this repo's 4,935 definition names, including 266 from a file that
        // lost nothing at all. A warning that wrong that often is the one a reader learns to
        // skip — which is the failure this whole item exists to prevent.
        var imperfect = ImperfectParseCount > 0
            ? $", {ImperfectParseCount:N0} imperfect parses (see: errors)"
            : string.Empty;

        // "definitions", not "symbols": this counts is_definition rows, while an index run
        // reports every symbol it parsed, prototypes and declarations included. Two honest
        // numbers that differ, so they must not share a word.
        return $"index: {RootPath} ({DefinitionCount:N0} definitions{imperfect}, built {built}{drift}{advice})";
    }

    private static string Describe(IndexStaleness stale)
    {
        var more = stale.Complete ? string.Empty : "+";
        var parts = new List<string>(2);

        if (stale.Changed > 0)
        {
            parts.Add($"{stale.Changed:N0}{more} of {stale.Examined:N0} indexed files changed on disk");
        }

        if (stale.Removed > 0)
        {
            parts.Add($"{stale.Removed:N0}{more} gone");
        }

        return string.Join(", ", parts);
    }

    private static SqliteConnection Open(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            // Same reason as the store: closing must actually close, or the GUI cannot
            // delete a cache while a CLI process lingers in the pool.
            Pooling = false,
        }.ToString());

        connection.Open();

        // Read-safe pragmas only. journal_mode/synchronous belong to writers, and
        // query_only is off the table (it blocks the TEMP tables boundaries need).
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = """
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -64000;
            """;
        pragmas.ExecuteNonQuery();

        return connection;
    }

    public void Dispose() => Connection.Dispose();
}
