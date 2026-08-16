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
