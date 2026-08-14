using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;

namespace CodeAnalyzer.App.Services;

/// <summary>Which layout the graph page should run.</summary>
public enum GraphLayout
{
    /// <summary>Force-directed (fcose). Good for seeing clusters.</summary>
    Force,

    /// <summary>Layered left-to-right (dagre). Good for following a call chain.</summary>
    Hierarchy,
}

/// <summary>Which of the five pictures the page is showing.</summary>
public enum GraphViewMode
{
    /// <summary>Callers and callees around one symbol.</summary>
    Graph,

    /// <summary>What a symbol is made of: members, instantiations, type relations.</summary>
    Composition,

    /// <summary>Routes between two chosen symbols.</summary>
    Paths,

    /// <summary>Where the code is, drilled one directory level at a time.</summary>
    Treemap,

    /// <summary>Dependencies between top-level directories.</summary>
    Wheel,
}

/// <summary>A request from the page to pull in more neighbours for one node.</summary>
public sealed record GraphExpandRequest(long SymbolId, GraphDirection Direction);

/// <summary>
/// An edge was clicked. Identifies the merged edge by its group key — source, target and
/// reference kind — plus the page's own edge id, echoed back so the answer can be matched
/// to the edge still selected when it arrives.
/// </summary>
public sealed record GraphEdgeSelection(long SourceId, long TargetId, ReferenceKind Kind, string EdgeId);

/// <summary>An edge (or one of its call-site rows) asked to open the source at a line.</summary>
public sealed record GraphEdgeActivation(long SourceId, int Line);

/// <summary>What form an export should take.</summary>
public enum GraphExportFormat
{
    /// <summary>The rendered picture, as a PNG data URL.</summary>
    Png,

    /// <summary>The indexed facts on the canvas, as a JSON document.</summary>
    Json,
}

/// <summary>
/// What the page produced for an export request. <paramref name="Data"/> is a PNG data URL
/// or a JSON string, and null when the canvas was empty — the caller says so rather than
/// writing an empty file.
/// </summary>
public sealed record GraphExportResult(GraphExportFormat Format, string? Data);

/// <summary>
/// The renderer, as the view models see it.
/// <para>
/// Deliberately free of any WebView2 type: view models talk to this interface only, the
/// concrete host registers itself from the view's code-behind, and tests can substitute a
/// recording fake. Every method is safe to call before the page has loaded — messages are
/// queued until it signals ready.
/// </para>
/// </summary>
public interface IGraphViewService
{
    /// <summary>True once the page has loaded and acknowledged the bridge.</summary>
    bool IsReady { get; }

    /// <summary>Raised on the UI thread when the page finishes loading.</summary>
    event EventHandler? Ready;

    /// <summary>A node was clicked. The shell responds by loading its details.</summary>
    event EventHandler<long>? NodeSelected;

    /// <summary>A node was double-clicked, meaning "make this the new focus".</summary>
    event EventHandler<long>? NodeActivated;

    /// <summary>An expand badge was clicked.</summary>
    event EventHandler<GraphExpandRequest>? ExpandRequested;

    /// <summary>An edge was clicked. The shell answers with <see cref="ShowEdgeDetailsAsync"/>.</summary>
    event EventHandler<GraphEdgeSelection>? EdgeSelected;

    /// <summary>An edge was double-clicked, or one of its call-site rows was chosen.</summary>
    event EventHandler<GraphEdgeActivation>? EdgeActivated;

    /// <summary>
    /// The legend's font size was changed from the page. Carries the new size in CSS
    /// pixels so the shell can store it; the page has already applied it.
    /// </summary>
    event EventHandler<double>? LegendSizeChanged;

    /// <summary>
    /// The node detail lines were toggled from the page's legend. Carries the new state so
    /// the shell can store it; the page has already relabelled.
    /// </summary>
    event EventHandler<bool>? NodeDetailsChanged;

    /// <summary>Raised when the page reports a script error, so it reaches the log.</summary>
    event EventHandler<string>? ScriptError;

    /// <summary>
    /// A treemap tile or a wheel arc was opened. The payload is a workspace-relative path,
    /// empty for the workspace root.
    /// </summary>
    event EventHandler<string>? DrillRequested;

    /// <summary>Replaces the whole graph with a new fragment.</summary>
    Task ShowGraphAsync(GraphPayload payload, GraphLayout layout);

    /// <summary>Adds nodes and edges to the current graph without disturbing existing positions.</summary>
    Task MergeGraphAsync(GraphPayload payload, long expandedSymbolId, GraphDirection direction);

    /// <summary>Re-runs the layout without changing the data.</summary>
    Task SetLayoutAsync(GraphLayout layout);

    /// <summary>Centres and highlights a node that is already on screen.</summary>
    Task FocusNodeAsync(long symbolId);

    /// <summary>Empties the canvas and shows a message in its place.</summary>
    Task ClearAsync(string message);

    /// <summary>Switches the page palette to match the shell.</summary>
    Task SetThemeAsync(AppTheme theme);

    /// <summary>Sets the legend's font size, in CSS pixels.</summary>
    Task SetLegendSizeAsync(double size);

    /// <summary>Shows or hides the parameter and descriptor lines on every node.</summary>
    Task SetNodeDetailsAsync(bool show);

    /// <summary>Shows one of the five views. The data for it is sent separately.</summary>
    Task SetViewModeAsync(GraphViewMode mode);

    /// <summary>Replaces the composition inspector. Null empties it.</summary>
    Task ShowCompositionAsync(CompositionPayload? payload);

    /// <summary>Replaces the traced routes. Null returns the view to its prompt.</summary>
    Task ShowPathsAsync(PathsPayload? payload);

    /// <summary>Replaces the treemap with one level of the tree.</summary>
    Task ShowTreemapAsync(TreemapPayload? payload);

    /// <summary>Replaces the dependency wheel.</summary>
    Task ShowWheelAsync(WheelPayload? payload);

    /// <summary>
    /// Asks the page to export the current graph. The answer arrives on
    /// <see cref="ExportProduced"/>, because the render lives on the other side of the
    /// bridge and only it knows what is actually on the canvas.
    /// </summary>
    Task RequestExportAsync(GraphExportFormat format);

    /// <summary>Raised on the UI thread when the page answers an export request.</summary>
    event EventHandler<GraphExportResult>? ExportProduced;

    /// <summary>
    /// Answers an <see cref="EdgeSelected"/> with the per-call-site rows behind the merged
    /// edge. Fetched on demand rather than shipped with every fragment, because the list
    /// is only worth reading once its edge is the one being asked about.
    /// </summary>
    Task ShowEdgeDetailsAsync(string edgeId, IReadOnlyList<EdgeCallSite> sites);
}
