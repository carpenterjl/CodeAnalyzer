using CodeAnalyzer.Cli.Output;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// The honesty sentences are load-bearing output, not decoration — these tests pin the
/// exact distinctions: gave-up is not no-route, capped is not complete.
/// </summary>
public class TerseFormatterTests
{
    private static readonly LocatedSymbol From = new(1, "alpha", SymbolKind.Function, "(void)", "a.c", 1);
    private static readonly LocatedSymbol To = new(2, "omega", SymbolKind.Function, "(void)", "b.c", 9);

    [Fact]
    public void AnExhaustedEmptySearchNeverReadsAsNoRoute()
    {
        var trace = new PathTrace
        {
            FromId = 1, ToId = 2, FromExists = true, ToExists = true,
            SearchExhausted = true,
        };

        var text = TerseFormatter.Trace(From, To, trace);

        Assert.Contains("NOT proven", text);
        Assert.Contains("--depth", text);
        Assert.DoesNotContain("does not reach", text);
    }

    [Fact]
    public void ACompletedEmptySearchSaysTheSearchCompleted()
    {
        var trace = new PathTrace
        {
            FromId = 1, ToId = 2, FromExists = true, ToExists = true,
            SearchExhausted = false,
        };

        var text = TerseFormatter.Trace(From, To, trace);

        Assert.Contains("does not reach", text);
        Assert.DoesNotContain("NOT proven", text);
    }

    [Fact]
    public void ATruncatedRouteListSaysMoreExist()
    {
        var trace = new PathTrace
        {
            FromId = 1, ToId = 2, FromExists = true, ToExists = true,
            Nodes = [new PathNode(1, "alpha", SymbolKind.Function, "a.c", 1),
                     new PathNode(2, "omega", SymbolKind.Function, "b.c", 9)],
            Links = [new PathLink(1, 2, ReferenceKind.Call, EdgeConfidence.Unique, 3)],
            Routes = [new long[] { 1, 2 }],
            Length = 1,
            Truncated = true,
        };

        var text = TerseFormatter.Trace(From, To, trace);

        Assert.Contains("alpha -> omega", text);
        Assert.Contains("more routes of this length exist", text);
    }

    [Fact]
    public void AnAmbiguousHopIsMarkedAndTheMarkExplained()
    {
        var trace = new PathTrace
        {
            FromId = 1, ToId = 2, FromExists = true, ToExists = true,
            Nodes = [new PathNode(1, "alpha", SymbolKind.Function, "a.c", 1),
                     new PathNode(2, "omega", SymbolKind.Function, "b.c", 9)],
            Links = [new PathLink(1, 2, ReferenceKind.Call, EdgeConfidence.Ambiguous, 3)],
            Routes = [new long[] { 1, 2 }],
            Length = 1,
        };

        var text = TerseFormatter.Trace(From, To, trace);

        Assert.Contains("-~>", text);
        Assert.Contains("one of several name matches", text);
    }

    [Fact]
    public void TheConfidenceFooterOnlyAppearsWhenSomethingWasUncertain()
    {
        var certain = new RelatedSymbol(3, "callee", SymbolKind.Function, "c.c", 4,
            ReferenceKind.Call, EdgeConfidence.Unique);
        var uncertain = certain with { Confidence = EdgeConfidence.Ambiguous };

        var certainText = TerseFormatter.Related(From, [certain], "callees", 100, null);
        var uncertainText = TerseFormatter.Related(From, [uncertain], "callees", 100, null);

        Assert.DoesNotContain("name match", certainText);
        Assert.Contains("~ = one of several name matches", uncertainText);
    }

    /// <summary>
    /// A call site line rebuilds the source in source order. The name is what keeps the
    /// receiver's dot attached to something: a use carries no arguments, so without it a
    /// site read <c>:632 SymbolKind.</c> — and a bare use read <c>:454</c> and nothing.
    /// </summary>
    [Fact]
    public void ASiteLineReadsLikeTheSourceItCameFrom()
    {
        var entry = new RelatedSymbol(3, "reader", SymbolKind.Method, "c.cs", 4,
            ReferenceKind.Use, EdgeConfidence.Unique);
        var sites = new Dictionary<long, List<EdgeCallSite>>
        {
            [3] = [new EdgeCallSite(632, null, EdgeConfidence.Unique, "SymbolKind")],
        };

        var text = TerseFormatter.Related(From, [entry], TerseFormatter.Callers, 100, sites);

        // Asking for callers, every site names the focus symbol.
        Assert.Contains(":632 SymbolKind.alpha", text);
        Assert.DoesNotContain("SymbolKind.\n", text);
    }

    /// <summary>
    /// The other direction names the other end: a callee site is written against the
    /// entry, not against the symbol being asked about.
    /// </summary>
    [Fact]
    public void ACalleeSiteNamesTheCalleeAndCarriesItsArguments()
    {
        var entry = new RelatedSymbol(3, "IndexAsync", SymbolKind.Method, "c.cs", 4,
            ReferenceKind.Call, EdgeConfidence.Ambiguous);
        var sites = new Dictionary<long, List<EdgeCallSite>>
        {
            [3] = [new EdgeCallSite(88, "(selection, store)", EdgeConfidence.Ambiguous, "orchestrator")],
        };

        var text = TerseFormatter.Related(From, [entry], TerseFormatter.Callees, 100, sites);

        Assert.Contains(":88 orchestrator.IndexAsync(selection, store)~", text);
    }

    /// <summary>A bare reference has no receiver, and must not grow a stray separator.</summary>
    [Fact]
    public void ASiteWithNoReceiverPrintsTheNameAlone()
    {
        var entry = new RelatedSymbol(3, "reader", SymbolKind.Method, "c.cs", 4,
            ReferenceKind.Use, EdgeConfidence.Unique);
        var sites = new Dictionary<long, List<EdgeCallSite>>
        {
            [3] = [new EdgeCallSite(454, null, EdgeConfidence.Unique)],
        };

        var text = TerseFormatter.Related(From, [entry], TerseFormatter.Callers, 100, sites);

        Assert.Contains(":454 alpha", text);
        Assert.DoesNotContain(".alpha", text);
    }

    [Fact]
    public void AMultiLineParameterListStaysOnOneLine()
    {
        var symbol = new LocatedSymbol(7, "wide", SymbolKind.Method,
            "(\n    int a,\n    int b)", "w.cs", 2);

        var line = TerseFormatter.SymbolLine(symbol);

        Assert.DoesNotContain('\n', line);
        Assert.Contains("( int a, int b)", line);
    }

    [Fact]
    public void OutputUsesBareLineFeeds()
    {
        var trace = new PathTrace
        {
            FromId = 1, ToId = 2, FromExists = true, ToExists = true,
            SearchExhausted = true,
        };

        Assert.DoesNotContain('\r', TerseFormatter.Trace(From, To, trace));
    }
}
