using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The M9 read paths, out of a real index: inherit attribution reaching the composition
/// inspector, per-call-site edge details, modifiers on every surface, container context,
/// and the treemap descending through a block-scoped namespace.
/// </summary>
public class M9QueryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-m9", Guid.NewGuid().ToString("N"));

    private WorkspaceSession _session = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        // Block-scoped namespace, deliberately: the treemap drill-through test needs the
        // style where every class is contained by the namespace symbol.
        WriteFile("src/Devices.cs", """
            namespace Hardware
            {
                public interface IDevice
                {
                    int Send(byte[] payload);
                }

                public abstract class DeviceBase
                {
                    protected int Retries;
                }

                public sealed class Radio : DeviceBase, IDevice
                {
                    private static readonly int Limit = 3;

                    public int Send(byte[] payload)
                    {
                        Transmit(payload, Limit);
                        Transmit(payload, 0);
                        return payload.Length;
                    }

                    internal void Transmit(byte[] payload, int retries) { }
                }
            }
            """);

        WriteFile("src/Uses.cs", """
            namespace Hardware
            {
                public class Panel
                {
                    public int Refresh(Radio radio)
                    {
                        return radio.Send(new byte[4]);
                    }
                }
            }
            """);

        _session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
        await _session.IndexAsync([string.Empty]);
    }

    public Task DisposeAsync()
    {
        _session.Dispose();
        WorkspaceCacheCleanup.Delete(_root);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }

        return Task.CompletedTask;
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private long FindSymbol(string name, SymbolKind kind)
    {
        var hit = _session.Search.Search(name)
            .FirstOrDefault(h => h.Name == name && h.Kind == kind);
        Assert.True(hit is not null, $"'{name}' ({kind}) was not indexed");
        return hit!.SymbolId;
    }

    [Fact]
    public void ACompositionListsItsBaseTypesAndTheInterfaceListsItsImplementations()
    {
        // Before M9, inherit references had no owning symbol, so both of these lists came
        // back empty for every C# class — the attribution fix is what fills them.
        var radio = _session.Composition.GetComposition(FindSymbol("Radio", SymbolKind.Class));
        Assert.NotNull(radio);

        var baseNames = radio!.BaseTypes.Select(b => b.Name).ToList();
        Assert.Contains("DeviceBase", baseNames);
        Assert.Contains("IDevice", baseNames);

        // Implements vs extends is derivable from the resolved target's kind — a fact,
        // not an inference, because the base list itself does not say which is which.
        Assert.Equal(SymbolKind.Interface,
            radio.BaseTypes.Single(b => b.Name == "IDevice").Kind);
        Assert.Equal(SymbolKind.Class,
            radio.BaseTypes.Single(b => b.Name == "DeviceBase").Kind);

        var device = _session.Composition.GetComposition(FindSymbol("IDevice", SymbolKind.Interface));
        Assert.Contains(device!.DerivedTypes, d => d.Name == "Radio");
    }

    [Fact]
    public void AnInheritEdgeIsNotAContainerToMemberLink()
    {
        // The base type is not contained by the derived class, so the display rule must
        // keep the edge on the canvas: an interface's neighbourhood showing its
        // implementations is the point of the attribution fix.
        var device = FindSymbol("IDevice", SymbolKind.Interface);
        var fragment = _session.Graph.GetNeighbourhood(device);

        var incoming = fragment.Edges.Where(e => e.TargetId == device).ToList();
        Assert.Contains(incoming, e => e.Kind == ReferenceKind.Inherit);
    }

    [Fact]
    public void AMergedEdgeCountsItsCallSitesAndListsThemOnRequest()
    {
        var send = FindSymbol("Send", SymbolKind.Method);
        var transmit = FindSymbol("Transmit", SymbolKind.Method);

        var fragment = _session.Graph.GetNeighbourhood(send, GraphDirection.Callees);
        var edge = Assert.Single(fragment.Edges,
            e => e.TargetId == transmit && e.Kind == ReferenceKind.Call);

        // Send calls Transmit twice; one drawn edge, two sites, and the count says so.
        Assert.Equal(2, edge.CallSiteCount);

        var sites = _session.Graph.GetEdgeCallSites(send, transmit, ReferenceKind.Call);
        Assert.Equal(2, sites.Count);
        Assert.Equal(sites.OrderBy(s => s.Line).Select(s => s.Line), sites.Select(s => s.Line));
        Assert.Contains(sites, s => s.ArgumentText == "(payload, Limit)");
        Assert.Contains(sites, s => s.ArgumentText == "(payload, 0)");
    }

    [Fact]
    public void ModifiersReachEverySurfaceThatShowsASymbol()
    {
        var radioId = FindSymbol("Radio", SymbolKind.Class);

        var detail = _session.Graph.GetDetail(radioId);
        Assert.Equal("public sealed", detail!.Modifiers);

        var member = detail.Members.Single(m => m.Name == "Transmit");
        Assert.Equal("internal", member.Modifiers);

        var composition = _session.Composition.GetComposition(radioId);
        Assert.Equal("public sealed", composition!.Modifiers);
        Assert.Equal("private static readonly",
            composition.Members.Single(m => m.Name == "Limit").Modifiers);

        var fragment = _session.Graph.GetNeighbourhood(radioId);
        var focus = fragment.Nodes.Single(n => n.Id == radioId);
        Assert.Equal("public sealed", focus.Modifiers);
    }

    [Fact]
    public void GraphNodesAndSearchHitsCarryTheirContainer()
    {
        var send = FindSymbol("Send", SymbolKind.Method);

        var fragment = _session.Graph.GetNeighbourhood(send);
        var focus = fragment.Nodes.Single(n => n.Id == send);
        Assert.Equal("Radio", focus.ContainerName);

        var hit = _session.Search.Search("Transmit").Single(h => h.Name == "Transmit");
        Assert.Equal("Radio", hit.ContainerName);
    }

    [Fact]
    public void TheTreemapDrillsThroughABlockScopedNamespaceToTheClasses()
    {
        var level = _session.Structure.GetTreemapLevel("src/Devices.cs");

        // One namespace tile would say nothing; the classes are what the drill is for.
        var names = level.Tiles.Select(t => t.Name).ToList();
        Assert.Contains("IDevice", names);
        Assert.Contains("DeviceBase", names);
        Assert.Contains("Radio", names);
        Assert.DoesNotContain("Hardware", names);

        // The rollup keys on the same roots: Radio's methods call out, and those links
        // must land on the Radio tile rather than vanish with the namespace.
        var radio = level.Tiles.Single(t => t.Name == "Radio");
        Assert.True(radio.InternalLinks + radio.OutgoingLinks > 0,
            "Radio's rolled-up links should follow it through the namespace");
    }

    [Fact]
    public void PayloadsCarryTheNewFactsToThePage()
    {
        var send = FindSymbol("Send", SymbolKind.Method);
        var payload = GraphPayloadBuilder.Build(_session.Graph.GetNeighbourhood(send));

        var focus = payload.Nodes.Single(n => n.Id == send.ToString());
        Assert.Equal("Radio", focus.Container);
        Assert.Equal("public", focus.Modifiers);

        var call = payload.Edges.Single(e =>
            e.Source == send.ToString() && e.Kind == "call" &&
            payload.Nodes.Single(n => n.Id == e.Target).Name == "Transmit");
        Assert.Equal(2, call.CallSites);
        Assert.Equal((int)ReferenceKind.Call, call.KindId);
    }
}
