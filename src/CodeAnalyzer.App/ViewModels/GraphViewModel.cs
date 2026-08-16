using CodeAnalyzer.App.Services;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// Owns the conversation with the renderer.
/// <para>
/// It knows how to talk to <see cref="IGraphViewService"/> but nothing about where graph
/// data comes from: requests for more data are raised as events and answered by the shell,
/// which is the piece that holds the workspace session.
/// </para>
/// </summary>
public sealed partial class GraphViewModel : ObservableObject
{
    private readonly IGraphViewService _service;
    private readonly IThemeService _themeService;

    public GraphViewModel(IGraphViewService service, IThemeService themeService)
    {
        _service = service;
        _themeService = themeService;

        _service.Ready += OnRendererReady;
        _service.NodeSelected += (_, id) => SymbolSelected?.Invoke(this, id);
        _service.NodeActivated += (_, id) => SymbolActivated?.Invoke(this, id);
        _service.ExpandRequested += (_, request) => ExpandRequested?.Invoke(this, request);

        // Forwarded like expansion: the shell holds the session, so it runs the call-site
        // query and answers the page; double-taps open the preview, which is also its job.
        _service.EdgeSelected += (_, selection) => EdgeSelected?.Invoke(this, selection);
        _service.EdgeActivated += (_, activation) => EdgeActivated?.Invoke(this, activation);

        // Same shape again: the per-site rows behind a stub live in the index.
        _service.IoStubSelected += (_, selection) => IoStubSelected?.Invoke(this, selection);

        // Forwarded rather than acted on here: the shell owns which path the treemap is
        // showing, and letting both switch the view would load the level twice, in a
        // racing order.
        _service.DrillRequested += (_, path) => DrillRequested?.Invoke(this, path);

        // Forwarded for the same reason: saving a file is the shell's job.
        _service.ExportProduced += (_, result) => ExportProduced?.Invoke(this, result);

        // The constants view's filters change what is queried, not just what is drawn, so
        // the page's toggles come back here and the shell re-runs the query.
        _service.ConstantsOptionsChanged += (_, options) =>
        {
            ConstantsOptions = options;
            ConstantsOptionsChanged?.Invoke(this, options);
        };

        // The page has already applied the new size; recording it here is what lets the
        // shell persist it and re-send it after a restart or a renderer crash.
        _service.LegendSizeChanged += (_, size) =>
        {
            _legendFontSize = LegendFontSizes.Clamp(size);
            LegendFontSizeChanged?.Invoke(this, EventArgs.Empty);
        };

        _service.NodeDetailsChanged += (_, show) =>
        {
            _showNodeDetails = show;
            ShowNodeDetailsChanged?.Invoke(this, EventArgs.Empty);
        };

        _themeService.ThemeChanged += (_, theme) => _ = _service.SetThemeAsync(theme);
    }

    private void OnRendererReady(object? sender, EventArgs e)
    {
        IsRendererReady = true;

        // The page starts on its own default palette and on its own default view, so tell it
        // what is actually selected. Doing this on Ready rather than at construction is what
        // makes session restore work: the shell reopens the last workspace as soon as the
        // window loads, and WebView2 finishes starting whenever it finishes — everything
        // posted before the page was listening was dropped on the floor.
        _ = _service.SetThemeAsync(_themeService.Current);
        _ = _service.SetViewModeAsync(ViewMode);
        _ = _service.SetLegendSizeAsync(LegendFontSize);
        _ = _service.SetNodeDetailsAsync(ShowNodeDetails);

        RendererReady?.Invoke(this, EventArgs.Empty);
    }

    private double _legendFontSize = LegendFontSizes.Default;

    /// <summary>
    /// Legend font size in CSS pixels. Assigning it pushes the value to the page; the page
    /// assigning it back arrives through <see cref="LegendFontSizeChanged"/> instead, so a
    /// user's click is never echoed straight back at them.
    /// </summary>
    public double LegendFontSize
    {
        get => _legendFontSize;
        set
        {
            var clamped = LegendFontSizes.Clamp(value);
            if (Math.Abs(clamped - _legendFontSize) < 0.001)
            {
                return;
            }

            _legendFontSize = clamped;
            OnPropertyChanged();
            _ = _service.SetLegendSizeAsync(clamped);
        }
    }

    /// <summary>The page changed the legend size. The shell stores it in the session.</summary>
    public event EventHandler? LegendFontSizeChanged;

    private bool _showNodeDetails = true;

    /// <summary>
    /// Whether nodes draw their parameter list and descriptor. Same echo rule as
    /// <see cref="LegendFontSize"/>: assigning pushes to the page, the page assigning it
    /// back arrives through <see cref="ShowNodeDetailsChanged"/>.
    /// </summary>
    public bool ShowNodeDetails
    {
        get => _showNodeDetails;
        set
        {
            if (value == _showNodeDetails)
            {
                return;
            }

            _showNodeDetails = value;
            OnPropertyChanged();
            _ = _service.SetNodeDetailsAsync(value);
        }
    }

    /// <summary>The page toggled the node detail lines. The shell stores it in the session.</summary>
    public event EventHandler? ShowNodeDetailsChanged;

    /// <summary>
    /// The page is listening. The shell answers this by re-sending whatever the visible view
    /// should be showing, since anything sent earlier never arrived.
    /// </summary>
    public event EventHandler? RendererReady;

    /// <summary>A node was clicked: show its facts, but leave the graph where it is.</summary>
    public event EventHandler<long>? SymbolSelected;

    /// <summary>A node was double-clicked: rebuild the graph around it.</summary>
    public event EventHandler<long>? SymbolActivated;

    /// <summary>More neighbours were asked for in one direction.</summary>
    public event EventHandler<GraphExpandRequest>? ExpandRequested;

    /// <summary>An edge was clicked: fetch its call sites and answer the page.</summary>
    public event EventHandler<GraphEdgeSelection>? EdgeSelected;

    /// <summary>An edge asked to open the source at a call site.</summary>
    public event EventHandler<GraphEdgeActivation>? EdgeActivated;

    /// <summary>An I/O stub was clicked: show its sites and framing in the detail pane.</summary>
    public event EventHandler<IoStubSelection>? IoStubSelected;

    /// <summary>A treemap tile or wheel arc was opened. Carries a workspace-relative path.</summary>
    public event EventHandler<string>? DrillRequested;

    /// <summary>The visible view changed, so the shell can load what it needs.</summary>
    public event EventHandler<GraphViewMode>? ViewModeChanged;

    /// <summary>Both path endpoints are set, so a trace can be run.</summary>
    public event EventHandler? PathEndpointsChanged;

    /// <summary>The wheel should be redrawn from the other set of facts.</summary>
    public event EventHandler? WheelSourceChanged;

    /// <summary>The constants view wants a different question asked of the index.</summary>
    public event EventHandler<ConstantsOptions>? ConstantsOptionsChanged;

    /// <summary>
    /// The constants view's current filters. Held here rather than in the page alone so a
    /// re-render — a live update, a view switch — asks the same question again.
    /// </summary>
    public ConstantsOptions ConstantsOptions { get; private set; } = new(false, false);

    /// <summary>The page answered an export request. The shell saves it.</summary>
    public event EventHandler<GraphExportResult>? ExportProduced;

    /// <summary>Asks the page for the current graph in the given format.</summary>
    public Task RequestExportAsync(GraphExportFormat format) => _service.RequestExportAsync(format);

    /// <summary>Delivers the call sites behind one merged edge to the page's popover.</summary>
    public Task ShowEdgeDetailsAsync(string edgeId, IReadOnlyList<EdgeCallSite> sites) =>
        _service.ShowEdgeDetailsAsync(edgeId, sites);

    [ObservableProperty]
    private bool _useHierarchicalLayout;

    [ObservableProperty]
    private long? _focusedSymbolId;

    [ObservableProperty]
    private bool _isRendererReady;

    private GraphLayout Layout => UseHierarchicalLayout ? GraphLayout.Hierarchy : GraphLayout.Force;

    // ---- View mode --------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGraphView))]
    [NotifyPropertyChangedFor(nameof(IsPathsView))]
    [NotifyPropertyChangedFor(nameof(IsWheelView))]
    private GraphViewMode _viewMode = GraphViewMode.Graph;

    /// <summary>Drives the toolbar controls that only make sense over one of the views.</summary>
    public bool IsGraphView => ViewMode == GraphViewMode.Graph;

    public bool IsPathsView => ViewMode == GraphViewMode.Paths;

    public bool IsWheelView => ViewMode == GraphViewMode.Wheel;

    partial void OnViewModeChanged(GraphViewMode value)
    {
        _ = _service.SetViewModeAsync(value);
        ViewModeChanged?.Invoke(this, value);
    }

    /// <summary>
    /// Bound from the toolbar tabs. Takes the mode by name because a XAML CommandParameter
    /// cannot express an enum member without a converter for every call site.
    /// </summary>
    [RelayCommand]
    private void SelectView(string? mode)
    {
        if (Enum.TryParse<GraphViewMode>(mode, ignoreCase: true, out var parsed))
        {
            ViewMode = parsed;
        }
    }

    // ---- Path endpoints ---------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwapPathEndpointsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPathEndpointsCommand))]
    private long? _pathStartId;

    [ObservableProperty]
    private string _pathStartName = "not set";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwapPathEndpointsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPathEndpointsCommand))]
    private long? _pathEndId;

    [ObservableProperty]
    private string _pathEndName = "not set";

    /// <summary>
    /// Where an endpoint lives, kept so it can be found again after a live update. Symbol
    /// ids do not survive their file being re-parsed; a file and a name do.
    /// </summary>
    public sealed record PathEndpoint(string Name, string RelativePath, int Line);

    public PathEndpoint? PathStart { get; private set; }

    public PathEndpoint? PathEnd { get; private set; }

    /// <summary>
    /// Records one end of a trace. Kept as an explicit action rather than "whatever you
    /// clicked last" so that browsing the graph in the middle of choosing endpoints does
    /// not silently move one of them.
    /// </summary>
    public void SetPathEndpoint(bool start, long symbolId, string name, string relativePath, int line)
    {
        var endpoint = new PathEndpoint(name, relativePath, line);

        if (start)
        {
            PathStartId = symbolId;
            PathStartName = name;
            PathStart = endpoint;
        }
        else
        {
            PathEndId = symbolId;
            PathEndName = name;
            PathEnd = endpoint;
        }

        PathEndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Points the endpoints at their new ids after a re-parse. An endpoint that no longer
    /// exists is cleared and labelled, rather than being left pointing at a dead id that
    /// would silently trace nothing.
    /// </summary>
    public void RelocateEndpoints(long? startId, long? endId)
    {
        var changed = false;

        if (PathStart is not null && startId != PathStartId)
        {
            PathStartId = startId;
            if (startId is null)
            {
                PathStartName = $"{PathStart.Name} (gone)";
                PathStart = null;
            }

            changed = true;
        }

        if (PathEnd is not null && endId != PathEndId)
        {
            PathEndId = endId;
            if (endId is null)
            {
                PathEndName = $"{PathEnd.Name} (gone)";
                PathEnd = null;
            }

            changed = true;
        }

        if (changed)
        {
            PathEndpointsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool HasBothEndpoints() => PathStartId is not null && PathEndId is not null;

    [RelayCommand(CanExecute = nameof(HasBothEndpoints))]
    private void SwapPathEndpoints()
    {
        (PathStartId, PathEndId) = (PathEndId, PathStartId);
        (PathStartName, PathEndName) = (PathEndName, PathStartName);
        (PathStart, PathEnd) = (PathEnd, PathStart);
        PathEndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool HasAnyEndpoint() => PathStartId is not null || PathEndId is not null;

    [RelayCommand(CanExecute = nameof(HasAnyEndpoint))]
    private void ClearPathEndpoints()
    {
        PathStartId = null;
        PathEndId = null;
        PathStartName = "not set";
        PathEndName = "not set";
        PathStart = null;
        PathEnd = null;
        PathEndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Wheel source -----------------------------------------------------

    [ObservableProperty]
    private WheelSource _wheelSource = WheelSource.Includes;

    [ObservableProperty]
    private string _wheelSourceLabel = "Includes & imports";

    partial void OnWheelSourceChanged(WheelSource value)
    {
        WheelSourceLabel = value == WheelSource.Includes ? "Includes & imports" : "Symbol references";
        WheelSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleWheelSource() =>
        WheelSource = WheelSource == WheelSource.Includes ? WheelSource.References : WheelSource.Includes;

    // ---- Rendering --------------------------------------------------------

    /// <summary>Replaces the graph with the neighbourhood of a symbol.</summary>
    public Task ShowAsync(GraphFragment fragment)
    {
        FocusedSymbolId = fragment.FocusId;
        return _service.ShowGraphAsync(GraphPayloadBuilder.Build(fragment), Layout);
    }

    /// <summary>Folds an expansion into the graph already on screen.</summary>
    public Task MergeAsync(GraphFragment fragment, long expandedSymbolId, GraphDirection direction) =>
        _service.MergeGraphAsync(GraphPayloadBuilder.Build(fragment), expandedSymbolId, direction);

    /// <summary>Centres a node that is already drawn.</summary>
    public Task FocusAsync(long symbolId) => _service.FocusNodeAsync(symbolId);

    public Task ShowCompositionAsync(CompositionView? view) =>
        _service.ShowCompositionAsync(view is null ? null : ViewPayloadBuilder.Build(view));

    public Task ShowPathsAsync(PathTrace? trace) =>
        _service.ShowPathsAsync(trace is null ? null : ViewPayloadBuilder.Build(trace));

    public Task ShowTreemapAsync(TreemapLevel? level) =>
        _service.ShowTreemapAsync(level is null ? null : ViewPayloadBuilder.Build(level));

    public Task ShowWheelAsync(DependencyWheel? wheel) =>
        _service.ShowWheelAsync(wheel is null ? null : ViewPayloadBuilder.Build(wheel));

    public Task ShowBoundariesAsync(IReadOnlyList<Core.Domain.IoBoundarySite>? sites) =>
        _service.ShowBoundariesAsync(sites is null ? null : ViewPayloadBuilder.Build(sites));

    /// <summary>
    /// Replaces the constants view. The filters ride along on the payload, because the page
    /// states the criterion it is showing rather than leaving the reader to remember it.
    /// </summary>
    public Task ShowConstantsAsync(IReadOnlyList<ValueGroup>? groups) =>
        _service.ShowConstantsAsync(groups is null
            ? null
            : ViewPayloadBuilder.Build(
                groups, ConstantsOptions.AcrossDirectories, ConstantsOptions.IncludeTrivial));

    public Task ClearAsync(string message)
    {
        FocusedSymbolId = null;
        return _service.ClearAsync(message);
    }

    [RelayCommand]
    private Task ToggleLayoutAsync()
    {
        UseHierarchicalLayout = !UseHierarchicalLayout;
        return _service.SetLayoutAsync(Layout);
    }

    partial void OnUseHierarchicalLayoutChanged(bool value) =>
        LayoutLabel = value ? "Hierarchy" : "Force";

    [ObservableProperty]
    private string _layoutLabel = "Force";
}
