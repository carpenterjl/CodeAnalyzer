using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Storage;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The stats block, measured against a workspace whose shape is known by construction.
/// The per-reference resolution triple is the point of the surface, so each of its three
/// buckets is populated deliberately: one call with exactly one definition, one call with
/// two, and one call with none at all.
/// </summary>
public class IndexStatsQueryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "codeanalyzer-stats", Guid.NewGuid().ToString("N"));

    private SqliteIndexStore? _store;

    public IndexStatsQueryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _store?.Dispose();
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

    private async Task<IndexStats> IndexAndReadAsync()
    {
        _store ??= SqliteIndexStore.Open(Path.Combine(_root, ".index", "index.db"), _root);
        _store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var orchestrator = new IndexOrchestrator(new FileCrawler(factory.IsSupportedExtension), factory);

        await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), _store);
        new ReferenceResolver(_store.Connection).ResolveAll();

        return IndexStatsQuery.Read(_store.Connection);
    }

    [Fact]
    public async Task TheResolutionTripleCountsReferencesNotEdges()
    {
        // twin must live in neither the caller's file nor an included one: a same-file
        // definition would win outright and the reference would leave the ambiguous bucket.
        WriteFile("a.c", """
            void unique_target(void) { }
            void caller(void) {
                unique_target();
                twin(1);
                nowhere_defined();
            }
            """);
        WriteFile("b.c", "void twin(int x) { }");
        WriteFile("c.c", "void twin(char c) { }");

        var stats = await IndexAndReadAsync();

        // twin() lands on both definitions: one ambiguous reference, two edges.
        Assert.True(stats.RefsResolvedUniquely >= 1);
        Assert.True(stats.RefsAmbiguous >= 1);
        Assert.True(stats.RefsUnresolved >= 1);
        Assert.Equal(stats.TotalRefs,
            stats.RefsResolvedUniquely + stats.RefsAmbiguous + stats.RefsUnresolved);
        Assert.True(stats.MeanCandidatesWhenAmbiguous >= 2.0);
        // Every unique reference contributes one edge, every ambiguous one at least two.
        Assert.True(stats.TotalEdges >= stats.RefsResolvedUniquely + 2 * stats.RefsAmbiguous);
    }

    [Fact]
    public async Task EverySplitPartitionsTheSameReferenceTotal()
    {
        // The splits are computed by a different query than the whole-index triple, over a
        // LEFT JOIN whose whole job is to keep edgeless references in the count. Drop the
        // join to an inner one and the unresolved column silently empties — so the check
        // that matters is that each split still adds up to the totals it slices.
        WriteFile("a.c", """
            void defined_here(void) { }
            void caller(void) {
                defined_here();
                absent_everywhere();
            }
            """);
        WriteFile("b.py", "gamma = 1");

        var stats = await IndexAndReadAsync();

        foreach (var splits in new[] { stats.RefsByKind, stats.RefsByLanguage })
        {
            Assert.Equal(stats.TotalRefs, splits.Sum(s => s.Total));
            Assert.Equal(stats.RefsResolvedUniquely, splits.Sum(s => s.Unique));
            Assert.Equal(stats.RefsAmbiguous, splits.Sum(s => s.Ambiguous));
            Assert.Equal(stats.RefsUnresolved, splits.Sum(s => s.Unresolved));
            Assert.All(splits, s => Assert.Equal(s.Total, s.Unique + s.Ambiguous + s.Unresolved));
        }

        // Unresolved must actually be populated, or the sums above pass on all-zero columns.
        Assert.True(stats.RefsUnresolved > 0);
        Assert.Contains(stats.RefsByLanguage, s => s.Name == "C");
    }

    [Fact]
    public async Task AnIncludeCountsResolvedAgainstItsFileNotItsAbsentEdge()
    {
        // An #include and a using name a file or a namespace, never a symbol, so the resolver
        // gives them no edge at all — and counting edges reported every one of them
        // unresolved. That read the truth backwards for a header sitting right next to its
        // includer: it resolves, in file_dep, to util.h. A Python import that names nothing
        // is here as the control — the fix must still leave a genuinely unresolved dependency
        // unresolved, not credit every include-or-import kind wholesale.
        WriteFile("main.c", """
            #include "util.h"
            void go(void) { helper(); }
            """);
        WriteFile("util.h", "void helper(void);");
        WriteFile("app.py", "import nonexistent_pkg\n");

        var stats = await IndexAndReadAsync();

        var include = Assert.Single(stats.RefsByKind, s => s.Name == nameof(ReferenceKind.Include));
        Assert.Equal(1, include.Total);
        Assert.Equal(1, include.Unique);      // resolved via file_dep, no longer a false gap
        Assert.Equal(0, include.Ambiguous);   // a dependency is unique-or-nothing by construction
        Assert.Equal(0, include.Unresolved);

        var import = Assert.Single(stats.RefsByKind, s => s.Name == nameof(ReferenceKind.Import));
        Assert.Equal(1, import.Total);
        Assert.Equal(0, import.Unique);        // nonexistent_pkg names no workspace file
        Assert.Equal(1, import.Unresolved);

        // The headline triple credits the resolved include exactly as the split does, or the
        // two would drift apart by that one reference.
        Assert.Equal(stats.RefsResolvedUniquely, stats.RefsByKind.Sum(s => s.Unique));
        Assert.Equal(stats.RefsUnresolved, stats.RefsByKind.Sum(s => s.Unresolved));
    }

    [Fact]
    public async Task TheExternalShareSeparatesAbsentNamesFromKindIncompatibleOnes()
    {
        // Three unresolved references, three different truths. absent_everywhere names
        // nothing — external. The call to `helper` names only a variable, which a call
        // cannot land on — still external, and the case a naive name-join gets wrong
        // (this round's own probe read `new Map()` matching a method called Map as a
        // workspace gap). The use of hidden_local names a real variable that the
        // resolver rightly refuses — it is another function's local — so it stays in
        // the residue, which is exactly where a real gap would hide too.
        WriteFile("a.c", """
            void caller(void) {
                int x = hidden_local;
                absent_everywhere();
                helper();
            }
            """);
        WriteFile("b.py", "helper = 1\n");
        WriteFile("c.c", "void f(void) { int hidden_local; }");

        var stats = await IndexAndReadAsync();

        var call = Assert.Single(stats.RefsByKind, s => s.Name == nameof(ReferenceKind.Call));
        Assert.Equal(2, call.Unresolved);
        Assert.Equal(2, call.External);

        // hidden_local is unresolved but not external: a compatible definition exists,
        // the refusal was locality. The external column must not sweep it in.
        var use = Assert.Single(stats.RefsByKind, s => s.Name == nameof(ReferenceKind.Use));
        Assert.True(use.Unresolved >= 1);
        Assert.Equal(0, use.External);

        // The split slices unresolved only; a resolved row contributes nothing.
        Assert.All(stats.RefsByKind, s => Assert.True(s.External <= s.Unresolved));
    }

    /// <summary>
    /// A name carried by more definitions than the resolver's candidate cap allows. Twenty-six
    /// so the set is over the cap of 24 with room to spare, and each on its own class so the
    /// definitions are members rather than locals — a local would be refused by the container
    /// rule first and would test the wrong bucket.
    /// </summary>
    private void WriteAHotName(string name)
    {
        for (var i = 0; i < 26; i++)
        {
            WriteFile($"hot/Holder{i}.cs", $$"""
                namespace W;
                public class Holder{{i}} { public int {{name}} { get; set; } }
                """);
        }
    }

    [Fact]
    public async Task EveryUnresolvedReferenceIsAccountedForByExactlyOneRule()
    {
        // The block's whole claim is that the four rules are exhaustive, so the assertion that
        // matters is the arithmetic, not any single bucket: every reference the resolver
        // attempted and refused lands in exactly one rule, and `Unexplained` — which exists to
        // catch the partition being wrong rather than to be populated — reads zero.
        WriteAHotName("Widget");
        WriteFile("Refused.cs", """
            namespace W;
            public class Refused
            {
                public void Go(object surprise)
                {
                    NoOneDefinesThis();     // external: no compatible definition anywhere
                    var a = Widget;         // hot name, no receiver to narrow it
                    var b = surprise.Widget;// hot name with a receiver that types to nothing
                    var c = tucked;         // another function's local: out of scope
                }
            }
            """);
        WriteFile("Locals.cs", """
            namespace W;
            public class Locals { public void M() { var tucked = 1; } }
            """);

        var stats = await IndexAndReadAsync();

        // Include and import are settled against file_dep and never attempted as symbols, so
        // they are outside this partition by construction — subtract them, don't ignore them.
        var fileScoped = stats.RefsByKind
            .Where(s => s.Name is nameof(ReferenceKind.Include) or nameof(ReferenceKind.Import))
            .Sum(s => s.Unresolved);
        Assert.Equal(stats.RefsUnresolved - fileScoped, stats.UnresolvedByRule.Sum(r => r.Count));
        Assert.Equal(0, Count(stats, UnresolvedRule.Unexplained));

        // ...and the sum is not zero, or the equality above would hold on an empty partition.
        Assert.True(stats.RefsUnresolved - fileScoped > 0);

        // Every rule is present as a row even when it counted nothing: a rule that prints only
        // when non-empty reads as a question never asked.
        Assert.Equal(
            Enum.GetValues<UnresolvedRule>().Length,
            stats.UnresolvedByRule.Select(r => r.Rule).Distinct().Count());
    }

    [Fact]
    public async Task EachRuleClaimsTheReferenceItActuallyRefused()
    {
        // Exhaustiveness alone can be met by one bucket swallowing everything, so each rule
        // is given a reference only it can explain.
        WriteAHotName("Widget");
        WriteFile("Refused.cs", """
            namespace W;
            public class Refused
            {
                public void Go(object surprise)
                {
                    NoOneDefinesThis();
                    var a = Widget;
                    var b = surprise.Widget;
                    var c = tucked;
                }
            }
            """);
        WriteFile("Locals.cs", """
            namespace W;
            public class Locals { public void M() { var tucked = 1; } }
            """);

        var stats = await IndexAndReadAsync();

        Assert.True(Count(stats, UnresolvedRule.External) > 0, "NoOneDefinesThis names nothing");
        Assert.True(Count(stats, UnresolvedRule.TooCommon) > 0, "bare Widget has 26 rivals");
        Assert.True(Count(stats, UnresolvedRule.ReceiverNotTyped) > 0, "surprise types to nothing");
        Assert.True(Count(stats, UnresolvedRule.OutOfScope) > 0, "tucked is another method's local");
    }

    [Fact]
    public async Task AHotNameWithNoReceiverIsNotFiledAsExternal()
    {
        // The rules are applied in an order that makes them exclusive, and the order is load-
        // bearing: `Widget` is a hot name *and* has compatible definitions, so a partition that
        // tested hotness first would report a workspace-defined name as external and hide the
        // gate that actually refused it. Nothing external exists here at all.
        WriteAHotName("Widget");
        WriteFile("Bare.cs", """
            namespace W;
            public class Bare { public void Go() { var a = Widget; } }
            """);

        var stats = await IndexAndReadAsync();

        Assert.True(Count(stats, UnresolvedRule.TooCommon) > 0);
        Assert.Equal(0, Count(stats, UnresolvedRule.External));
        Assert.Equal(0, Count(stats, UnresolvedRule.Unexplained));
    }

    private static int Count(IndexStats stats, UnresolvedRule rule) =>
        stats.UnresolvedByRule.Single(r => r.Rule == rule).Count;

    [Fact]
    public async Task APathScopeNarrowsEveryCountToItsSubtree()
    {
        WriteFile("core/a.c", """
            int shared_core;
            void core_fn(void) { shared_core = 1; }
            """);
        WriteFile("app/b.c", """
            int app_only;
            void app_fn(void) { app_only = 2; }
            """);

        var whole = await IndexAndReadAsync();
        var core = IndexStatsQuery.Read(_store!.Connection, "core");

        // The subtree sees only its own file and echoes the scope back normalised; the whole
        // workspace carries no scope at all.
        Assert.Equal(2, whole.TotalFiles);
        Assert.Null(whole.ScopePath);
        Assert.Equal(1, core.TotalFiles);
        Assert.Equal("core", core.ScopePath);
        Assert.True(core.TotalRefs > 0 && core.TotalRefs < whole.TotalRefs);

        // Every split still partitions the scoped total, the same invariant the whole-index
        // report holds — a scope narrows the rows, it does not break their arithmetic.
        Assert.Equal(core.TotalRefs, core.RefsByKind.Sum(s => s.Total));
        Assert.Equal(core.RefsResolvedUniquely, core.RefsByKind.Sum(s => s.Unique));
        Assert.Equal(core.TotalSymbols, core.SymbolsByKind.Sum(c => c.Count));

        // "." and a blank string are the workspace root — everything — not a literal path
        // that no forward-slashed rel_path could match, which would silently zero the report.
        Assert.Equal(whole.TotalFiles, IndexStatsQuery.Read(_store.Connection, ".").TotalFiles);
        Assert.Equal(whole.TotalFiles, IndexStatsQuery.Read(_store.Connection, "  ").TotalFiles);

        // A sibling prefix must not be swept in by the LIKE: scoping to "app" never sees
        // "appendix". (There is no appendix here; the guard is that "core" saw exactly one.)
        Assert.Equal(0, IndexStatsQuery.Read(_store.Connection, "cor").TotalFiles);
    }

    [Fact]
    public async Task TalliesCoverEveryFileAndSymbol()
    {
        WriteFile("a.c", "int alpha;");
        WriteFile("b.py", "beta = 1");

        var stats = await IndexAndReadAsync();

        Assert.Equal(2, stats.TotalFiles);
        Assert.Equal(stats.TotalFiles, stats.FilesByLanguage.Sum(c => c.Count));
        Assert.Equal(stats.TotalSymbols, stats.SymbolsByKind.Sum(c => c.Count));
        Assert.True(stats.DatabaseBytes > 0);
    }
}
