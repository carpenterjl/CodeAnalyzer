using CodeAnalyzer.Core.Analysis;
using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using CodeAnalyzer.Core.Watching;

namespace CodeAnalyzer.Core.Workspaces;

public sealed record IndexRunResult(IndexOutcome Outcome, int EdgesCreated, int FilesRemoved, TimeSpan Elapsed);

/// <summary>
/// What a live update did. <paramref name="FullResolve"/> says whether the fast path was
/// usable, which is the difference between a few milliseconds and a whole-workspace resolve.
/// </summary>
public sealed record LiveUpdateResult(
    int FilesParsed,
    int FilesRemoved,
    int EdgesCreated,
    bool FullResolve,
    TimeSpan Elapsed)
{
    public bool ChangedAnything => FilesParsed > 0 || FilesRemoved > 0;
}

/// <summary>
/// One open workspace: its database, index pipeline, and the query services that read it.
/// <para>
/// The UI holds a session rather than the individual services, so opening or closing a
/// workspace is a single lifetime to manage.
/// </para>
/// </summary>
public sealed class WorkspaceSession : IDisposable
{
    private readonly SqliteIndexStore _store;
    private readonly IAnalyzerFactory _analyzerFactory;

    private WorkspaceSession(string rootPath, SqliteIndexStore store, IAnalyzerFactory analyzerFactory)
    {
        RootPath = rootPath;
        _store = store;
        _analyzerFactory = analyzerFactory;
        Settings = store.LoadSettings();

        Search = new SymbolSearchService(store.Connection);
        Graph = new GraphQueryService(store.Connection);
        Composition = new CompositionQueryService(store.Connection);
        Paths = new PathQueryService(store.Connection);
        Structure = new StructureQueryService(store.Connection);
    }

    public string RootPath { get; }

    public SymbolSearchService Search { get; }

    public GraphQueryService Graph { get; }

    /// <summary>What a symbol is made of: members, instantiations, type relations.</summary>
    public CompositionQueryService Composition { get; }

    /// <summary>Routes between two symbols through resolved references.</summary>
    public PathQueryService Paths { get; }

    /// <summary>Workspace shape: the treemap level reader and the dependency wheel.</summary>
    public StructureQueryService Structure { get; }

    /// <summary>
    /// Runs a query against the index under the connection lock.
    /// <para>
    /// The query services are exposed directly for readability at the call site, but there is
    /// one connection behind all of them and it is not thread-safe. Every caller that reads
    /// from a background thread must come through here — otherwise a live update landing
    /// mid-query corrupts the reader.
    /// </para>
    /// </summary>
    public T Read<T>(Func<T> query) => _store.Gate.Read(query);

    /// <summary>Opens a workspace, creating or reusing its cached index.</summary>
    public static WorkspaceSession Open(string rootPath, IAnalyzerFactory analyzerFactory)
    {
        var store = SqliteIndexStore.OpenForWorkspace(rootPath);
        var session = new WorkspaceSession(rootPath, store, analyzerFactory);

        // A cached index is immediately searchable, so reopening a workspace does not
        // require re-indexing before the user can do anything.
        store.Gate.Run(() => session.Search.Reload());
        return session;
    }

    public IReadOnlyList<string> LoadSelectedDirectories() => _store.LoadSelectedDirectories();

    /// <summary>
    /// The workspace's crawl settings: extra ignore rules and the size cap. Loaded with the
    /// workspace; changing them takes effect on the next index run, which the caller owns —
    /// files newly excluded leave the index the same way deleted files do.
    /// </summary>
    public WorkspaceSettings Settings { get; private set; }

    public void SaveSettings(WorkspaceSettings settings)
    {
        Settings = settings;
        _store.SaveSettings(settings);
    }

    /// <summary>
    /// Files whose last parse was imperfect: syntax errors with partial symbols indexed,
    /// or hard failures that produced nothing.
    /// </summary>
    public IReadOnlyList<Storage.FileErrorRecord> ReadFileErrors() => _store.ReadFileErrors();

    /// <summary>Creates a watcher over this workspace, filtered the same way the crawler is.</summary>
    public WorkspaceWatcher CreateWatcher() => new(RootPath, _analyzerFactory.IsSupportedExtension, Settings);

    /// <summary>
    /// Indexes the selected directories, resolves references, and refreshes search.
    /// All work runs on a background thread; the caller stays responsive.
    /// </summary>
    public async Task<IndexRunResult> IndexAsync(
        IReadOnlyList<string> selectedDirectories,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        var selection = new WorkspaceSelection(
            RootPath,
            selectedDirectories.Count == 0 ? [string.Empty] : selectedDirectories);

        _store.BeginRun();
        _store.SaveSelectedDirectories(selectedDirectories);

        var crawler = new FileCrawler(_analyzerFactory.IsSupportedExtension, Settings);
        var orchestrator = new IndexOrchestrator(crawler, _analyzerFactory);

        var outcome = await orchestrator.IndexAsync(
            selection,
            _store,
            incrementalGate: _store,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (outcome.WasCancelled)
        {
            // Leave the index as-is: partial results are still valid facts, and a
            // half-finished resolve would be misleading.
            return new IndexRunResult(outcome, 0, 0, DateTimeOffset.UtcNow - startedAt);
        }

        return await Task.Run(
            () =>
            {
                progress?.Report(new IndexProgress
                {
                    Phase = IndexPhase.Resolving,
                    FilesDiscovered = outcome.FilesDiscovered,
                    FilesParsed = outcome.FilesParsed,
                    FilesUnchanged = outcome.FilesUnchanged,
                    SymbolsFound = outcome.SymbolsFound,
                });

                // The selection defines what the index is meant to hold, so anything the
                // crawl did not reach is dropped — unchecking a directory has to take its
                // symbols and links with it, not just stop refreshing them.
                var removed = _store.RemoveFilesNotSeenThisRun();

                var edges = _store.Gate.Read(() =>
                {
                    var created = new ReferenceResolver(_store.Connection).ResolveAll(cancellationToken);
                    Search.Reload(cancellationToken);
                    return created;
                });

                progress?.Report(new IndexProgress
                {
                    Phase = IndexPhase.Complete,
                    FilesDiscovered = outcome.FilesDiscovered,
                    FilesParsed = outcome.FilesParsed,
                    FilesUnchanged = outcome.FilesUnchanged,
                    FilesFailed = outcome.FilesFailed,
                    FilesWithSyntaxErrors = outcome.FilesWithSyntaxErrors,
                    SymbolsFound = outcome.SymbolsFound,
                });

                return new IndexRunResult(outcome, edges, removed, DateTimeOffset.UtcNow - startedAt);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Folds a batch of filesystem changes into the index.
    /// <para>
    /// Only the named paths are crawled, and only the references those paths could have moved
    /// are re-resolved — see <see cref="ReferenceResolver.ResolveIncremental"/>. The one thing
    /// that forces a full resolve is a change to the include graph: reach is transitive, so a
    /// new <c>#include</c> can promote a reference two hops away that this batch never
    /// mentions. That is detected rather than assumed, by comparing the dirty files'
    /// dependency edges before and after.
    /// </para>
    /// </summary>
    public async Task<LiveUpdateResult> ApplyChangesAsync(
        WorkspaceChangeBatch batch,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        // Everything the batch could touch, as it stands in the index right now. Removed
        // directories expand to the files under them.
        var affectedPaths = _store.FindIndexedPathsAtOrUnder(
            batch.RemovedPaths.Concat(batch.ChangedFiles).Concat(batch.ChangedDirectories));

        var beforeIds = affectedPaths
            .Select(_store.FindFileId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        // Names as they were, so a caller in an untouched file is re-resolved when the
        // definition it pointed at disappears.
        var dirtyNames = _store.ReadDefinedNames(beforeIds);
        var dependenciesBefore = _store.ReadDependencyEdges(beforeIds);

        var removed = _store.RemoveFilesAtOrUnder(batch.RemovedPaths);

        _store.BeginRun();

        var crawler = new TargetedCrawler(
            new FileCrawler(_analyzerFactory.IsSupportedExtension, Settings),
            batch.ChangedFiles);

        var selection = new WorkspaceSelection(RootPath, batch.ChangedDirectories);
        var orchestrator = new IndexOrchestrator(crawler, _analyzerFactory);

        var outcome = await orchestrator.IndexAsync(
            selection,
            _store,
            incrementalGate: _store,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (outcome.WasCancelled)
        {
            return new LiveUpdateResult(0, removed, 0, false, DateTimeOffset.UtcNow - startedAt);
        }

        return await Task.Run(
            () =>
            {
                var writtenIds = _store.TakeWrittenPaths()
                    .Select(_store.FindFileId)
                    .Where(id => id is not null)
                    .Select(id => id!.Value)
                    .ToList();

                // Editors touch files without changing them, and the incremental gate catches
                // that before anything is parsed. With nothing written and nothing removed
                // there is no reference anywhere that could have moved.
                if (writtenIds.Count == 0 && removed == 0)
                {
                    return new LiveUpdateResult(0, 0, 0, false, DateTimeOffset.UtcNow - startedAt);
                }

                var dirtyIds = beforeIds.Union(writtenIds).ToList();

                foreach (var name in _store.ReadDefinedNames(writtenIds))
                {
                    dirtyNames.Add(name);
                }

                // A file appearing or vanishing can re-point a dependency in a file this
                // batch never mentions, and so shift the include reach of a third one.
                var structural = removed > 0
                    || writtenIds.Count > beforeIds.Intersect(writtenIds).Count();

                var edges = _store.Gate.Read(() =>
                {
                    var resolver = new ReferenceResolver(_store.Connection);

                    // Re-parsing wiped these files' dependency rows, so they have to be
                    // re-pointed before the include graph can be compared with itself.
                    resolver.ResolveDependenciesFor(dirtyIds);

                    var full = structural
                        || !dependenciesBefore.SetEquals(_store.ReadDependencyEdges(dirtyIds));

                    var created = full
                        ? resolver.ResolveAll(cancellationToken)
                        : resolver.ResolveIncremental(dirtyIds, dirtyNames, cancellationToken);

                    Search.Reload(cancellationToken);
                    return (Created: created, Full: full);
                });

                return new LiveUpdateResult(
                    outcome.FilesParsed,
                    removed,
                    edges.Created,
                    edges.Full,
                    DateTimeOffset.UtcNow - startedAt);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a file's text for the preview pane.</summary>
    public string? TryReadSource(string relativePath)
    {
        var fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _store.Dispose();
}
