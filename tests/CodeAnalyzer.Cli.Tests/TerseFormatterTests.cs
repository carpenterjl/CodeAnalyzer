using CodeAnalyzer.Cli.Output;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Search;
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
    public void AConstructReachingPastItsErrorLineSaysHowFar()
    {
        // "12 indexed" says what survived and nothing about reach — the exact gap that
        // hid a swallow for nine rounds. The extent appears only when it adds anything:
        // a one-line error stays one line, and an old index without the column stays
        // silent rather than guessing.
        var report = new ParseErrorReport(3, [
            new("eats.html", "HTML", null, SymbolCount: 1, Line: 3, Text: "<script>", EndLine: 40,
                LineCount: 900),
            new("narrow.cs", "C#", null, SymbolCount: 12, Line: 7, Text: "= ;", EndLine: 7,
                LineCount: 400),
            new("old.cs", "C#", null, SymbolCount: 5, Line: 9, Text: "[]", EndLine: null),
        ]);

        var text = TerseFormatter.ParseErrors(report, limit: 10);

        Assert.Contains(
            "eats.html:3  1 indexed — the construct it stopped in runs to line 40 of 900", text);
        Assert.Contains("narrow.cs:7  12 indexed", text);
        Assert.DoesNotContain("runs to line 7", text);
        Assert.DoesNotContain("runs to line 9", text);

        // Reaching line 40 of 900 is a clause. None of these reaches the end of its file, so
        // none of them earns the alarm.
        Assert.DoesNotContain("!!", text);
    }

    [Fact]
    public void AConstructThatNeverEndsIsAnAlarmRatherThanALongerClause()
    {
        // Two files with the SAME extent and the same error line. The only difference is the
        // denominator, and it is the whole difference between "the grammar is older than
        // this code" and "the rest of this file was never read". Before line_count existed
        // both printed the identical mild sentence.
        var report = new ParseErrorReport(2, [
            new("swallowed.html", "HTML", null, SymbolCount: 0, Line: 3, Text: "<script>",
                EndLine: 40, LineCount: 40),
            new("survived.html", "HTML", null, SymbolCount: 31, Line: 3, Text: "<script>",
                EndLine: 40, LineCount: 900),
        ]);

        var text = TerseFormatter.ParseErrors(report, limit: 10);

        Assert.Contains("!! 1 of them stopped inside a construct that never ends", text);
        Assert.Contains(
            "swallowed.html:3  0 indexed — !! the construct it stopped in never ends: "
            + "lines 3–40 were consumed as its body and never read", text);
        Assert.Contains(
            "survived.html:3  31 indexed — the construct it stopped in runs to line 40 of 900",
            text);
    }

    [Fact]
    public void AnIndexWrittenBeforeTheDenominatorExistedRaisesNoAlarm()
    {
        // The extent is there and the file length is not, so whether the construct reached
        // the end is unknown. Unknown prints the weaker true sentence rather than the
        // stronger guess — an alarm that fires on a null is one nobody can act on.
        var report = new ParseErrorReport(1, [
            new("old.html", "HTML", null, SymbolCount: 2, Line: 3, Text: "<script>", EndLine: 40),
        ]);

        var text = TerseFormatter.ParseErrors(report, limit: 10);

        Assert.Contains("the construct it stopped in runs to line 40", text);
        Assert.DoesNotContain("of ", text[text.IndexOf("runs to line 40", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("!!", text);
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
        UnresolvedByRule:
        [
            new(UnresolvedRule.External, unresolved - 4),
            new(UnresolvedRule.TooCommon, 2),
            new(UnresolvedRule.ReceiverNotTyped, 1),
            new(UnresolvedRule.OutOfScope, 1),
            new(UnresolvedRule.Unexplained, 0),
        ],
        UnresolvedByRulePerLanguage:
        [
            // C's whole residue is external; C#'s is not, so exactly one of the two rows
            // has a rule to name after external and the other must stay silent.
            new("C#", [new(UnresolvedRule.External, unresolved - 4), new(UnresolvedRule.TooCommon, 2),
                       new(UnresolvedRule.ReceiverNotTyped, 1), new(UnresolvedRule.OutOfScope, 1)]),
            new("C", [new(UnresolvedRule.External, 15)]),
        ],
        RefsOnlyCrossLanguage: 3,
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
    public void StatsSayHowMuchOfARowsUnresolvedIsCorrectlyExternal()
    {
        // A row that is all external reads "correct, not a gap" at a glance; a fully
        // resolved row has no unresolved to slice and must not carry the share at all.
        var stats = SomeStats() with
        {
            RefsByKind =
            [
                new("Inherit", 90, 27, 0, 63, External: 63),
                new("Include", 5, 5, 0, 0),
            ],
        };

        var text = TerseFormatter.Stats(stats);
        Assert.Contains("100.0% external", text);
        var includeRow = text.Split('\n').Single(l => l.Contains("Include"));
        Assert.DoesNotContain("external", includeRow);
    }

    [Fact]
    public void StatsCallDependenciesWhatTheyAreAndReconcileThemWithTheirReferences()
    {
        // "imports: 18 of 559" mislabelled C's includes and could not be compared with
        // the by-kind rows, whose Include+Import total counts references before the
        // packs deduplicate them. The line now names the thing it counts and, when the
        // two totals differ, says why rather than letting the tables disagree quietly.
        var stats = SomeStats() with
        {
            RefsByKind =
            [
                new("Call", 50, 40, 5, 5),
                new("Include", 6, 6, 0, 0),
                new("Import", 4, 0, 0, 4),
            ],
        };

        var text = TerseFormatter.Stats(stats);
        Assert.Contains("file dependencies: 3 of 8 name a workspace file", text);
        Assert.Contains("(10 include/import references, deduplicated)", text);
        Assert.DoesNotContain("imports:", text);

        // When nothing was deduplicated there is nothing to explain.
        var agreeing = SomeStats() with
        {
            RefsByKind = [new("Include", 8, 3, 0, 5)],
        };
        Assert.DoesNotContain("deduplicated", TerseFormatter.Stats(agreeing));
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

    /// <summary>
    /// The fact sheet's headline count needed the list open to be read correctly:
    /// <c>callers: 24</c> read as twenty-four call sites when eight were a minified
    /// JavaScript identifier spelling the same word as a C# enum member. The listing already
    /// marked those '?'; the number did not. Silent when there are none, so the ordinary
    /// symbol reads exactly as before — which is the half that keeps the note meaningful.
    /// </summary>
    [Fact]
    public void TheCallerCountSaysHowManyOfItsCallersAreOnlyANameMatch()
    {
        var real = new RelatedSymbol(3, "caller", SymbolKind.Method, "a.cs", 4,
            ReferenceKind.Use, EdgeConfidence.Unique);
        var coincidence = real with { Id = 4, Name = "n", Confidence = EdgeConfidence.Weak };

        var mixed = SomeDetail() with { Callers = [real, coincidence] };
        var clean = SomeDetail() with { Callers = [real] };

        Assert.Contains("callers: 2 (1 cross-language name match)", TerseFormatter.Detail(mixed));
        Assert.Contains("callers: 1  callees: 0", TerseFormatter.Detail(clean));
        Assert.DoesNotContain("cross-language", TerseFormatter.Detail(clean));
    }

    private static SymbolDetail SomeDetail() => new()
    {
        Id = 1,
        Name = "alpha",
        Kind = SymbolKind.Function,
        RelativePath = "a.c",
        Language = "C",
        StartLine = 1,
    };

    [Fact]
    public void CalleeSiteLinesSayWhichFileTheyAreIn()
    {
        // A callee row is headed by the TARGET's file and the site lines under it are in the
        // FOCUS's, so ":7" sits directly beneath a path it is not in. Left alone for a round
        // on the grounds that the two files are rarely different; they differ on 61.0% of
        // this repo's callee rows and 67.0% of JGraph's, which is the common case.
        var callee = new RelatedSymbol(3, "omega", SymbolKind.Function, "b.c", 9,
            ReferenceKind.Call, EdgeConfidence.Unique);
        var sites = new Dictionary<long, List<EdgeCallSite>>
        {
            [3] = [new EdgeCallSite(7, "()", EdgeConfidence.Unique, null, "omega")],
        };

        var callees = TerseFormatter.Related(From, [callee], TerseFormatter.Callees, 100, sites);

        Assert.Contains("indented lines are sites in a.c", callees);
        Assert.Contains("b.c:9", callees);
        Assert.Contains(":7 omega()", callees);
    }

    [Fact]
    public void ACallerListingDoesNotBorrowTheCalleeNote()
    {
        // Both ends of a caller row are the call site, so there is nothing to disambiguate
        // and the line would be false. Nor does it belong on a callee listing that was not
        // asked for sites — there are no indented lines to explain.
        var caller = new RelatedSymbol(3, "omega", SymbolKind.Function, "b.c", 9,
            ReferenceKind.Call, EdgeConfidence.Unique);
        var sites = new Dictionary<long, List<EdgeCallSite>>
        {
            [3] = [new EdgeCallSite(7, "()", EdgeConfidence.Unique, null, "alpha")],
        };

        Assert.DoesNotContain("indented lines are sites",
            TerseFormatter.Related(From, [caller], TerseFormatter.Callers, 100, sites));
        Assert.DoesNotContain("indented lines are sites",
            TerseFormatter.Related(From, [caller], TerseFormatter.Callees, 100, null));
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
    /// The cap says how much it cut. A field report on another codebase read
    /// "… list capped at 100 per direction" and could not tell 101 from 1,010 without
    /// leaving the tool for grep.
    /// </summary>
    [Fact]
    public void ACappedListSaysHowManyItIsShowingOf()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => new RelatedSymbol(i, $"caller{i}", SymbolKind.Method, "c.cs", i,
                ReferenceKind.Call, EdgeConfidence.Unique))
            .ToList();

        var text = TerseFormatter.Related(From, entries, TerseFormatter.Callers, 100, null, total: 412);

        Assert.Contains("showing 100 of 412", text);
    }

    /// <summary>
    /// And it says so even when the list came back shorter than the cap, which is the case
    /// the old length test could not see. The LIMIT applies to rows and the list holds one
    /// entry per caller and reference kind, so a symbol whose callers reference it many
    /// times loses entries while never reaching the cap: measured on this repo,
    /// <c>OverloadSql.Count</c> listed 51 of 174 and announced nothing at all.
    /// </summary>
    [Fact]
    public void AShortListThatWasStillTruncatedSaysSo()
    {
        var entries = Enumerable.Range(0, 51)
            .Select(i => new RelatedSymbol(i, $"caller{i}", SymbolKind.Method, "c.cs", i,
                ReferenceKind.Call, EdgeConfidence.Unique))
            .ToList();

        var text = TerseFormatter.Related(From, entries, TerseFormatter.Callers, 100, null, total: 174);

        Assert.Contains("showing 51 of 174", text);
    }

    /// <summary>Nothing cut, nothing said.</summary>
    [Fact]
    public void ACompleteListMentionsNoCap()
    {
        var entry = new RelatedSymbol(3, "caller", SymbolKind.Method, "c.cs", 4,
            ReferenceKind.Call, EdgeConfidence.Unique);

        var text = TerseFormatter.Related(From, [entry], TerseFormatter.Callers, 100, null, total: 1);

        Assert.DoesNotContain("capped", text);
        Assert.DoesNotContain("showing", text);
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

    /// <summary>
    /// The per-language refusal split, on the row that already exists rather than in a
    /// block of its own. External is left off it because the row carries that share
    /// already — what a reader cannot see anywhere else is which rule takes the rest, and
    /// measured here that is 39.7% of JavaScript's residue against 10.4% of C#'s.
    /// </summary>
    [Fact]
    public void EachLanguageRowNamesWhicheverRuleTakesMostOfWhatExternalLeaves()
    {
        var text = TerseFormatter.Stats(SomeStats());

        // C# has refusals besides external, so its row names the largest of them; C's
        // residue is entirely external, so there is nothing left to name and it says
        // nothing rather than printing a zero.
        var languageRows = text.Split('\n')
            .SkipWhile(line => !line.StartsWith("by language", StringComparison.Ordinal))
            .Skip(1)
            .Take(2)
            .ToList();

        Assert.Contains("too common", languageRows[0]);
        Assert.DoesNotContain("too common", languageRows[1]);
        Assert.Contains("external", languageRows[1]);
    }

    private static SymbolSearchHit Hit(long id, string name, bool loose) =>
        new(id, name, SymbolKind.Method, "s.cs", (int)id, null, Score: 0, LooseMatch: loose);

    /// <summary>
    /// A list of coincidences presented as a list of results is the failure two field
    /// reports described and this round reproduced: <c>search_symbols McpServer</c>
    /// answered with one unrelated test method and said nothing about it.
    /// </summary>
    [Fact]
    public void AListOfNothingButLooseHitsSaysSoBeforeListingThem()
    {
        var text = TerseFormatter.Search(
            "McpServer", [Hit(1, "ABindingInsideATypedTemplate", loose: true)], kindFilter: null);

        Assert.StartsWith("no symbol matches 'McpServer' well", text);
        Assert.Contains("ABindingInsideATypedTemplate", text);
        Assert.Contains("exact match", text);
    }

    [Fact]
    public void AFailedNameSearchShowsWhatTheCommentsAnswered()
    {
        var rescue = new SymbolSearchHit(
            9, "ImperfectParseCount", SymbolKind.Property, "Session.cs", 92, null, Score: 0,
            DocComment: "How many files the stats command reports as imperfect.");

        var text = TerseFormatter.Search(
            "StatsCommand", [], kindFilter: null, commentRescue: [rescue]);

        Assert.StartsWith("no symbols match 'StatsCommand'", text);
        Assert.Contains("mentions every word of it", text);
        Assert.Contains("ImperfectParseCount", text);

        // The comment is shown, not merely counted: a row whose reason for being here
        // cannot be read is a claim rather than a result.
        Assert.Contains("stats command", text);
    }

    [Fact]
    public void AFailedSearchWithNoCommentAnswerSaysTheSecondQuestionWasAsked()
    {
        // A silent second search that finds nothing reads exactly like one that never ran,
        // and the next thing the reader needs is the name of the flag that asks it directly.
        var text = TerseFormatter.Search("zzz", [], kindFilter: null, commentRescue: []);

        Assert.Contains("nor does any comment", text);
        Assert.Contains("--in-comments", text);
    }

    [Fact]
    public void AListOfNothingButLooseHitsAlsoGetsTheCommentAnswer()
    {
        var rescue = new SymbolSearchHit(
            9, "PeelAxes", SymbolKind.Method, "Handles.cs", 49, null, Score: 0,
            DocComment: "Splits a leading axes handle off an argument list.");

        var text = TerseFormatter.Search(
            "TargetAxes", [Hit(1, "TheAppearanceTailReachesTheWedges", loose: true)],
            kindFilter: null, commentRescue: [rescue]);

        Assert.StartsWith("no symbol matches 'TargetAxes' well", text);
        Assert.Contains("PeelAxes", text);
    }

    [Fact]
    public void ASearchThatFoundSomethingIsNotToldAboutComments()
    {
        var text = TerseFormatter.Search(
            "Xaml", [Hit(1, "XamlAnalyzer", loose: false)], kindFilter: null, commentRescue: []);

        Assert.DoesNotContain("comment", text);
    }

    [Fact]
    public void StrongHitsAreListedPlainAndTheLooseTailIsMarkedOnce()
    {
        var text = TerseFormatter.Search(
            "Xaml",
            [Hit(1, "XamlAnalyzer", loose: false), Hit(2, "XamlRegistry", loose: false),
             Hit(3, "ExtractedSymbol", loose: true)],
            kindFilter: null);

        Assert.DoesNotContain("no symbol matches", text);
        Assert.Contains("… and 1 where the letters merely appear in order:", text);

        // The boundary line separates them rather than labelling each row, which is only
        // sound because one query means one bar and the list is sorted by score.
        var boundary = text.IndexOf('…');
        Assert.True(text.IndexOf("XamlRegistry", StringComparison.Ordinal) < boundary);
        Assert.True(text.IndexOf("ExtractedSymbol", StringComparison.Ordinal) > boundary);
    }

    [Fact]
    public void AListWithNoLooseHitsSaysNothingAboutTheBarAtAll()
    {
        var text = TerseFormatter.Search(
            "ReferenceKind", [Hit(1, "ReferenceKind", loose: false)], kindFilter: null);

        Assert.DoesNotContain("merely appear in order", text);
        Assert.DoesNotContain("exact match", text);
    }

    /// <summary>
    /// An exact search that returns anything returned names containing the query, so the
    /// advice would be telling the reader to do what they just did.
    /// </summary>
    [Fact]
    public void AnExactSearchIsNeverToldToTryAnExactSearch()
    {
        var text = TerseFormatter.Search(
            "Mcp", [Hit(1, "McpCommand", loose: true)], kindFilter: null, exact: true);

        Assert.DoesNotContain("exact match", text);
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
