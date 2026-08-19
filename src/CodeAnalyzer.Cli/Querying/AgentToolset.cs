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
/// What a search found, kept in two lists because they answer two different questions.
/// </summary>
/// <param name="Hits">Symbols matched by name — the question that was asked.</param>
/// <param name="CommentRescue">
/// Symbols matched by the words of the query appearing in the comment above them. Non-empty
/// only when <paramref name="Hits"/> held nothing worth showing, so a caller may print it
/// without checking anything: an empty list is the tool saying it tried.
/// </param>
internal sealed record SymbolSearchAnswer(
    List<SymbolSearchHit> Hits,
    List<SymbolSearchHit> CommentRescue,
    IReadOnlyList<SymbolKind> KindsThisIndexHasNoneOf,
    int LiteralSites);

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
    public SymbolSearchAnswer Search(
        string query,
        IReadOnlySet<SymbolKind>? kinds,
        int limit,
        bool exact = false,
        bool inComments = false,
        bool withComments = false,
        CancellationToken cancellationToken = default) =>
        Query(() =>
        {
            Session.EnsureSearchCurrent();
            var options = new SymbolSearchOptions
            {
                Limit = limit,
                Kinds = kinds,
                // Searching comments is a mode rather than a widening, so it wins over
                // --exact instead of combining with it: exact says how to match a name,
                // and here there is no name being matched.
                Match = inComments
                    ? SymbolMatchMode.DocComment
                    : exact ? SymbolMatchMode.Substring : SymbolMatchMode.Fuzzy,
                IncludeDocComments = withComments,
            };

            var hits = Session.Search.Search(query, options, cancellationToken);

            // The rescue is asked only when the name search has nothing to show — no hits
            // at all, or nothing but letters-appear-in-order ones. Asking it always would
            // put prose matches under every successful query; asking it here costs one
            // indexed LIKE on the path where the answer was going to be "nothing".
            // Already-a-comment-search is excluded: it IS the second question.
            var rescue = inComments || !hits.TrueForAll(hit => hit.LooseMatch)
                ? []
                : Session.Search.SearchDocCommentWords(query, options with { Limit = 5 }, cancellationToken);

            // Asked only when the filter is what emptied the result. A kind token the
            // index holds nothing of is not a failed search, it is an impossible one, and
            // the two are indistinguishable from the outside.
            IReadOnlyList<SymbolKind> emptyKinds = [];
            if (hits.Count == 0 && kinds is { Count: > 0 })
            {
                var present = Session.Search.DefinitionsByKind();
                emptyKinds = [.. kinds.Where(kind => !present.ContainsKey(kind))];
            }

            // The third question, asked on the same failing path and for the same reason
            // the second one is. A name index and a comment index both answer about
            // declarations; a literal is neither. An OpenSim Studio session went to grep for
            // the field names a solver emits — `new NodalScalarField("Displacement", …)` —
            // and got a wrong first answer, because nothing in a search result says that
            // constructor arguments are indexed at all. They are, and this is the place a
            // reader is standing when it would have helped.
            var literalSites = 0;
            if (!inComments && hits.Count == 0 && emptyKinds.Count == 0)
            {
                var literals = Session.Values.FindByValue(query, limit: 1, cancellationToken: cancellationToken);
                literalSites = (literals?.Matches.Count ?? 0) + (literals?.ArgumentSites.Count ?? 0);
            }

            return new SymbolSearchAnswer(hits, rescue, emptyKinds, literalSites);
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
    public IndexStats Stats(string? pathScope = null) =>
        Query(() => IndexStatsQuery.Read(Session.Connection, pathScope));

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
