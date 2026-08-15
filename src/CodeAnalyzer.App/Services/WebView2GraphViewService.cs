using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodeAnalyzer.App.Services;

/// <summary>
/// Hosts the bundled graph page in WebView2 and moves JSON messages across the boundary.
/// <para>
/// The page is served from a virtual host mapped to the local <c>wwwroot</c> folder, so it
/// loads over an <c>https://</c> origin without any network access. External navigation and
/// new windows are refused outright: this control renders our own bundle and nothing else.
/// </para>
/// </summary>
public sealed class WebView2GraphViewService(IUiDispatcher dispatcher, ILogger<WebView2GraphViewService> logger)
    : IGraphViewService, IDisposable
{
    /// <summary>Virtual host the bundle is served from. Must match the CSP in index.html.</summary>
    private const string VirtualHost = "codeanalyzer.graph";

    private const string StartUrl = $"https://{VirtualHost}/index.html";

    /// <summary>
    /// Messages posted before the page signalled ready. Bounded so a page that never loads
    /// cannot grow this without limit; the graph is fully re-sent on every focus anyway.
    /// </summary>
    private const int MaxQueuedMessages = 16;

    private readonly List<string> _pending = [];

    private WebView2? _view;
    private CoreWebView2? _core;
    private long _sequence;

    public bool IsReady { get; private set; }

    public event EventHandler? Ready;
    public event EventHandler<long>? NodeSelected;
    public event EventHandler<long>? NodeActivated;
    public event EventHandler<GraphExpandRequest>? ExpandRequested;
    public event EventHandler<GraphEdgeSelection>? EdgeSelected;
    public event EventHandler<GraphEdgeActivation>? EdgeActivated;
    public event EventHandler<IoStubSelection>? IoStubSelected;
    public event EventHandler<double>? LegendSizeChanged;
    public event EventHandler<bool>? NodeDetailsChanged;
    public event EventHandler<string>? ScriptError;
    public event EventHandler<string>? DrillRequested;
    public event EventHandler<GraphExportResult>? ExportProduced;

    /// <summary>
    /// Called from the view's code-behind once the control exists. Initialisation is async
    /// and awaited by the caller, so the UI thread is never blocked on the runtime starting.
    /// </summary>
    public async Task AttachAsync(WebView2 view)
    {
        _view = view;

        // Keep the browser profile inside our own app data rather than next to the exe,
        // which may sit in a read-only location.
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeAnalyzer",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment
            .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder)
            .ConfigureAwait(true);

        await view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        _core = view.CoreWebView2;

        var assetRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            assetRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        var settings = _core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        _core.WebMessageReceived += OnWebMessageReceived;

        // The bundle is entirely local. Anything that tries to leave it is a bug or an
        // injected link, and either way it must not be followed.
        _core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Blocked graph page navigation to {Uri}", e.Uri);
                e.Cancel = true;
            }
        };

        _core.NewWindowRequested += (_, e) =>
        {
            logger.LogWarning("Blocked graph page popup to {Uri}", e.Uri);
            e.Handled = true;
        };

        _core.ProcessFailed += (_, e) =>
        {
            IsReady = false;
            logger.LogError("WebView2 process failed: {Kind}", e.ProcessFailedKind);
        };

        _core.Navigate(StartUrl);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unreadable message from graph page");
            return;
        }

        try
        {
            var message = JsonNode.Parse(json)?.AsObject();
            var type = message?["type"]?.GetValue<string>();
            if (type is null)
            {
                return;
            }

            var payload = message?["payload"]?.AsObject();

            switch (type)
            {
                case "ready":
                    IsReady = true;
                    FlushPending();
                    Ready?.Invoke(this, EventArgs.Empty);
                    break;

                case "nodeSelected" when TryGetId(payload, out var selectedId):
                    NodeSelected?.Invoke(this, selectedId);
                    break;

                case "nodeActivated" when TryGetId(payload, out var activatedId):
                    NodeActivated?.Invoke(this, activatedId);
                    break;

                case "expandRequested" when TryGetId(payload, out var expandId):
                    var direction = payload?["direction"]?.GetValue<string>() switch
                    {
                        "callers" => GraphDirection.Callers,
                        "callees" => GraphDirection.Callees,
                        _ => GraphDirection.Both,
                    };
                    ExpandRequested?.Invoke(this, new GraphExpandRequest(expandId, direction));
                    break;

                case "edgeSelected":
                    if (TryGetLong(payload?["source"], out var edgeSourceId)
                        && TryGetLong(payload?["target"], out var edgeTargetId)
                        && payload?["kindId"]?.GetValue<int>() is { } kindId)
                    {
                        EdgeSelected?.Invoke(this, new GraphEdgeSelection(
                            edgeSourceId,
                            edgeTargetId,
                            (ReferenceKind)kindId,
                            payload?["edgeId"]?.GetValue<string>() ?? string.Empty));
                    }
                    break;

                case "edgeActivated":
                    if (TryGetLong(payload?["source"], out var activatedSourceId)
                        && payload?["line"]?.GetValue<int>() is { } line)
                    {
                        EdgeActivated?.Invoke(this, new GraphEdgeActivation(activatedSourceId, line));
                    }
                    break;

                case "ioStubSelected":
                    if (payload?["name"]?.GetValue<string>() is { } stubName)
                    {
                        var refIds = new List<long>();
                        if (payload["refIds"] is JsonArray refArray)
                        {
                            foreach (var raw in refArray)
                            {
                                if (TryGetLong(raw, out var refId))
                                {
                                    refIds.Add(refId);
                                }
                            }
                        }

                        IoStubSelected?.Invoke(this, new IoStubSelection(
                            stubName,
                            payload["directionLabel"]?.GetValue<string>() ?? "",
                            payload["source"]?.GetValue<string>() ?? "",
                            payload["gateNote"]?.GetValue<string>(),
                            refIds));
                    }
                    break;

                case "legendSizeChanged":
                    if (payload?["size"]?.GetValue<double>() is { } legendSize)
                    {
                        LegendSizeChanged?.Invoke(this, legendSize);
                    }
                    break;

                case "nodeDetailsChanged":
                    if (payload?["show"]?.GetValue<bool>() is { } showDetails)
                    {
                        NodeDetailsChanged?.Invoke(this, showDetails);
                    }
                    break;

                case "treemapDrill":
                    // An absent path means the workspace root, which is a real destination.
                    DrillRequested?.Invoke(this, payload?["path"]?.GetValue<string>() ?? string.Empty);
                    break;

                case "exportResult":
                    var format = payload?["format"]?.GetValue<string>() == "json"
                        ? GraphExportFormat.Json
                        : GraphExportFormat.Png;
                    var data = payload?["data"]?.GetValue<string>();
                    ExportProduced?.Invoke(this, new GraphExportResult(format, data));
                    break;

                case "error":
                    var text = payload?["message"]?.GetValue<string>() ?? "unknown";
                    logger.LogError("Graph page error: {Message}", text);
                    ScriptError?.Invoke(this, text);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Malformed message from graph page: {Json}", json);
        }
    }

    private static bool TryGetId(JsonObject? payload, out long id) =>
        TryGetLong(payload?["id"], out id);

    private static bool TryGetLong(JsonNode? raw, out long id)
    {
        id = 0;
        if (raw is null)
        {
            return false;
        }

        // Ids cross the boundary as strings because JavaScript numbers cannot hold the
        // full 64-bit range, but be tolerant of a numeric one.
        return raw.GetValueKind() switch
        {
            JsonValueKind.String => long.TryParse(raw.GetValue<string>(), out id),
            JsonValueKind.Number => raw.AsValue().TryGetValue(out id),
            _ => false,
        };
    }

    // ---- Outbound ---------------------------------------------------------

    public Task ShowGraphAsync(GraphPayload payload, GraphLayout layout) =>
        PostAsync("setGraph", new SetGraphMessage(payload, LayoutName(layout)));

    public Task MergeGraphAsync(GraphPayload payload, long expandedSymbolId, GraphDirection direction) =>
        PostAsync("mergeGraph", new MergeGraphMessage(
            payload,
            expandedSymbolId.ToString(),
            DirectionName(direction)));

    public Task SetLayoutAsync(GraphLayout layout) =>
        PostAsync("setLayout", new LayoutMessage(LayoutName(layout)));

    public Task FocusNodeAsync(long symbolId) =>
        PostAsync("focus", new IdMessage(symbolId.ToString()));

    public Task ClearAsync(string message) =>
        PostAsync("clear", new MessageOnly(message));

    public Task SetThemeAsync(AppTheme theme) =>
        PostAsync("setTheme", new ThemeMessage(theme == AppTheme.Dark ? "dark" : "light"));

    public Task SetLegendSizeAsync(double size) =>
        PostAsync("setLegendSize", new LegendSizeMessage(size));

    public Task SetNodeDetailsAsync(bool show) =>
        PostAsync("setNodeDetails", new NodeDetailsMessage(show));

    public Task SetViewModeAsync(GraphViewMode mode) =>
        PostAsync("setViewMode", new ViewModeMessage(ViewModeName(mode)));

    public Task ShowCompositionAsync(CompositionPayload? payload) =>
        PostAsync("setComposition", new CompositionMessage(payload));

    public Task ShowPathsAsync(PathsPayload? payload) =>
        PostAsync("setPaths", new PathsMessage(payload));

    public Task ShowTreemapAsync(TreemapPayload? payload) =>
        PostAsync("setTreemap", new TreemapMessage(payload));

    public Task ShowWheelAsync(WheelPayload? payload) =>
        PostAsync("setWheel", new WheelMessage(payload));

    public Task ShowBoundariesAsync(BoundariesPayload? payload) =>
        PostAsync("setBoundaries", new BoundariesMessage(payload));

    public Task RequestExportAsync(GraphExportFormat format) =>
        PostAsync("exportView", new ExportMessage(format == GraphExportFormat.Json ? "json" : "png"));

    public Task ShowEdgeDetailsAsync(string edgeId, IReadOnlyList<EdgeCallSite> sites) =>
        PostAsync("edgeDetails", new EdgeDetailsMessage(
            edgeId,
            sites.Select(site => new EdgeSiteWire(
                site.Line,
                site.ArgumentText,
                KindLabels.For(site.Confidence))).ToList()));

    /// <summary>Wire names for the view modes. The page keys its sections off these.</summary>
    private static string ViewModeName(GraphViewMode mode) => mode switch
    {
        GraphViewMode.Composition => "composition",
        GraphViewMode.Paths => "paths",
        GraphViewMode.Treemap => "treemap",
        GraphViewMode.Wheel => "wheel",
        GraphViewMode.Boundaries => "boundaries",
        _ => "graph",
    };

    private static string LayoutName(GraphLayout layout) =>
        layout == GraphLayout.Hierarchy ? "hierarchy" : "force";

    private static string DirectionName(GraphDirection direction) => direction switch
    {
        GraphDirection.Callers => "callers",
        GraphDirection.Callees => "callees",
        _ => "both",
    };

    private Task PostAsync<T>(string type, T payload)
    {
        var envelope = new Envelope<T>(type, Interlocked.Increment(ref _sequence), payload);
        var json = JsonSerializer.Serialize(envelope, GraphPayloadBuilder.JsonOptions);

        // Serialisation happens on the calling thread; only the post itself needs the UI one.
        return dispatcher.InvokeAsync(() => Send(json));
    }

    private void Send(string json)
    {
        if (_core is null)
        {
            return;
        }

        if (!IsReady)
        {
            if (_pending.Count >= MaxQueuedMessages)
            {
                _pending.RemoveAt(0);
            }

            _pending.Add(json);
            return;
        }

        try
        {
            _core.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            // A message that fails to post must not take the app down with it.
            logger.LogWarning(ex, "Failed to post message to graph page");
        }
    }

    private void FlushPending()
    {
        foreach (var json in _pending)
        {
            try
            {
                _core?.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to flush queued message to graph page");
            }
        }

        _pending.Clear();
    }

    public void Dispose()
    {
        if (_core is not null)
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
        }

        _view?.Dispose();
        _view = null;
        _core = null;
        IsReady = false;
    }

    // ---- Wire shapes ------------------------------------------------------

    private sealed record Envelope<T>(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("seq")] long Seq,
        [property: JsonPropertyName("payload")] T Payload);

    private sealed record SetGraphMessage(
        [property: JsonPropertyName("graph")] GraphPayload Graph,
        [property: JsonPropertyName("layout")] string Layout);

    private sealed record MergeGraphMessage(
        [property: JsonPropertyName("graph")] GraphPayload Graph,
        [property: JsonPropertyName("expandedId")] string ExpandedId,
        [property: JsonPropertyName("direction")] string Direction);

    private sealed record LayoutMessage([property: JsonPropertyName("layout")] string Layout);

    private sealed record IdMessage([property: JsonPropertyName("id")] string Id);

    private sealed record MessageOnly([property: JsonPropertyName("message")] string Message);

    private sealed record ThemeMessage([property: JsonPropertyName("theme")] string Theme);

    private sealed record ViewModeMessage([property: JsonPropertyName("mode")] string Mode);

    private sealed record LegendSizeMessage([property: JsonPropertyName("size")] double Size);

    private sealed record NodeDetailsMessage([property: JsonPropertyName("show")] bool Show);

    private sealed record ExportMessage([property: JsonPropertyName("format")] string Format);

    private sealed record EdgeDetailsMessage(
        [property: JsonPropertyName("edgeId")] string EdgeId,
        [property: JsonPropertyName("sites")] IReadOnlyList<EdgeSiteWire> Sites);

    private sealed record EdgeSiteWire(
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("argText")] string? ArgText,
        [property: JsonPropertyName("confidenceLabel")] string ConfidenceLabel);

    // A null payload is dropped by the serialiser's WhenWritingNull rule, which is exactly
    // what the page reads as "there is nothing to show here yet".
    private sealed record CompositionMessage(
        [property: JsonPropertyName("composition")] CompositionPayload? Composition);

    private sealed record PathsMessage([property: JsonPropertyName("paths")] PathsPayload? Paths);

    private sealed record TreemapMessage([property: JsonPropertyName("treemap")] TreemapPayload? Treemap);

    private sealed record WheelMessage([property: JsonPropertyName("wheel")] WheelPayload? Wheel);

    private sealed record BoundariesMessage(
        [property: JsonPropertyName("boundaries")] BoundariesPayload? Boundaries);
}
