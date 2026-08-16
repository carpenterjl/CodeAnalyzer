using CodeAnalyzer.Core.Crawling;
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
