using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CodeAnalyzer.App.Services;
using CodeAnalyzer.Core.Analysis;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Watching;
using CodeAnalyzer.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// Shell view model. Owns the workspace session and the panes; every indexing, search and
/// query call runs on a background thread so the UI thread only ever applies results.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Debounce before a keystroke turns into a search, in milliseconds.</summary>
    private const int SearchDebounceMs = 150;

    private readonly IDialogService _dialogService;
    private readonly IThemeService _themeService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAnalyzerFactory _analyzerFactory;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>
    /// Held by anything that writes to the index. An index run and a live update both mutate
    /// the same database, and the second one to arrive has to wait rather than interleave.
    /// </summary>
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    private WorkspaceSession? _session;
    private WorkspaceWatcher? _watcher;
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _searchCancellation;

    /// <summary>Workspace-relative path the treemap is currently showing. Empty is the root.</summary>
    private string _treemapPath = string.Empty;

    public MainViewModel(
        IDialogService dialogService,
        IThemeService themeService,
        IUiDispatcher dispatcher,
        IAnalyzerFactory analyzerFactory,
        ILogger<MainViewModel> logger,
        WorkspaceTreeViewModel workspaceTree,
        SearchViewModel search,
        GraphViewModel graph,
        SymbolDetailViewModel detail,
        CodePreviewViewModel preview,
        StatusBarViewModel status)
    {
        _dialogService = dialogService;
        _themeService = themeService;
        _dispatcher = dispatcher;
        _analyzerFactory = analyzerFactory;
        _logger = logger;

        WorkspaceTree = workspaceTree;
        Search = search;
        Graph = graph;
        Detail = detail;
        Preview = preview;
        Status = status;

        Status.CancelRequested += (_, _) => CancelIndexing();
        WorkspaceTree.SelectionApplied += OnSelectionApplied;
        Search.ResultActivated += OnSearchResultActivated;
        Search.PropertyChanged += OnSearchPropertyChanged;
        Search.KindFilterChanged += (_, _) => _ = RunSearchAsync(Search.Query);

        // Clicking a node shows its facts; double-clicking re-roots the graph there. The
        // two are separated so exploring the detail pane never reshuffles the canvas.
        Graph.SymbolSelected += (_, id) => _ = ShowSymbolFactsAsync(id);
        Graph.SymbolActivated += (_, id) => _ = FocusSymbolAsync(id);
        Graph.ExpandRequested += (_, request) => _ = ExpandAsync(request);
        Graph.EdgeSelected += (_, selection) => _ = AnswerEdgeSelectionAsync(selection);
        Graph.EdgeActivated += (_, activation) => _ = OpenEdgeCallSiteAsync(activation);

        Graph.RendererReady += (_, _) => _ = RepaintRendererAsync();
        Graph.ViewModeChanged += (_, _) =>
        {
            // Export only means anything over the neighbourhood graph, so its buttons
            // follow the visible view.
            ExportPngCommand.NotifyCanExecuteChanged();
            ExportJsonCommand.NotifyCanExecuteChanged();
            _ = RefreshCurrentViewAsync();
        };
        Graph.ExportProduced += (_, result) => _ = SaveExportAsync(result);
        Graph.DrillRequested += (_, path) => _ = DrillTreemapAsync(path);
        Graph.PathEndpointsChanged += (_, _) => _ = TracePathsAsync();
        Graph.WheelSourceChanged += (_, _) => _ = LoadWheelAsync();

        // Saved as it changes rather than only on close: a reading preference the user had
        // to hunt for once should not be lost to a crash.
        Graph.LegendFontSizeChanged += (_, _) => SaveSession();
        Graph.ShowNodeDetailsChanged += (_, _) => SaveSession();
    }

    public WorkspaceTreeViewModel WorkspaceTree { get; }

    public SearchViewModel Search { get; }

    public GraphViewModel Graph { get; }

    public SymbolDetailViewModel Detail { get; }

    public CodePreviewViewModel Preview { get; }

    public StatusBarViewModel Status { get; }

    [ObservableProperty]
    private string _title = "CodeAnalyzer";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReindexCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportPngCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportJsonCommand))]
    private string? _workspaceRoot;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    // ---- Workspace lifecycle ---------------------------------------------

    [RelayCommand]
    private async Task OpenWorkspaceAsync()
    {
        var folder = _dialogService.PickFolder("Select a workspace folder", WorkspaceRoot);
        if (folder is null)
        {
            return;
        }

        await OpenWorkspaceCoreAsync(folder).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens a workspace by path. Shared by the Open button and by session restore, so a
    /// restored workspace is in exactly the state a freshly opened one would be.
    /// </summary>
    private async Task<bool> OpenWorkspaceCoreAsync(string folder)
    {
        try
        {
            Status.Message = "Opening workspace…";

            StopWatching();
            _session?.Dispose();
            _session = await Task.Run(() => WorkspaceSession.Open(folder, _analyzerFactory)).ConfigureAwait(true);

            WorkspaceRoot = folder;
            Title = $"CodeAnalyzer — {folder}";

            Detail.Clear();
            Preview.Clear();
            Search.Results.Clear();
            await Graph.ClearAsync("Search for a symbol to see its dependency graph.").ConfigureAwait(true);

            // Every view holds facts about the workspace that just closed. Reset them all
            // rather than leaving the previous project's picture on screen.
            _treemapPath = string.Empty;
            Graph.ClearPathEndpointsCommand.Execute(null);
            await Graph.ShowCompositionAsync(null).ConfigureAwait(true);
            await RefreshCurrentViewAsync().ConfigureAwait(true);
            SetPathStartCommand.NotifyCanExecuteChanged();
            SetPathEndCommand.NotifyCanExecuteChanged();

            await WorkspaceTree.LoadWorkspaceAsync(folder, _session.Settings).ConfigureAwait(true);

            var selection = _session.LoadSelectedDirectories();
            WorkspaceTree.RestoreSelection(selection);

            await RefreshErrorsAsync().ConfigureAwait(true);

            var indexed = _session.Search.IndexedSymbolCount;
            if (indexed > 0)
            {
                // A cached index is searchable straight away.
                Search.EmptyMessage = "Type to search symbols.";
                Status.Message = $"Loaded cached index: {indexed:N0} symbols. Re-index to pick up changes.";
            }
            else
            {
                Search.EmptyMessage = "Select directories and choose Apply selection to index them.";
                Status.Message = "Select directories to index, then choose Apply selection.";
            }

            StartWatching(selection);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open workspace {Folder}", folder);
            _dialogService.ShowError("Open workspace", $"Could not open '{folder}':\n\n{ex.Message}");
            Status.Message = "Failed to open workspace.";
            return false;
        }
    }

    private bool CanReindex() => _session is not null && !Status.IsIndexing;

    [RelayCommand(CanExecute = nameof(CanReindex))]
    private Task ReindexAsync() => RunIndexAsync(WorkspaceTree.CollectSelectedDirectories());

    private void OnSelectionApplied(object? sender, IReadOnlyList<string> selectedDirectories) =>
        _ = RunIndexAsync(selectedDirectories);

    private async Task RunIndexAsync(IReadOnlyList<string> selectedDirectories)
    {
        if (_session is null || Status.IsIndexing)
        {
            return;
        }

        _indexCancellation?.Dispose();
        _indexCancellation = new CancellationTokenSource();

        Status.IsIndexing = true;
        Status.ErrorCount = 0;
        Status.Message = "Indexing…";
        ReindexCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();

        // Progress arrives on the UI thread because Progress<T> captures this context.
        var progress = new Progress<IndexProgress>(ApplyProgress);
        var stopwatch = Stopwatch.StartNew();

        await _mutationGate.WaitAsync().ConfigureAwait(true);

        try
        {
            var result = await _session
                .IndexAsync(selectedDirectories, progress, _indexCancellation.Token)
                .ConfigureAwait(true);

            stopwatch.Stop();

            Status.Message = result.Outcome.WasCancelled
                ? $"Indexing cancelled after {result.Outcome.FilesParsed:N0} files."
                : Describe(result, stopwatch.Elapsed);

            // Same reason the live-update path refreshes: a re-index replaces symbol rows and
            // can drop whole directories, so results and panes left over from before it are
            // pointing at ids that no longer exist. A stale hit for a file the user just
            // unchecked reads as the unchecking having failed.
            await RefreshAfterUpdateAsync().ConfigureAwait(true);

            // The selection is what bounds the watcher, and applying a selection is one of
            // the two ways to get here.
            StartWatching(selectedDirectories);

            await RefreshErrorsAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status.Message = "Indexing cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing failed");
            _dialogService.ShowError("Indexing", ex.Message);
            Status.Message = "Indexing failed.";
        }
        finally
        {
            _mutationGate.Release();
            Status.IsIndexing = false;
            Status.ProgressPercent = null;
            ReindexCommand.NotifyCanExecuteChanged();
            OpenSettingsCommand.NotifyCanExecuteChanged();
        }
    }

    private static string Describe(IndexRunResult result, TimeSpan elapsed)
    {
        var outcome = result.Outcome;
        var parts = new List<string>
        {
            $"Indexed {outcome.FilesParsed:N0} files",
            $"{outcome.SymbolsFound:N0} symbols",
            $"{result.EdgesCreated:N0} links",
        };

        if (outcome.FilesUnchanged > 0)
        {
            parts.Add($"{outcome.FilesUnchanged:N0} unchanged");
        }

        if (outcome.FilesWithSyntaxErrors > 0)
        {
            parts.Add($"{outcome.FilesWithSyntaxErrors:N0} with syntax errors");
        }

        if (result.FilesRemoved > 0)
        {
            parts.Add($"{result.FilesRemoved:N0} removed");
        }

        return $"{string.Join(", ", parts)} in {elapsed.TotalSeconds:F1}s.";
    }

    private void ApplyProgress(IndexProgress progress)
    {
        Status.FilesDiscovered = progress.FilesDiscovered;
        Status.FilesProcessed = progress.FilesParsed + progress.FilesUnchanged;
        Status.ErrorCount = progress.FilesFailed + progress.FilesWithSyntaxErrors;
        Status.ProgressPercent = progress.PercentComplete;

        Status.Message = progress.Phase switch
        {
            IndexPhase.Crawling => "Scanning files…",
            IndexPhase.Parsing => "Parsing…",
            IndexPhase.Resolving => "Resolving references…",
            _ => Status.Message,
        };
    }

    private void CancelIndexing()
    {
        _indexCancellation?.Cancel();
        Status.Message = "Cancelling…";
    }

    // ---- Live updates -----------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveUpdateLabel))]
    private bool _isLiveUpdateEnabled = true;

    public string LiveUpdateLabel => IsLiveUpdateEnabled ? "Live: on" : "Live: off";

    partial void OnIsLiveUpdateEnabledChanged(bool value)
    {
        if (!value)
        {
            StopWatching();
            Status.Message = "Live updates off. Re-index to pick up changes.";
            return;
        }

        var selection = WorkspaceTree.CollectSelectedDirectories();
        StartWatching(selection);

        // A watcher only reports what happens after it starts, so anything edited while it
        // was off is invisible to it. Saying "watching for changes" and leaving the index
        // stale would be the wrong kind of reassuring; catching up is cheap, because the
        // size-and-stamp gate skips every file that did not actually move.
        if (_session is not null)
        {
            Status.Message = "Live updates on — catching up…";
            _ = RunIndexAsync(selection);
        }
    }

    [RelayCommand]
    private void ToggleLiveUpdates() => IsLiveUpdateEnabled = !IsLiveUpdateEnabled;

    private void StartWatching(IReadOnlyList<string> selectedDirectories)
    {
        if (_session is null || !IsLiveUpdateEnabled)
        {
            return;
        }

        try
        {
            if (_watcher is null)
            {
                _watcher = _session.CreateWatcher();
                _watcher.ChangesReady += OnChangesReady;
            }

            _watcher.Start(selectedDirectories);
        }
        catch (Exception ex)
        {
            // A watcher is a convenience. Losing it should degrade the app to manual
            // re-indexing, not stop the workspace from opening.
            _logger.LogError(ex, "Could not watch {Root}", _session.RootPath);
            Status.Message = "Could not watch this folder for changes; use Re-index.";
        }
    }

    private void StopWatching()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.ChangesReady -= OnChangesReady;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>Raised on the watcher's timer thread, so nothing here may touch the UI.</summary>
    private void OnChangesReady(object? sender, WorkspaceChangeBatch batch) =>
        _dispatcher.Post(() => _ = ApplyLiveChangesAsync(batch));

    private async Task ApplyLiveChangesAsync(WorkspaceChangeBatch batch)
    {
        if (_session is null || !IsLiveUpdateEnabled)
        {
            return;
        }

        // The watcher lost events and cannot say what changed. Anything short of a full pass
        // would leave the index quietly wrong.
        if (batch.ResyncRequired)
        {
            Status.Message = "Too many changes at once — re-indexing.";
            await RunIndexAsync(WorkspaceTree.CollectSelectedDirectories()).ConfigureAwait(true);
            return;
        }

        await _mutationGate.WaitAsync().ConfigureAwait(true);

        try
        {
            var session = _session;
            var result = await session.ApplyChangesAsync(batch).ConfigureAwait(true);

            if (!result.ChangedAnything)
            {
                return;
            }

            Status.Message = DescribeUpdate(result);
            await RefreshAfterUpdateAsync().ConfigureAwait(true);
            await RefreshErrorsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live update failed for {Count} paths", batch.TouchedCount);
            Status.Message = "Live update failed; use Re-index.";
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static string DescribeUpdate(LiveUpdateResult result)
    {
        var parts = new List<string>();

        if (result.FilesParsed > 0)
        {
            parts.Add($"{result.FilesParsed:N0} file{(result.FilesParsed == 1 ? "" : "s")} re-indexed");
        }

        if (result.FilesRemoved > 0)
        {
            parts.Add($"{result.FilesRemoved:N0} removed");
        }

        // Worth saying which path ran: one is milliseconds and one is the whole workspace,
        // and the difference explains a pause the user would otherwise wonder about.
        parts.Add(result.FullResolve
            ? $"{result.EdgesCreated:N0} links rebuilt"
            : $"{result.EdgesCreated:N0} links updated");

        return $"{string.Join(", ", parts)} in {result.Elapsed.TotalMilliseconds:F0} ms.";
    }

    /// <summary>
    /// Puts the panes back in step after the index moved under them.
    /// <para>
    /// Re-parsing a file replaces its symbol rows, so every id on screen that came from it is
    /// now dangling. The selected symbol is looked up again by file and name; search results
    /// are re-run for the same reason.
    /// </para>
    /// </summary>
    private async Task RefreshAfterUpdateAsync()
    {
        if (_session is null)
        {
            return;
        }

        await RelocateSelectionAsync().ConfigureAwait(true);
        await RelocatePathEndpointsAsync().ConfigureAwait(true);
        await RefreshCurrentViewAsync().ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(Search.Query))
        {
            await RunSearchAsync(Search.Query).ConfigureAwait(true);
        }
    }

    private async Task RelocateSelectionAsync()
    {
        if (_session is null || !Detail.HasSymbol || Detail.RelativePath is not { } path)
        {
            return;
        }

        var session = _session;
        var name = Detail.Name;
        var line = Detail.Line;

        var id = await Task
            .Run(() => session.Read(() => session.Graph.FindDefinitionId(path, name, line)))
            .ConfigureAwait(true);

        if (id is null)
        {
            Detail.Clear();
            Detail.EmptyMessage = $"{name} is no longer defined in {path}.";
            SetPathStartCommand.NotifyCanExecuteChanged();
            SetPathEndCommand.NotifyCanExecuteChanged();
            await Graph.ClearAsync($"{name} is no longer in the index.").ConfigureAwait(true);
            return;
        }

        // Rebuild rather than repaint: the neighbours may have changed too, and the graph is
        // holding ids from the file that was just re-parsed.
        await FocusSymbolAsync(id.Value).ConfigureAwait(true);
    }

    private async Task RelocatePathEndpointsAsync()
    {
        if (_session is null || (Graph.PathStartId is null && Graph.PathEndId is null))
        {
            return;
        }

        var session = _session;
        var start = Graph.PathStart;
        var end = Graph.PathEnd;

        var located = await Task.Run(() => session.Read(() => (
            Start: start is null ? null : session.Graph.FindDefinitionId(start.RelativePath, start.Name, start.Line),
            End: end is null ? null : session.Graph.FindDefinitionId(end.RelativePath, end.Name, end.Line))))
            .ConfigureAwait(true);

        Graph.RelocateEndpoints(located.Start, located.End);
    }

    /// <summary>
    /// Re-sends what the visible view should be showing, once the page is listening.
    /// <para>
    /// Session restore reopens the last workspace the moment the window loads, which is
    /// usually before WebView2 has finished starting. Everything posted until then was
    /// dropped, so the tabs would say Treemap while the page still showed the empty graph.
    /// </para>
    /// </summary>
    private async Task RepaintRendererAsync()
    {
        if (_session is null)
        {
            return;
        }

        if (Detail.HasSymbol)
        {
            await FocusSymbolAsync(Detail.SymbolId).ConfigureAwait(true);
        }

        await RefreshCurrentViewAsync().ConfigureAwait(true);
    }

    // ---- Session restore ---------------------------------------------------

    /// <summary>
    /// Reopens whatever was on screen last time. Called once the window is up, so the shell
    /// is already interactive while the cached index loads.
    /// </summary>
    public async Task RestoreSessionAsync()
    {
        SessionState state;

        try
        {
            state = await Task.Run(() => SessionStateStore.Load()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the saved session");
            return;
        }

        if (state.DarkTheme != IsDarkTheme)
        {
            ToggleThemeCommand.Execute(null);
        }

        IsLiveUpdateEnabled = state.LiveUpdates;

        // Pushed even with no workspace to reopen: the legend is on screen either way.
        Graph.LegendFontSize = state.LegendFontSize;
        Graph.ShowNodeDetails = state.GraphNodeDetails;

        if (string.IsNullOrWhiteSpace(state.WorkspaceRoot))
        {
            return;
        }

        var root = state.WorkspaceRoot;

        if (!Directory.Exists(root))
        {
            // Said plainly rather than silently starting empty: a workspace that has moved is
            // something the user should know about.
            Status.Message = $"Last workspace is gone: {root}";
            return;
        }

        if (!await OpenWorkspaceCoreAsync(root).ConfigureAwait(true))
        {
            return;
        }

        _treemapPath = state.TreemapPath;

        if (Enum.TryParse<GraphViewMode>(state.ViewMode, ignoreCase: true, out var mode))
        {
            // Assigning the mode raises ViewModeChanged, which loads whatever that view needs.
            Graph.ViewMode = mode;
        }

        await RestoreFocusedSymbolAsync(state).ConfigureAwait(true);
    }

    private async Task RestoreFocusedSymbolAsync(SessionState state)
    {
        if (_session is null
            || string.IsNullOrEmpty(state.FocusedRelativePath)
            || string.IsNullOrEmpty(state.FocusedSymbolName))
        {
            return;
        }

        var session = _session;

        var id = await Task
            .Run(() => session.Read(() => session.Graph.FindDefinitionId(
                state.FocusedRelativePath, state.FocusedSymbolName, state.FocusedLine)))
            .ConfigureAwait(true);

        // Gone since last time is an ordinary outcome, not an error: the file may have been
        // edited while the app was closed.
        if (id is not null)
        {
            await FocusSymbolAsync(id.Value).ConfigureAwait(true);
        }
    }

    /// <summary>Records the shell's state for next launch. Called as the window closes.</summary>
    public void SaveSession() => SessionStateStore.Save(new SessionState
    {
        WorkspaceRoot = WorkspaceRoot,
        DarkTheme = IsDarkTheme,
        ViewMode = Graph.ViewMode.ToString(),
        TreemapPath = _treemapPath,
        LiveUpdates = IsLiveUpdateEnabled,
        LegendFontSize = Graph.LegendFontSize,
        GraphNodeDetails = Graph.ShowNodeDetails,
        FocusedRelativePath = Detail.HasSymbol ? Detail.RelativePath : null,
        FocusedSymbolName = Detail.HasSymbol ? Detail.Name : null,
        FocusedLine = Detail.HasSymbol ? Detail.Line : 0,
    });

    // ---- Search ----------------------------------------------------------

    private void OnSearchPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.Query))
        {
            _ = RunSearchAsync(Search.Query);
        }
    }

    private async Task RunSearchAsync(string query)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        if (_session is null || string.IsNullOrWhiteSpace(query))
        {
            Search.Results.Clear();
            return;
        }

        try
        {
            // Debounce: a fast typist should not trigger a scan per character.
            await Task.Delay(SearchDebounceMs, token).ConfigureAwait(true);

            Search.IsSearching = true;

            // Read on the UI thread, where the toggles live, and handed to the worker as a
            // fixed set: the user may click another chip while this search is running.
            var kinds = Search.SelectedKinds();
            var options = kinds is null ? null : new SymbolSearchOptions { Kinds = kinds };

            var session = _session;
            var hits = await Task.Run(
                    () => session.Read(() => session.Search.Search(query, options, token)), token)
                .ConfigureAwait(true);

            token.ThrowIfCancellationRequested();

            Search.Results.Clear();
            foreach (var hit in hits)
            {
                Search.Results.Add(new SearchResultItem(
                    hit.SymbolId,
                    hit.Name,
                    KindLabels.For(hit.Kind),
                    string.Empty,
                    hit.RelativePath,
                    hit.Line,
                    hit.ContainerName,
                    hit.ParameterText,
                    hit.Descriptor));
            }

            // The filter is named in the empty message: an active filter is the most likely
            // reason a query that used to match now does not.
            Search.EmptyMessage = hits.Count == 0
                ? Search.HasKindFilter
                    ? $"No symbols match '{query}' in the selected kinds."
                    : $"No symbols match '{query}'."
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Query}", query);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                Search.IsSearching = false;
            }
        }
    }

    /// <summary>
    /// Returns the result list to every kind. Lives here rather than on the search view
    /// model only so the toolbar button has a command to bind to.
    /// </summary>
    [RelayCommand]
    private void ClearSearchFilters() => Search.ClearKindFilters();

    private void OnSearchResultActivated(object? sender, SearchResultItem result) =>
        _ = FocusSymbolAsync(result.SymbolId);

    // ---- Graph -----------------------------------------------------------

    /// <summary>
    /// Opens a symbol the detail pane is already naming: an overload sibling, a caller, a
    /// callee. The graph and the search box could always reach these; the lists that named
    /// them could not, which made jumping between two overloads a round trip through the
    /// search box for something already on screen.
    /// </summary>
    [RelayCommand]
    private Task FocusSymbol(long symbolId) => FocusSymbolAsync(symbolId);

    /// <summary>
    /// Makes a symbol the centre of attention: facts, source, and a fresh graph around it.
    /// </summary>
    private async Task FocusSymbolAsync(long symbolId)
    {
        if (_session is null)
        {
            return;
        }

        if (!await ShowSymbolFactsAsync(symbolId).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var session = _session;
            var fragment = await Task
                .Run(() => session.Read(() => session.Graph.GetNeighbourhood(symbolId)))
                .ConfigureAwait(true);

            await Graph.ShowAsync(fragment).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the graph for symbol {SymbolId}", symbolId);
        }
    }

    /// <summary>
    /// Loads the detail pane and the source preview. Returns false when the symbol has
    /// gone, for instance because the index was rebuilt under a stale graph node.
    /// </summary>
    private async Task<bool> ShowSymbolFactsAsync(long symbolId)
    {
        if (_session is null)
        {
            return false;
        }

        try
        {
            var session = _session;
            var detail = await Task
                .Run(() => session.Read(() => session.Graph.GetDetail(symbolId)))
                .ConfigureAwait(true);

            if (detail is null)
            {
                return false;
            }

            ApplyDetail(detail);

            var source = await Task.Run(() => session.TryReadSource(detail.RelativePath)).ConfigureAwait(true);
            if (source is not null)
            {
                Preview.RelativePath = detail.RelativePath;
                Preview.Language = detail.Language;
                Preview.Text = source;
                Preview.HighlightLine = detail.StartLine;
                Preview.HasContent = true;
            }
            else
            {
                Preview.Clear();
                Preview.EmptyMessage = $"Could not read {detail.RelativePath}.";
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load facts for symbol {SymbolId}", symbolId);
            return false;
        }
    }

    /// <summary>Pulls in one more ring of neighbours around a node already on screen.</summary>
    private async Task ExpandAsync(GraphExpandRequest request)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var session = _session;
            var fragment = await Task
                .Run(() => session.Read(() => session.Graph.GetNeighbourhood(request.SymbolId, request.Direction)))
                .ConfigureAwait(true);

            await Graph.MergeAsync(fragment, request.SymbolId, request.Direction).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to expand symbol {SymbolId}", request.SymbolId);
        }
    }

    /// <summary>
    /// Answers an edge click with the un-merged call sites behind it. The page shows them
    /// in the popover; fetching on click keeps them out of every setGraph payload.
    /// </summary>
    private async Task AnswerEdgeSelectionAsync(GraphEdgeSelection selection)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var session = _session;
            var sites = await Task
                .Run(() => session.Read(() =>
                    session.Graph.GetEdgeCallSites(selection.SourceId, selection.TargetId, selection.Kind)))
                .ConfigureAwait(true);

            await Graph.ShowEdgeDetailsAsync(selection.EdgeId, sites).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load call sites for edge {EdgeId}", selection.EdgeId);
        }
    }

    /// <summary>
    /// Opens the preview at one call site — a double-tapped edge, or a row in its list.
    /// The source symbol's file is where the reference physically sits.
    /// </summary>
    private async Task OpenEdgeCallSiteAsync(GraphEdgeActivation activation)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var session = _session;
            var detail = await Task
                .Run(() => session.Read(() => session.Graph.GetDetail(activation.SourceId)))
                .ConfigureAwait(true);

            if (detail is null)
            {
                return;
            }

            var source = await Task.Run(() => session.TryReadSource(detail.RelativePath)).ConfigureAwait(true);
            if (source is null)
            {
                Preview.Clear();
                Preview.EmptyMessage = $"Could not read {detail.RelativePath}.";
                return;
            }

            Preview.RelativePath = detail.RelativePath;
            Preview.Language = detail.Language;
            Preview.Text = source;
            Preview.HighlightLine = activation.Line;
            Preview.HasContent = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open call site for symbol {SymbolId}", activation.SourceId);
        }
    }

    // ---- The other four views ---------------------------------------------

    /// <summary>
    /// Loads whatever the visible view needs. Called on every view switch, so a view is
    /// never left showing the previous workspace's facts.
    /// </summary>
    private Task RefreshCurrentViewAsync() => Graph.ViewMode switch
    {
        GraphViewMode.Composition => LoadCompositionAsync(),
        GraphViewMode.Paths => TracePathsAsync(),
        GraphViewMode.Treemap => LoadTreemapAsync(),
        GraphViewMode.Wheel => LoadWheelAsync(),
        _ => Task.CompletedTask,
    };

    private async Task LoadCompositionAsync()
    {
        if (_session is null || !Detail.HasSymbol)
        {
            await Graph.ShowCompositionAsync(null).ConfigureAwait(true);
            return;
        }

        try
        {
            var session = _session;
            var symbolId = Detail.SymbolId;
            var view = await Task
                .Run(() => session.Read(() => session.Composition.GetComposition(symbolId)))
                .ConfigureAwait(true);

            // A stale result from a slower query must not overwrite a newer selection.
            if (Detail.SymbolId == symbolId)
            {
                await Graph.ShowCompositionAsync(view).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load composition for symbol {SymbolId}", Detail.SymbolId);
        }
    }

    private async Task TracePathsAsync()
    {
        if (_session is null || Graph.PathStartId is null || Graph.PathEndId is null)
        {
            await Graph.ShowPathsAsync(null).ConfigureAwait(true);
            return;
        }

        try
        {
            var session = _session;
            var from = Graph.PathStartId.Value;
            var to = Graph.PathEndId.Value;

            Status.Message = $"Tracing {Graph.PathStartName} → {Graph.PathEndName}…";

            var trace = await Task
                .Run(() => session.Read(() => session.Paths.FindPaths(from, to)))
                .ConfigureAwait(true);

            await Graph.ShowPathsAsync(trace).ConfigureAwait(true);

            Status.Message = trace.Routes.Count > 0
                ? $"{trace.Routes.Count:N0} shortest route{(trace.Routes.Count == 1 ? "" : "s")}, " +
                  $"{trace.Length} hop{(trace.Length == 1 ? "" : "s")} each."
                : trace.SearchExhausted
                    ? "No route found within the search limit."
                    : "No route between those two symbols.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trace paths");
            Status.Message = "Path trace failed.";
        }
    }

    private async Task DrillTreemapAsync(string path)
    {
        _treemapPath = path ?? string.Empty;

        // Setting the mode raises ViewModeChanged, which loads the level. Loading here as
        // well would run the same query twice and let the slower one win.
        if (Graph.ViewMode != GraphViewMode.Treemap)
        {
            Graph.ViewMode = GraphViewMode.Treemap;
            return;
        }

        await LoadTreemapAsync().ConfigureAwait(true);
    }

    private async Task LoadTreemapAsync()
    {
        if (_session is null)
        {
            await Graph.ShowTreemapAsync(null).ConfigureAwait(true);
            return;
        }

        try
        {
            var session = _session;
            var path = _treemapPath;
            var level = await Task
                .Run(() => session.Read(() => session.Structure.GetTreemapLevel(path)))
                .ConfigureAwait(true);

            // Drilling into something with nothing under it would leave a blank pane and no
            // way back, so stay where we are and say why.
            if (level.Tiles.Count == 0 && path.Length > 0)
            {
                Status.Message = $"Nothing indexed under {path}.";
                return;
            }

            await Graph.ShowTreemapAsync(level).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the treemap level {Path}", _treemapPath);
        }
    }

    private async Task LoadWheelAsync()
    {
        if (_session is null)
        {
            await Graph.ShowWheelAsync(null).ConfigureAwait(true);
            return;
        }

        try
        {
            var session = _session;
            var source = Graph.WheelSource;
            var wheel = await Task
                .Run(() => session.Read(() => session.Structure.GetDependencyWheel(source)))
                .ConfigureAwait(true);

            await Graph.ShowWheelAsync(wheel).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the dependency wheel");
        }
    }

    private bool CanSetPathEndpoint() =>
        _session is not null && Detail.HasSymbol && Detail.RelativePath is not null;

    [RelayCommand(CanExecute = nameof(CanSetPathEndpoint))]
    private void SetPathStart() =>
        Graph.SetPathEndpoint(start: true, Detail.SymbolId, Detail.Name, Detail.RelativePath!, Detail.Line);

    [RelayCommand(CanExecute = nameof(CanSetPathEndpoint))]
    private void SetPathEnd() =>
        Graph.SetPathEndpoint(start: false, Detail.SymbolId, Detail.Name, Detail.RelativePath!, Detail.Line);

    private void ApplyDetail(Core.Graph.SymbolDetail detail)
    {
        Detail.SymbolId = detail.Id;
        Detail.Name = detail.Name;
        Detail.KindLabel = KindLabels.For(detail.Kind);
        Detail.Descriptor = SymbolFacts.Describe(
            detail.Kind,
            detail.Modifiers,
            detail.TypeText,
            hasParameterList: detail.ParameterText is not null,
            detail.OverloadCount,
            detail.OverloadOrdinal);
        Detail.Parameters = detail.ParameterText;
        Detail.ScopePath = string.IsNullOrEmpty(detail.ScopePath) ? null : detail.ScopePath;
        Detail.Signature = detail.Signature;
        Detail.Value = detail.Value;
        Detail.TypeText = detail.TypeText;
        Detail.Modifiers = detail.Modifiers;
        Detail.RelativePath = detail.RelativePath;
        Detail.Line = detail.StartLine;
        Detail.Language = detail.Language;

        Detail.Overloads.Clear();
        foreach (var overload in detail.Overloads)
        {
            // Falling back to the signature keeps the row from being blank for a language
            // whose pack captures a declarator but no parameter node.
            Detail.Overloads.Add(new OverloadItem(
                overload.Id,
                overload.ParameterText ?? overload.Signature ?? detail.Name,
                overload.Line,
                overload.IsCurrent));
        }

        Detail.Members.Clear();
        foreach (var member in detail.Members)
        {
            Detail.Members.Add(new MemberItem(
                member.Id, member.Name, KindLabels.For(member.Kind), member.TypeText, member.Value));
        }

        Detail.Callers.Clear();
        foreach (var caller in detail.Callers)
        {
            Detail.Callers.Add(ToRelatedItem(caller));
        }

        Detail.Callees.Clear();
        foreach (var callee in detail.Callees)
        {
            Detail.Callees.Add(ToRelatedItem(callee));
        }

        Detail.UnresolvedReferences.Clear();
        foreach (var unresolved in detail.UnresolvedReferences)
        {
            Detail.UnresolvedReferences.Add(new UnresolvedReferenceItem(
                unresolved.Name, KindLabels.For(unresolved.Kind), unresolved.Line));
        }

        Detail.HasSymbol = true;

        SetPathStartCommand.NotifyCanExecuteChanged();
        SetPathEndCommand.NotifyCanExecuteChanged();

        // The composition inspector follows the selection the same way the detail pane
        // does, so switching symbols while it is open keeps it in step.
        if (Graph.ViewMode == GraphViewMode.Composition)
        {
            _ = LoadCompositionAsync();
        }
    }

    private static RelatedSymbolItem ToRelatedItem(Core.Graph.RelatedSymbol related) => new(
        related.Id,
        related.Name,
        KindLabels.For(related.Kind),
        related.RelativePath,
        related.Line,
        KindLabels.For(related.Confidence),
        related.Confidence != EdgeConfidence.Unique);

    // ---- Settings ----------------------------------------------------------

    private bool CanEditSettings() => _session is not null && !Status.IsIndexing;

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void OpenSettings()
    {
        if (_session is null)
        {
            return;
        }

        var edited = _dialogService.EditSettings(_session.Settings);
        if (edited is null)
        {
            return;
        }

        _session.SaveSettings(edited);

        // The running watcher was built with the old rules; stopping it here lets the
        // re-index below finish with a fresh one. The re-index is what makes the rules
        // true in both directions — newly excluded files leave the index the same way
        // deleted files do.
        StopWatching();
        Status.Message = "Settings saved — re-indexing…";
        _ = ApplySettingsAsync(edited);
    }

    private async Task ApplySettingsAsync(Core.Crawling.WorkspaceSettings settings)
    {
        if (_session is null)
        {
            return;
        }

        // The tree's grey-out has to keep matching what the crawler skips, and its ignore
        // rules are baked in at load. Reloading collapses it, so the selection is carried
        // across by hand.
        var selection = WorkspaceTree.CollectSelectedDirectories();
        await WorkspaceTree.LoadWorkspaceAsync(_session.RootPath, settings).ConfigureAwait(true);
        WorkspaceTree.RestoreSelection(selection);

        await RunIndexAsync(selection).ConfigureAwait(true);
    }

    // ---- Export ------------------------------------------------------------

    private bool CanExport() => _session is not null && Graph.IsGraphView;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportPngAsync() => Graph.RequestExportAsync(GraphExportFormat.Png);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportJsonAsync() => Graph.RequestExportAsync(GraphExportFormat.Json);

    /// <summary>
    /// Saves what the page produced. The page answers with what is actually on the canvas —
    /// including expansions — which the host cannot reconstruct, so the data crosses the
    /// bridge rather than being rebuilt here.
    /// </summary>
    private async Task SaveExportAsync(GraphExportResult result)
    {
        if (result.Data is null)
        {
            Status.Message = "Nothing to export — the graph is empty.";
            return;
        }

        var isPng = result.Format == GraphExportFormat.Png;
        var baseName = Detail.HasSymbol ? SanitizeFileName(Detail.Name) : "graph";

        var path = _dialogService.PickSaveFile(
            isPng ? "Export graph as PNG" : "Export graph as JSON",
            isPng ? "PNG image (*.png)|*.png" : "JSON (*.json)|*.json",
            baseName + (isPng ? ".png" : ".json"));

        if (path is null)
        {
            return;
        }

        try
        {
            if (isPng)
            {
                // The page sends a data URL; everything after the comma is the image.
                var comma = result.Data.IndexOf(',');
                var bytes = Convert.FromBase64String(result.Data[(comma + 1)..]);
                await Task.Run(() => File.WriteAllBytes(path, bytes)).ConfigureAwait(true);
            }
            else
            {
                await Task.Run(() => File.WriteAllText(path, result.Data)).ConfigureAwait(true);
            }

            Status.Message = $"Exported {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export the graph to {Path}", path);
            _dialogService.ShowError("Export", $"Could not write '{path}':\n\n{ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim('_', '.', ' ');
        return cleaned.Length == 0 ? "graph" : cleaned;
    }

    // ---- Error list ---------------------------------------------------------

    public ObservableCollection<FileErrorItem> FileErrors { get; } = [];

    [ObservableProperty]
    private bool _isErrorPaneVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorSummary))]
    [NotifyPropertyChangedFor(nameof(HasFileErrors))]
    private int _errorFileCount;

    public bool HasFileErrors => ErrorFileCount > 0;

    public string ErrorSummary =>
        $"⚠ {ErrorFileCount:N0} file{(ErrorFileCount == 1 ? "" : "s")} with errors";

    [RelayCommand]
    private void ToggleErrorPane() => IsErrorPaneVisible = !IsErrorPaneVisible;

    [ObservableProperty]
    private FileErrorItem? _selectedError;

    /// <summary>Selecting an error opens the file, so the arrow keys walk the list.</summary>
    partial void OnSelectedErrorChanged(FileErrorItem? value)
    {
        if (value is not null)
        {
            _ = OpenErrorFileAsync(value);
        }
    }

    /// <summary>
    /// Re-reads the files whose last parse was imperfect. Called whenever the index moved:
    /// after an index run, after a live update, and on open, because a cached index
    /// remembers its errors too.
    /// </summary>
    private async Task RefreshErrorsAsync()
    {
        if (_session is null)
        {
            FileErrors.Clear();
            ErrorFileCount = 0;
            IsErrorPaneVisible = false;
            return;
        }

        try
        {
            var session = _session;
            var errors = await Task
                .Run(() => session.Read(() => session.ReadFileErrors()))
                .ConfigureAwait(true);

            FileErrors.Clear();
            foreach (var error in errors)
            {
                // The distinction matters: a syntax error still contributed partial
                // symbols; a hard failure contributed nothing.
                var description = error.Message
                    ?? $"Syntax errors — {error.SymbolCount:N0} symbol{(error.SymbolCount == 1 ? "" : "s")} still indexed";

                FileErrors.Add(new FileErrorItem(error.RelativePath, error.Language, description));
            }

            ErrorFileCount = FileErrors.Count;

            if (ErrorFileCount == 0)
            {
                IsErrorPaneVisible = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the file error list");
        }
    }

    private async Task OpenErrorFileAsync(FileErrorItem item)
    {
        if (_session is null)
        {
            return;
        }

        var session = _session;
        var source = await Task.Run(() => session.TryReadSource(item.RelativePath)).ConfigureAwait(true);

        if (source is null)
        {
            Preview.Clear();
            Preview.EmptyMessage = $"Could not read {item.RelativePath}.";
            return;
        }

        Preview.RelativePath = item.RelativePath;
        Preview.Language = item.Language;
        Preview.Text = source;
        Preview.HighlightLine = 1;
        Preview.HasContent = true;
    }

    // ---- Keyboard ----------------------------------------------------------

    /// <summary>Raised by Ctrl+F. Focus lives in the view, so the window answers this.</summary>
    public event EventHandler? SearchFocusRequested;

    [RelayCommand]
    private void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    // ---- Misc ------------------------------------------------------------

    [RelayCommand]
    private void ToggleTheme()
    {
        _themeService.Toggle();
        IsDarkTheme = _themeService.Current == AppTheme.Dark;
    }

    public void Dispose()
    {
        StopWatching();
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _mutationGate.Dispose();
        _session?.Dispose();
    }
}
