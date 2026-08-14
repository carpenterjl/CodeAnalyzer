using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Full stack: crawl → parse → SQLite → resolve → search and graph queries,
/// against a real temporary workspace and a real database.
/// </summary>
public class IndexStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeanalyzer-store", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private SqliteIndexStore? _store;

    public IndexStoreTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, ".index", "index.db");
    }

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

    /// <summary>Runs a full index and resolve, returning the open store.</summary>
    private async Task<SqliteIndexStore> IndexAsync(bool useIncrementalGate = false)
    {
        _store ??= SqliteIndexStore.Open(_databasePath, _root);
        _store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension);
        var orchestrator = new IndexOrchestrator(crawler, factory);

        await orchestrator.IndexAsync(
            WorkspaceSelection.EntireWorkspace(_root),
            _store,
            incrementalGate: useIncrementalGate ? _store : null);

        new ReferenceResolver(_store.Connection).ResolveAll();
        return _store;
    }

    private const string UartSource = """
        #include "ring.h"

        #define BUFFER_SIZE 256

        int ring_count(struct Ring *r) {
            return r->head - r->tail;
        }

        int uart_write(struct Ring *r, char c) {
            if (ring_count(r) >= BUFFER_SIZE) {
                return 1;
            }
            printf("writing");
            return 0;
        }
        """;

    [Fact]
    public async Task PersistsSymbolsAndFiles()
    {
        WriteFile("src/uart.c", UartSource);
        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        Assert.True(search.IndexedSymbolCount > 0);

        var hit = Assert.Single(search.Search("uart_write"), h => h.Name == "uart_write");
        Assert.Equal("src/uart.c", hit.RelativePath);
        Assert.Equal(SymbolKind.Function, hit.Kind);
    }

    [Fact]
    public async Task ResolvesCallEdgesBetweenFunctions()
    {
        WriteFile("src/uart.c", UartSource);
        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var writeId = search.Search("uart_write").First(h => h.Name == "uart_write").SymbolId;
        var graph = new GraphQueryService(store.Connection);

        var detail = graph.GetDetail(writeId);
        Assert.NotNull(detail);

        // uart_write calls ring_count, which is defined in the same file.
        var callee = Assert.Single(detail!.Callees, c => c.Name == "ring_count");
        Assert.Equal(ReferenceKind.Call, callee.ReferenceKind);
        Assert.Equal(EdgeConfidence.Unique, callee.Confidence);
    }

    [Fact]
    public async Task ReportsUnresolvedExternalCallsRatherThanGuessing()
    {
        WriteFile("src/uart.c", UartSource);
        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var writeId = search.Search("uart_write").First(h => h.Name == "uart_write").SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(writeId);

        // printf is not defined anywhere in the workspace; it must be listed, not invented.
        Assert.Contains(detail!.UnresolvedReferences, u => u.Name == "printf");
        Assert.DoesNotContain(detail.Callees, c => c.Name == "printf");
    }

    [Fact]
    public async Task FlagsAmbiguousMatchesWhenSeveralDefinitionsShareAName()
    {
        // Two files in different top-level directories define the same function name,
        // and a third calls it. Neither is closer, so both must be recorded.
        WriteFile("alpha/impl.c", "int shared_op(void) { return 1; }");
        WriteFile("beta/impl.c", "int shared_op(void) { return 2; }");
        WriteFile("gamma/caller.c", "int run(void) { return shared_op(); }");

        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var runId = search.Search("run").First(h => h.Name == "run").SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(runId);

        var candidates = detail!.Callees.Where(c => c.Name == "shared_op").ToList();

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(EdgeConfidence.Ambiguous, c.Confidence));
    }

    [Fact]
    public async Task PrefersTheDefinitionInTheSameFile()
    {
        WriteFile("alpha/local.c", """
            static int helper(void) { return 1; }
            int use_local(void) { return helper(); }
            """);
        WriteFile("beta/other.c", "int helper(void) { return 2; }");

        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var userId = search.Search("use_local").First(h => h.Name == "use_local").SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(userId);

        // The same-file tier wins outright, so this stays unambiguous.
        var callee = Assert.Single(detail!.Callees, c => c.Name == "helper");
        Assert.Equal(EdgeConfidence.Unique, callee.Confidence);
        Assert.Equal("alpha/local.c", callee.RelativePath);
    }

    [Fact]
    public async Task ExposesStructMembersAsCompositionFacts()
    {
        WriteFile("src/types.c", """
            struct Packet {
                int length;
                char payload[64];
            };
            """);

        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var packetId = search.Search("Packet").First(h => h.Name == "Packet").SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(packetId);

        Assert.Equal(new[] { "length", "payload" }, detail!.Members.Select(m => m.Name));
        Assert.Equal("int", detail.Members[0].TypeText);
    }

    [Fact]
    public async Task NeighbourhoodGraphIncludesFocusAndItsEdges()
    {
        WriteFile("src/uart.c", UartSource);
        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var writeId = search.Search("uart_write").First(h => h.Name == "uart_write").SymbolId;
        var fragment = new GraphQueryService(store.Connection).GetNeighbourhood(writeId);

        Assert.Equal(writeId, fragment.FocusId);
        Assert.Contains(fragment.Nodes, n => n.Name == "uart_write");
        Assert.Contains(fragment.Nodes, n => n.Name == "ring_count");

        // Every edge endpoint must be present in the node set the view receives.
        Assert.All(fragment.Edges, e =>
        {
            Assert.Contains(fragment.Nodes, n => n.Id == e.SourceId);
            Assert.Contains(fragment.Nodes, n => n.Id == e.TargetId);
        });
    }

    [Fact]
    public async Task ReindexingUnchangedFilesSkipsThem()
    {
        WriteFile("src/a.c", "int a(void) { return 0; }");
        WriteFile("src/b.c", "int b(void) { return 0; }");

        await IndexAsync();

        // Second pass with the gate enabled: nothing changed on disk.
        var store = _store!;
        store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension);
        var orchestrator = new IndexOrchestrator(crawler, factory);

        var outcome = await orchestrator.IndexAsync(
            WorkspaceSelection.EntireWorkspace(_root),
            store,
            incrementalGate: store);

        Assert.Equal(2, outcome.FilesUnchanged);
        Assert.Equal(0, outcome.FilesParsed);
    }

    [Fact]
    public async Task EditingAFileReplacesItsSymbolsRatherThanDuplicatingThem()
    {
        WriteFile("src/a.c", "int original(void) { return 0; }");
        await IndexAsync();

        WriteFile("src/a.c", "int renamed(void) { return 0; }");
        await IndexAsync();

        var search = new SymbolSearchService(_store!.Connection);
        search.Reload();

        Assert.DoesNotContain(search.Search("original"), h => h.Name == "original");
        Assert.Single(search.Search("renamed"), h => h.Name == "renamed");
    }

    [Fact]
    public async Task DeletedFilesAreRemovedFromTheIndex()
    {
        WriteFile("src/keep.c", "int keep(void) { return 0; }");
        WriteFile("src/remove.c", "int gone(void) { return 0; }");
        await IndexAsync();

        File.Delete(Path.Combine(_root, "src", "remove.c"));

        var store = _store!;
        store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension);
        var orchestrator = new IndexOrchestrator(crawler, factory);

        await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), store, incrementalGate: store);
        var removed = store.RemoveFilesNotSeenThisRun();

        Assert.Equal(1, removed);

        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        Assert.DoesNotContain(search.Search("gone"), h => h.Name == "gone");
        Assert.Single(search.Search("keep"), h => h.Name == "keep");
    }

    [Fact]
    public async Task RecordsIncludeGraphBetweenWorkspaceFiles()
    {
        WriteFile("include/ring.h", "struct Ring { int head; int tail; };");
        WriteFile("src/uart.c", UartSource);

        var store = await IndexAsync();

        using var command = store.Connection.CreateCommand();
        command.CommandText = """
            SELECT src.rel_path, dep.rel_path
            FROM file_dep d
            JOIN file src ON src.id = d.file_id
            JOIN file dep ON dep.id = d.dep_file_id
            """;

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        // "ring.h" resolves to include/ring.h by path suffix.
        Assert.Equal("src/uart.c", reader.GetString(0));
        Assert.Equal("include/ring.h", reader.GetString(1));
    }

    [Fact]
    public async Task SearchExcludesFunctionLocalsByDefault()
    {
        WriteFile("src/a.c", """
            int compute(void) {
                int scratch_value = 1;
                return scratch_value;
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        Assert.Empty(search.Search("scratch_value"));
        Assert.NotEmpty(search.Search("scratch_value", new SymbolSearchOptions { ExcludeFunctionLocals = false }));
    }

    [Fact]
    public async Task SelectedDirectoriesRoundTrip()
    {
        WriteFile("src/a.c", "int a(void) { return 0; }");
        var store = await IndexAsync();

        store.SaveSelectedDirectories(["src", "drivers"]);
        Assert.Equal(new[] { "src", "drivers" }, store.LoadSelectedDirectories());
    }
}
