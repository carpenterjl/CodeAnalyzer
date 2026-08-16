using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Export;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Cli.Querying;

/// <summary>
/// The index has become unreadable mid-session — most likely another process is rebuilding
/// it. Commands map this to <see cref="ExitCodes.IndexBusy"/>; nothing retries silently.
/// </summary>
internal sealed class IndexUnavailableException(string message) : Exception(message);

/// <summary>
/// The one façade both entry points call: every CLI subcommand and every MCP tool is a
/// thin wrapper over a method here, so the two can never answer the same question
/// differently.
/// <para>
/// Every database touch goes through the session's gate — the MCP server dispatches tool
/// calls concurrently, and there is one SQLite connection behind all of this.
/// </para>
/// </summary>
internal sealed class AgentToolset(ReadOnlyIndexSession session)
{
    public ReadOnlyIndexSession Session { get; } = session;

    public LocateResult Locate(string symbolText, CancellationToken cancellationToken = default) =>
        Query(() =>
        {
            Session.EnsureSearchCurrent();
            return SymbolLocator.Locate(Session, symbolText, cancellationToken);
        });

    /// <summary>
    /// Fuzzy by default. <paramref name="exact"/> switches to verbatim containment, for
    /// the case where fuzzy's subsequence reach is the problem rather than the feature —
    /// a short common word matches letter-by-letter across half the test names in a repo.
    /// </summary>
    public List<SymbolSearchHit> Search(
        string query,
        IReadOnlySet<SymbolKind>? kinds,
        int limit,
        bool exact = false,
        CancellationToken cancellationToken = default) =>
        Query(() =>
        {
            Session.EnsureSearchCurrent();
            return Session.Search.Search(
                query,
                new SymbolSearchOptions
                {
                    Limit = limit,
                    Kinds = kinds,
                    Match = exact ? SymbolMatchMode.Substring : SymbolMatchMode.Fuzzy,
                },
                cancellationToken);
        });

    public SymbolDetail? GetDetail(long symbolId, CancellationToken cancellationToken = default) =>
        Query(() => Session.Graph.GetDetail(symbolId, cancellationToken));

    public List<EdgeCallSite> GetCallSites(
        long sourceId,
        long targetId,
        ReferenceKind kind,
        CancellationToken cancellationToken = default) =>
        Query(() => Session.Graph.GetEdgeCallSites(sourceId, targetId, kind, cancellationToken));

    public PathTrace Trace(
        long fromId,
        long toId,
        int? maxDepth,
        CancellationToken cancellationToken = default) =>
        Query(() => Session.Paths.FindPaths(fromId, toId, maxDepth, cancellationToken));

    /// <summary>
    /// Every I/O boundary site in the workspace: built-in catalog matches plus the user's
    /// own marks, exactly as the GUI's Boundaries view computes them — the marks stored
    /// with the index are honoured here too.
    /// </summary>
    public List<IoBoundarySite> Boundaries(CancellationToken cancellationToken = default) =>
        Query(() => Session.IoBoundaries.GetAllSites(
            IoCatalog.BuiltIn.Entries, Session.Settings.IoMarks, cancellationToken));

    /// <summary>
    /// Definitions whose literal denotes <paramref name="literal"/>, in any language and
    /// any notation. Null when the text is not a literal this build can read — a value
    /// search that quietly fell back to a name search would answer a different question.
    /// </summary>
    public ValueMatchSet? FindByValue(
        string literal,
        int limit,
        CancellationToken cancellationToken = default) =>
        Query(() => Session.Values.FindByValue(literal, limit, null, cancellationToken));

    /// <summary>
    /// Definitions elsewhere carrying this symbol's value. Null when it has no literal the
    /// parser could certify, or when nothing else carries it.
    /// </summary>
    public ValueMatchSet? SameValue(long symbolId, CancellationToken cancellationToken = default) =>
        Query(() => Session.Values.GetSameValue(symbolId, cancellationToken));

    /// <summary>
    /// Values carried by definitions in more than one language — or, when
    /// <paramref name="acrossDirectories"/> is set, in more than one top-level directory.
    /// </summary>
    public IReadOnlyList<ValueGroup> SharedValues(
        bool acrossDirectories,
        bool includeTrivial,
        CancellationToken cancellationToken = default) =>
        Query(() => Session.Values.GetSharedValues(acrossDirectories, includeTrivial, cancellationToken));

    /// <summary>
    /// Everything the index can say about one symbol, assembled for the markdown fact
    /// report — the same Core builder the GUI's "copy facts" uses, so the two documents
    /// cannot drift. Null when the symbol has gone from the index.
    /// </summary>
    public SymbolContextReport? Report(long symbolId, CancellationToken cancellationToken = default) =>
        Query(() =>
        {
            var ioSites = Session.IoBoundaries.GetSitesForCallers(
                [symbolId], IoCatalog.BuiltIn.Entries, Session.Settings.IoMarks, cancellationToken);
            return SymbolContextReportBuilder.Build(
                Session.Graph,
                Session.Values,
                ioSites,
                Session.RootPath,
                symbolId,
                provenance: $"index built {Session.LastIndexUtc ?? "unknown"}",
                cancellationToken);
        });

    /// <summary>The ranked codebase overview an agent primes its context with.</summary>
    public RepoMap Map(CancellationToken cancellationToken = default) =>
        Query(() => RepoMapService.Build(Session, cancellationToken));

    /// <summary>One file's definitions in source order, or null when no file matches.</summary>
    public FileOutline? Outline(string pathText) =>
        Query(() => FileOutlineService.Build(Session, pathText));

    /// <summary>
    /// Files the parser could not fully read, with the position it stopped at. Every other
    /// answer this toolset gives is drawn from files that parsed; this is the one that says
    /// which ones did not, so a caller can judge how much of the workspace the rest covers.
    /// </summary>
    public ParseErrorReport ParseErrors() =>
        Query(() => FileErrorQuery.Read(Session.Connection));

    /// <summary>
    /// Aggregate facts about the index itself — the answer to "how well is this thing
    /// resolving", which every per-symbol query implies and none states.
    /// </summary>
    public IndexStats Stats() =>
        Query(() => IndexStatsQuery.Read(Session.Connection));

    /// <summary>Runs a read under the gate, translating a torn-down index into one message.</summary>
    public T Query<T>(Func<T> query)
    {
        try
        {
            return Session.Gate.Read(query);
        }
        catch (SqliteException e)
        {
            // The session validated the schema at open, so a failure now means the file
            // changed underneath us — a rebuild in another process is the ordinary cause.
            throw new IndexUnavailableException(
                $"the index could not be read (is another process rebuilding it?): {e.Message}");
        }
    }
}
