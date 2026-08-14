using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Runs the real pipeline and checks the fragment the graph view receives.
/// <para>
/// The unit tests over <see cref="GraphPayloadBuilder"/> use hand-built fragments; this
/// class exists to catch the other half, where the counts come out of SQL. The two must
/// agree, because the difference between them is what the expand buttons offer.
/// </para>
/// </summary>
public class GraphFragmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-graph", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceSession _session;

    public GraphFragmentTests()
    {
        Directory.CreateDirectory(_root);

        // uart_init is called from three places, one of which calls it twice. Its own body
        // references a macro, a struct type and another function.
        WriteFile("drivers/uart.c", """
            #define UART_BAUD 115200

            struct uart_state { int baud; };

            static int uart_configure(int baud) {
                return baud == UART_BAUD;
            }

            int uart_init(struct uart_state *state) {
                state->baud = UART_BAUD;
                return uart_configure(UART_BAUD);
            }
            """);

        WriteFile("app/main.c", """
            int uart_init(struct uart_state *state);

            int main(void) {
                uart_init(0);
                uart_init(0);
                return 0;
            }
            """);

        WriteFile("app/board.c", """
            int uart_init(struct uart_state *state);

            void board_init(void) {
                uart_init(0);
            }
            """);

        WriteFile("test/harness.c", """
            int uart_init(struct uart_state *state);

            void test_uart(void) {
                uart_init(0);
            }
            """);

        _session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
    }

    public void Dispose()
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
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<long> IndexAndFindAsync(string name)
    {
        await _session.IndexAsync([string.Empty]);

        var hit = _session.Search
            .Search(name)
            .FirstOrDefault(h => h.Name == name && h.RelativePath == "drivers/uart.c");

        Assert.True(hit is not null, $"'{name}' was not indexed as a definition in drivers/uart.c");
        return hit!.SymbolId;
    }

    [Fact]
    public async Task StoredTotalsMatchTheEdgesTheFragmentActuallyContains()
    {
        var symbolId = await IndexAndFindAsync("uart_init");
        var fragment = _session.Graph.GetNeighbourhood(symbolId);

        var focus = fragment.Nodes.Single(n => n.Id == symbolId);

        // Well under the per-direction cap, so the fragment holds everything there is.
        // The totals therefore have to agree exactly with the edges present; if they were
        // computed over a different key the page would offer an expansion that adds nothing.
        var incoming = fragment.Edges.Count(e => e.TargetId == symbolId);
        var outgoing = fragment.Edges.Count(e => e.SourceId == symbolId);

        Assert.Equal(incoming, focus.CallerCount);
        Assert.Equal(outgoing, focus.CalleeCount);
    }

    [Fact]
    public async Task ARepeatedCallCountsOnceRatherThanOncePerCallSite()
    {
        var symbolId = await IndexAndFindAsync("uart_init");
        var fragment = _session.Graph.GetNeighbourhood(symbolId, GraphDirection.Callers);

        var callers = fragment.Edges
            .Where(e => e.TargetId == symbolId)
            .Select(e => e.SourceId)
            .ToList();

        // main() calls uart_init twice; that is one link, not two.
        Assert.Equal(callers.Count, callers.Distinct().Count());

        var callerNames = callers
            .Select(id => fragment.Nodes.Single(n => n.Id == id).Name)
            .ToList();
        Assert.Contains("main", callerNames);
        Assert.Contains("board_init", callerNames);
        Assert.Contains("test_uart", callerNames);
    }

    [Fact]
    public async Task ExpandingTwiceReturnsTheSameFragment()
    {
        var symbolId = await IndexAndFindAsync("uart_init");

        var first = _session.Graph.GetNeighbourhood(symbolId);
        var second = _session.Graph.GetNeighbourhood(symbolId);

        Assert.Equal(
            first.Edges.Select(e => (e.SourceId, e.TargetId, e.Kind)).OrderBy(t => t.Item1).ThenBy(t => t.Item2),
            second.Edges.Select(e => (e.SourceId, e.TargetId, e.Kind)).OrderBy(t => t.Item1).ThenBy(t => t.Item2));
    }

    [Fact]
    public async Task TheFragmentReachesTheRendererWithItsFactsIntact()
    {
        var symbolId = await IndexAndFindAsync("uart_init");
        var payload = GraphPayloadBuilder.Build(_session.Graph.GetNeighbourhood(symbolId));

        var focus = payload.Nodes.Single(n => n.Id == symbolId.ToString());
        Assert.True(focus.IsFocus);
        Assert.Equal("function", focus.Group);

        // The macro's literal value is a fact worth seeing on the node itself.
        var macro = payload.Nodes.SingleOrDefault(n => n.Name == "UART_BAUD");
        Assert.True(macro is not null, "the macro uart_init references should be in its neighbourhood");
        Assert.Equal("115200", macro!.Value);

        // Every edge declares how far the resolver trusted it.
        Assert.All(payload.Edges, e =>
            Assert.Contains(e.Confidence, new[] { "unique", "ambiguous", "weak" }));
        Assert.All(payload.Edges, e => Assert.False(string.IsNullOrWhiteSpace(e.ConfidenceLabel)));
    }

    [Fact]
    public async Task AnAmbiguousNameIsFlaggedRatherThanResolvedToOneDefinition()
    {
        // Two files define the same helper. Nothing in the syntax says which one the call
        // in drivers/uart.c means, so the edges must not claim certainty.
        WriteFile("drivers/dup_a.c", "int shared_helper(void) { return 1; }");
        WriteFile("drivers/dup_b.c", "int shared_helper(void) { return 2; }");
        WriteFile("drivers/caller.c", """
            int shared_helper(void);

            int uses_helper(void) {
                return shared_helper();
            }
            """);

        await _session.IndexAsync([string.Empty]);

        var hit = _session.Search.Search("uses_helper").Single(h => h.Name == "uses_helper");
        var payload = GraphPayloadBuilder.Build(
            _session.Graph.GetNeighbourhood(hit.SymbolId, GraphDirection.Callees));

        var helperEdges = payload.Edges
            .Where(e => payload.Nodes.Any(n => n.Id == e.Target && n.Name == "shared_helper"))
            .ToList();

        Assert.NotEmpty(helperEdges);
        Assert.All(helperEdges, e =>
        {
            Assert.Equal("ambiguous", e.Confidence);
            Assert.Equal("one of several name matches", e.ConfidenceLabel);
            Assert.True(e.Candidates > 1, $"expected several candidates, saw {e.Candidates}");
        });
    }
}
