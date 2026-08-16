using CodeAnalyzer.Cli.Output;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Storage;
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

    private static FileErrorRecord Stumbled(string path, int line, string? text) =>
        new(path, "C#", Message: null, SymbolCount: 12, Line: line, Text: text);

    [Fact]
    public void TheTallyLeadsBecauseItIsUsuallyTheWholeAnswer()
    {
        // The point of the command. Fifty file names read as "your workspace is a mess";
        // the same fifty rolled up to one construct read as "your grammar is old", which
        // is the true one. The tally must therefore come before the list, and must count
        // every file rather than only the ones the list had room for.
        var report = new ParseErrorReport(196, [
            Stumbled("a.cs", 10, "[]"),
            Stumbled("b.cs", 20, "[]"),
            Stumbled("c.cs", 30, "<Grid.RowDefinitions>"),
        ]);

        var text = TerseFormatter.ParseErrors(report, limit: 1);

        Assert.Contains("3 of 196", text);
        Assert.Contains("2 × C#", text);
        Assert.True(
            text.IndexOf("what it stopped at", StringComparison.Ordinal) < text.IndexOf("a.cs", StringComparison.Ordinal),
            "the tally has to precede the file list");
    }

    [Fact]
    public void ACappedListSaysHowManyItDidNotShow()
    {
        var report = new ParseErrorReport(196, [
            Stumbled("a.cs", 1, "[]"), Stumbled("b.cs", 2, "[]"), Stumbled("c.cs", 3, "[]"),
        ]);

        var text = TerseFormatter.ParseErrors(report, limit: 2);

        Assert.Contains("1 more files not shown", text);
        Assert.DoesNotContain("c.cs", text);
    }

    [Fact]
    public void AFileWithNoQuotableTextIsStillCountedAndPlaced()
    {
        // A token the grammar expected and did not find has a position and no extent.
        // It must not vanish from the tally for want of something to quote.
        var report = new ParseErrorReport(2, [Stumbled("a.cs", 7, null)]);

        var text = TerseFormatter.ParseErrors(report, limit: 10);

        Assert.Contains("a token the grammar expected", text);
        Assert.Contains("a.cs:7", text);
    }

    [Fact]
    public void AWorkspaceWithNothingToReportSaysSoWithoutAnEmptyTable()
    {
        Assert.Equal(
            "all 196 indexed files parsed cleanly",
            TerseFormatter.ParseErrors(new ParseErrorReport(196, []), limit: 10));
    }

    [Fact]
    public void AHardFailureShowsItsMessageInsteadOfASymbolCount()
    {
        var report = new ParseErrorReport(2, [
            new("broken.cs", "C#", "the parser returned no tree", SymbolCount: 0),
        ]);

        var text = TerseFormatter.ParseErrors(report, limit: 10);

        Assert.Contains("the parser returned no tree", text);
        Assert.DoesNotContain("0 indexed", text);
    }

    private static IndexStats SomeStats(int unresolved = 10, int ambiguous = 5) => new(
        TotalFiles: 3,
        FilesByLanguage: [new("C#", 2), new("C", 1)],
        ImperfectFiles: 1,
        TotalSymbols: 40,
        SymbolsByKind: [new("Method", 30), new("Field", 10)],
        TotalRefs: 100,
        RefsWithReceiver: 20,
        RefsWithArgs: 30,
        RefsResolvedUniquely: 100 - unresolved - ambiguous,
        RefsAmbiguous: ambiguous,
        RefsUnresolved: unresolved,
        MeanCandidatesWhenAmbiguous: 2.5,
        RefsByKind: [new("Call", 60, 40, 5, 15), new("Use", 40, 25, 0, 15)],
        RefsByLanguage: [new("C#", 70, 50, 5, 15), new("C", 30, 15, 0, 15)],
        TotalEdges: 95,
        EdgesByConfidence: [new("Unique", 85), new("Ambiguous", 10)],
        TotalDeps: 8,
        ResolvedDeps: 3,
        DatabaseBytes: 5 * 1024 * 1024);

    [Fact]
    public void StatsKeepTheUnresolvedCountHonest()
    {
        // Half a workspace's references pointing nowhere reads as a broken index unless
        // the block says what unresolved means. The note must ride with the number.
        var text = TerseFormatter.Stats(SomeStats(unresolved: 50));

        Assert.Contains("unresolved", text);
        Assert.Contains("50.0%", text);
        Assert.Contains("not a defect count", text);
    }

    [Fact]
    public void StatsResolutionTripleSumsToTheReferenceTotal()
    {
        var text = TerseFormatter.Stats(SomeStats(unresolved: 10, ambiguous: 5));

        Assert.Contains("references: 100", text);
        Assert.Contains("resolved uniquely", text);
        Assert.Contains("85", text);
        Assert.Contains("(2.5 candidates each)", text);
    }

    [Fact]
    public void StatsAnnounceAPathScopeAndTheWholeWorkspaceStaysSilent()
    {
        // A scoped report must say so up front, or a reader takes a subtree's numbers for the
        // whole index's. The unscoped report carries no such line — silence is the default.
        Assert.DoesNotContain("scope:", TerseFormatter.Stats(SomeStats()));

        var scoped = TerseFormatter.Stats(SomeStats() with { ScopePath = "src/CodeAnalyzer.Core" });
        Assert.Contains("scope: src/CodeAnalyzer.Core", scoped);
    }

    [Fact]
    public void StatsNameReferenceKindsRatherThanNumberingThem()
    {
        // The whole point of the by-kind table is that a reader can act on a bad row. A
        // row labelled "7" is a lookup in the enum before the reader learns anything.
        var text = TerseFormatter.Stats(SomeStats());

        Assert.Contains("by reference kind", text);
        Assert.Contains("Call", text);
        Assert.DoesNotContain("kind 1", text);
    }

    [Fact]
    public void StatsShowASubsetResolvingWorseThanTheIndexAverage()
    {
        // A kind or language whose unresolved share stands out is the reason to publish
        // the split at all — this is the shape that sent round eight after `new Foo()`.
        var stats = SomeStats() with
        {
            RefsByKind = [new("Call", 60, 40, 5, 15), new("Instantiate", 20, 0, 0, 20)],
        };

        var text = TerseFormatter.Stats(stats);

        Assert.Contains("Instantiate", text);
        Assert.Contains("unres 100.0%", text);
    }

    [Fact]
    public void StatsWithNoAmbiguityDoNotInventACandidateAverage()
    {
        var text = TerseFormatter.Stats(SomeStats(ambiguous: 0));

        Assert.DoesNotContain("candidates each", text);
    }

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

    /// <summary>
    /// A markup extension is stored whole, because the extension is the reference rather
    /// than an argument to one. Prefixing the name there read
    /// <c>SearchBox{StaticResource SearchBox}</c>.
    /// </summary>
    [Fact]
    public void AMarkupExtensionSiteShowsTheExtensionOnce()
    {
        var entry = new RelatedSymbol(3, "SearchBox", SymbolKind.ResourceKey, "Themes/Controls.xaml", 526,
            ReferenceKind.Resource, EdgeConfidence.Unique);
        var sites = new Dictionary<long, List<EdgeCallSite>>
        {
            [3] = [new EdgeCallSite(405, "{StaticResource SearchBox}", EdgeConfidence.Unique, null, "SearchBox")],
        };

        var text = TerseFormatter.Related(From, [entry], TerseFormatter.Callees, 100, sites);

        Assert.Contains(":405 {StaticResource SearchBox}", text);
        Assert.DoesNotContain("SearchBox{StaticResource", text);
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
