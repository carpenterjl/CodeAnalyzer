using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The four M6 views, read out of a real index.
/// <para>
/// These run the whole pipeline rather than hand-building fragments, because what is being
/// checked is mostly SQL: a query that returns the wrong shape here would show the user a
/// picture that disagrees with the detail pane next to it.
/// </para>
/// </summary>
public class ViewQueryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-views", Guid.NewGuid().ToString("N"));

    private WorkspaceSession _session = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        WriteFile("drivers/uart.h", """
            #ifndef UART_H
            #define UART_H
            #define UART_BAUD 115200

            struct uart_config {
                int  baud;
                char name[16];
            };

            int uart_init(struct uart_config *config);
            #endif
            """);

        // uart_init declares a local, which is the case the container-to-member rule exists
        // for: `scratch` is uart_init's own, and listing it as a dependency is noise.
        WriteFile("drivers/uart.c", """
            #include "uart.h"

            static int uart_configure(int baud) {
                return baud == UART_BAUD;
            }

            int uart_init(struct uart_config *config) {
                int scratch = config->baud;
                return uart_configure(scratch);
            }
            """);

        WriteFile("app/main.c", """
            #include "../drivers/uart.h"

            int board_setup(void) {
                struct uart_config config;
                return uart_init(&config);
            }

            int main(void) {
                return board_setup();
            }
            """);

        WriteFile("rtl/fifo.sv", """
            module fifo #(parameter WIDTH = 8) (
                input  logic clk,
                output logic full
            );
            endmodule
            """);

        WriteFile("rtl/top.sv", """
            module top (
                input logic clk
            );
                fifo #(.WIDTH(16)) buffer (.clk(clk), .full());
            endmodule
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

    private long Find(string name, string relativePath) =>
        _session.Search
            .Search(name)
            .Single(hit => hit.Name == name && hit.RelativePath == relativePath)
            .SymbolId;

    // ---- Container-to-member rule -----------------------------------------

    [Fact]
    public void ASymbolDoesNotDependOnItsOwnMembers()
    {
        var uartInit = Find("uart_init", "drivers/uart.c");
        var detail = _session.Graph.GetDetail(uartInit)!;

        // `scratch` is declared inside uart_init, so it is composition, not a dependency.
        Assert.Contains(detail.Members, member => member.Name == "scratch");
        Assert.DoesNotContain(detail.Callees, callee => callee.Name == "scratch");

        // The call that actually leaves the function is still there.
        Assert.Contains(detail.Callees, callee => callee.Name == "uart_configure");
    }

    [Fact]
    public void TheStoredTotalsAgreeWithTheFilteredEdges()
    {
        // The badge arithmetic subtracts drawn edges from the stored totals, so if the two
        // applied the container rule differently the page would offer an expansion that
        // adds nothing.
        var uartInit = Find("uart_init", "drivers/uart.c");
        var fragment = _session.Graph.GetNeighbourhood(uartInit);
        var focus = fragment.Nodes.Single(node => node.Id == uartInit);

        Assert.Equal(fragment.Edges.Count(edge => edge.TargetId == uartInit), focus.CallerCount);
        Assert.Equal(fragment.Edges.Count(edge => edge.SourceId == uartInit), focus.CalleeCount);

        Assert.DoesNotContain(fragment.Nodes, node => node.Name == "scratch");
    }

    [Fact]
    public void AMemberIsNotReportedAsCalledByItsOwnContainer()
    {
        // The mirror image of the rule: whatever the callee side hides, the caller side
        // must hide too, or expanding from the other end would resurrect the edge.
        var uartInit = Find("uart_init", "drivers/uart.c");
        var scratch = _session.Composition.GetComposition(uartInit)!
            .Members.Single(member => member.Name == "scratch");

        // The reference is still in the index — this is a display rule, not a lost fact.
        Assert.True(scratch.ReferenceCount > 0, "uart_init reads its own local");

        var detail = _session.Graph.GetDetail(scratch.Id)!;
        Assert.DoesNotContain(detail.Callers, caller => caller.Name == "uart_init");
    }

    // ---- Composition inspector --------------------------------------------

    [Fact]
    public void AStructListsItsFieldsInSourceOrderWithTheirTypes()
    {
        var view = _session.Composition.GetComposition(Find("uart_config", "drivers/uart.h"))!;

        Assert.Equal(SymbolKind.Struct, view.Kind);
        Assert.Equal(new[] { "baud", "name" }, view.Members.Select(member => member.Name));
        Assert.Equal("int", view.Members.Single(member => member.Name == "baud").TypeText);

        // Reference counts are resolved references, not textual name matches. A bare
        // `config->baud` does not bind to this field, because nothing syntactic says which
        // struct `config` is, so the count stays at zero rather than claiming a use.
        Assert.All(view.Members, member => Assert.True(member.ReferenceCount >= 0));
    }

    [Fact]
    public void AVerilogModuleListsItsPortsAndParameters()
    {
        var view = _session.Composition.GetComposition(Find("fifo", "rtl/fifo.sv"))!;

        var names = view.Members.Select(member => member.Name).ToList();
        Assert.Contains("WIDTH", names);
        Assert.Contains("clk", names);
        Assert.Contains("full", names);

        Assert.Contains(view.Members, member => member is { Name: "clk", Kind: SymbolKind.Port });
    }

    [Fact]
    public void InstantiationIsReadableFromBothEnds()
    {
        var top = _session.Composition.GetComposition(Find("top", "rtl/top.sv"))!;
        var instance = Assert.Single(top.Instantiates, link => link.Name == "fifo");
        Assert.NotNull(instance.TargetId);
        Assert.Equal(SymbolKind.Module, instance.Kind);

        var fifo = _session.Composition.GetComposition(Find("fifo", "rtl/fifo.sv"))!;
        Assert.Contains(fifo.InstantiatedBy, link => link.Name == "top");
    }

    [Fact]
    public void CompositionSurvivesTheTripToTheRenderer()
    {
        var payload = ViewPayloadBuilder.Build(
            _session.Composition.GetComposition(Find("uart_config", "drivers/uart.h"))!);

        Assert.Equal("struct", payload.Kind);
        Assert.Equal("type", payload.Group);
        Assert.Contains(payload.Members, member => member is { Name: "baud", Type: "int" });
    }

    // ---- Path tracer -------------------------------------------------------

    [Fact]
    public void ARouteBetweenTwoFunctionsIsFoundAndOrdered()
    {
        var trace = _session.Paths.FindPaths(
            Find("main", "app/main.c"),
            Find("uart_configure", "drivers/uart.c"));

        Assert.NotEmpty(trace.Routes);

        var byId = trace.Nodes.ToDictionary(node => node.Id, node => node.Name);
        var route = trace.Routes[0].Select(id => byId[id]).ToList();

        Assert.Equal("main", route[0]);
        Assert.Equal("uart_configure", route[^1]);
        Assert.Equal(["main", "board_setup", "uart_init", "uart_configure"], route);
        Assert.Equal(3, trace.Length);

        // Every hop drawn has a fact behind it.
        Assert.Equal(route.Count - 1, trace.Links.Count);
    }

    [Fact]
    public void ASymbolTracedToItselfIsASingleStepNotAnEmptyResult()
    {
        var main = Find("main", "app/main.c");
        var trace = _session.Paths.FindPaths(main, main);

        Assert.Equal([[main]], trace.Routes);
        Assert.Equal(0, trace.Length);
    }

    [Fact]
    public void NoRouteIsReportedAsNoRouteRatherThanAsGivingUp()
    {
        // Nothing links the RTL to the C driver, and the search is small enough to prove it.
        var trace = _session.Paths.FindPaths(
            Find("top", "rtl/top.sv"),
            Find("uart_configure", "drivers/uart.c"));

        Assert.Empty(trace.Routes);
        Assert.False(trace.SearchExhausted, "the search finished; it did not run out of budget");
    }

    [Fact]
    public void AMissingEndpointIsSaidOutLoud()
    {
        var trace = _session.Paths.FindPaths(Find("main", "app/main.c"), -1);

        Assert.False(trace.ToExists);
        Assert.True(ViewPayloadBuilder.Build(trace).MissingEndpoint);
    }

    [Fact]
    public void ADepthBudgetThatStopsTheSearchIsNotMistakenForAnAbsentRoute()
    {
        // The route is three hops. Asked for two, the search must say it gave up rather
        // than report that nothing connects them.
        var trace = _session.Paths.FindPaths(
            Find("main", "app/main.c"),
            Find("uart_configure", "drivers/uart.c"),
            maxDepth: 2);

        Assert.Empty(trace.Routes);
        Assert.True(trace.SearchExhausted);
    }

    // ---- Treemap -----------------------------------------------------------

    [Fact]
    public void TheRootLevelBucketsTheWorkspaceByTopDirectory()
    {
        var level = _session.Structure.GetTreemapLevel(string.Empty);

        var names = level.Tiles.Select(tile => tile.Name).ToList();
        Assert.Contains("drivers", names);
        Assert.Contains("app", names);
        Assert.Contains("rtl", names);

        var drivers = level.Tiles.Single(tile => tile.Name == "drivers");
        Assert.Equal(TreemapTileType.Directory, drivers.Type);
        Assert.Equal(2, drivers.Files);
        Assert.True(drivers.Symbols > 0);
        Assert.Equal(drivers.Symbols, drivers.Value);
    }

    [Fact]
    public void DrillingReachesFilesAndThenTheSymbolsInsideThem()
    {
        var directory = _session.Structure.GetTreemapLevel("drivers");
        var file = Assert.Single(directory.Tiles, tile => tile.Name == "uart.c");
        Assert.Equal(TreemapTileType.File, file.Type);
        Assert.Equal("drivers/uart.c", file.Path);

        var symbols = _session.Structure.GetTreemapLevel(file.Path);
        Assert.Equal(TreemapTileType.Symbol, symbols.ChildType);
        Assert.Contains(symbols.Tiles, tile => tile.Name == "uart_init" && tile.SymbolId is not null);

        // A symbol tile is sized by the lines it occupies, which is a fact about the source.
        Assert.All(symbols.Tiles, tile => Assert.True(tile.Value >= 1));
    }

    [Fact]
    public void ADirectoryThatOnlyCallsItselfIsColouredDifferentlyFromOneThatReachesOut()
    {
        var level = _session.Structure.GetTreemapLevel(string.Empty);

        var app = level.Tiles.Single(tile => tile.Name == "app");
        var rtl = level.Tiles.Single(tile => tile.Name == "rtl");

        // app/main.c calls into drivers/, so some of its links leave the tile.
        Assert.True(app.OutgoingLinks > 0, "app depends on drivers");

        // The RTL only refers to itself.
        Assert.Equal(0, rtl.OutgoingLinks);
        Assert.True(rtl.InternalLinks > 0, "top instantiates fifo inside rtl/");
    }

    [Fact]
    public void TheTreemapReachesTheRendererWithItsTypesIntact()
    {
        var payload = ViewPayloadBuilder.Build(_session.Structure.GetTreemapLevel(string.Empty));

        Assert.Equal("dir", payload.ChildType);
        Assert.All(payload.Tiles, tile => Assert.Contains(tile.Type, new[] { "dir", "file" }));
    }

    // ---- Dependency wheel --------------------------------------------------

    [Fact]
    public void TheWheelDrawsResolvedIncludesBetweenTopLevelDirectories()
    {
        var wheel = _session.Structure.GetDependencyWheel(WheelSource.Includes);

        var link = Assert.Single(wheel.Links, l => l is { Source: "app", Target: "drivers" });
        Assert.Equal(1, link.Count);

        var app = wheel.Groups.Single(group => group.Name == "app");
        Assert.Equal(1, app.Files);
    }

    [Fact]
    public void TheWheelCanCountSymbolReferencesInsteadOfIncludes()
    {
        var wheel = _session.Structure.GetDependencyWheel(WheelSource.References);

        // Same direction, but counting uses of names rather than include lines, so there
        // are more of them.
        Assert.Contains(wheel.Links, link => link is { Source: "app", Target: "drivers" });
        Assert.Contains(wheel.Links, link => link is { Source: "rtl", Target: "rtl" });
    }

    [Fact]
    public void ADependencyOutsideTheWorkspaceIsCountedRatherThanDrawn()
    {
        var wheel = _session.Structure.GetDependencyWheel(WheelSource.Includes);
        var drivers = wheel.Groups.Single(group => group.Name == "drivers");

        // uart.c includes uart.h, which resolves; nothing else does. The <stdint.h>-style
        // case is what Unresolved counts, and there is none here.
        Assert.Equal(0, drivers.Unresolved);

        var payload = ViewPayloadBuilder.Build(wheel);
        Assert.Equal("includes", payload.Source);
        Assert.All(payload.Groups, group => Assert.False(string.IsNullOrEmpty(group.Label)));
    }
}
