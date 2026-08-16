using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Graph;
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

    /// <summary>Crawl settings stored with the index; carries the user's I/O boundary marks.</summary>
    public WorkspaceSettings Settings { get; private set; }

    /// <summary>When the index was last built, ISO-8601, null for a cache that never completed a run.</summary>
    public string? LastIndexUtc { get; private set; }

    /// <summary>Definitions in the index, for the header line.</summary>
    public int DefinitionCount { get; private set; }

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
    }

    /// <summary>The one-line provenance every command prints: what index, how big, how old.</summary>
    public string DescribeIndex()
    {
        var built = "never completed";
        if (LastIndexUtc is not null)
        {
            built = DateTimeOffset.TryParse(LastIndexUtc, out var stamp)
                ? stamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm'Z'")
                : LastIndexUtc;
        }

        return $"index: {RootPath} ({DefinitionCount:N0} symbols, built {built} — "
            + "run 'codeanalyzer index' to refresh)";
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
