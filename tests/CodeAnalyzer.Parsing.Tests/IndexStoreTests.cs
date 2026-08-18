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

    /// <summary>
    /// Every row of every listing reads <c>path:line</c>, and the two have to describe one
    /// place. A callee row names the file the callee is defined in, so its line has to be
    /// the line it is defined on — reading the reference's line instead pairs that path
    /// with a position from a different file entirely. Measured before this was fixed:
    /// 99.7% of cross-file callee rows on this repo named a line that was not the target's
    /// declaration, and 39.1% named a line past the end of the file they pointed at.
    /// </summary>
    [Fact]
    public async Task ACalleeIsLocatedWhereItIsDeclaredAndACallerWhereItCalls()
    {
        // The two files are different lengths on purpose: the call sits on a line the
        // callee's file does not have, which is exactly the shape that went unnoticed.
        WriteFile("alpha/target.c", "int target(void) { return 1; }");
        WriteFile("beta/caller.c", """
            // 1
            // 2
            // 3
            // 4
            int caller(void)
            {
                return target();
            }
            """);

        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        var graph = new GraphQueryService(store.Connection);

        var callerId = search.Search("caller").First(h => h.Name == "caller").SymbolId;
        var callee = Assert.Single(graph.GetDetail(callerId)!.Callees, c => c.Name == "target");
        Assert.Equal("alpha/target.c", callee.RelativePath);
        Assert.Equal(1, callee.Line);

        // And from the other end the pair is the call site: the caller's file, and the line
        // inside it where the call is written.
        var targetId = search.Search("target").First(h => h.Name == "target").SymbolId;
        var caller = Assert.Single(graph.GetDetail(targetId)!.Callers, c => c.Name == "caller");
        Assert.Equal("beta/caller.c", caller.RelativePath);
        Assert.Equal(7, caller.Line);
    }

    [Fact]
    public async Task ACallerWithSeveralSitesIsLocatedAtItsFirst()
    {
        // A quarter of caller rows on a real corpus have more than one distinct site line;
        // the listed one is the first, by contract, not whichever row the scan yielded.
        WriteFile("alpha/target.c", "int target(void) { return 1; }");
        WriteFile("beta/caller.c", """
            int caller(void)
            {
                target();
                target();
                return target();
            }
            """);

        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        var graph = new GraphQueryService(store.Connection);

        var targetId = search.Search("target").First(h => h.Name == "target").SymbolId;
        var caller = Assert.Single(graph.GetDetail(targetId)!.Callers, c => c.Name == "caller");
        Assert.Equal(3, caller.Line);
    }

    [Fact]
    public async Task ASameNameTieGoesToTheSymbolTheWorkspaceActuallyUses()
    {
        // 15 of the 51 real queries ever issued against this tool hit a rank-1 tie between
        // same-name symbols — same score, same length — which an unstable sort resolved by
        // partition luck. The tie goes to unique-edge referencers: the class two other
        // types use beats the identically named field declared earlier, which nothing
        // references. Unique edges only, because ambiguous edges land on every same-name
        // candidate and would score the whole tie group as equally important.
        WriteFile("src/widgets.cs", """
            class Alpha
            {
                public int Widget;
            }

            class Widget
            {
            }

            class UserOne
            {
                private Widget _fieldOne;
            }

            class UserTwo
            {
                private Widget _fieldTwo;
            }
            """);

        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var hits = search.Search("Widget").Where(h => h.Name == "Widget").ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal(SymbolKind.Class, hits[0].Kind);
        Assert.Equal(SymbolKind.Field, hits[1].Kind);
    }

    [Fact]
    public async Task ACommentSearchFindsWhatANameSearchCannot()
    {
        // The point of the whole feature: you remember what a thing does and not what it is
        // called. Neither name contains "retry", so a name search of any mode returns
        // nothing at all.
        WriteFile("src/net.cs", """
            class Transport
            {
                // Sends the frame again when the far end answers 502.
                public void Push() { }

                // Blocks until the link is idle.
                public void Drain() { }
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        Assert.Empty(search.Search("502"));

        var found = search.Search("502", new SymbolSearchOptions
        {
            Match = SymbolMatchMode.DocComment,
        });

        var hit = Assert.Single(found);
        Assert.Equal("Push", hit.Name);

        // A comment match always carries its comment, whatever the caller asked for: a hit
        // whose reason for being in the list cannot be read is a claim, not a result.
        Assert.Contains("502", hit.DocComment);
    }

    [Fact]
    public async Task ACommentHitWhoseNameAlsoMatchesComesFirst()
    {
        // Prose has no score, so the order is stated rather than left to the scan — round
        // sixteen's lesson. A symbol CALLED retry whose comment mentions retrying is more
        // likely to be what "retry" meant than one that mentions it in passing.
        WriteFile("src/net.cs", """
            class Transport
            {
                // Mentions retry only in passing.
                public void Drain() { }

                // Retry policy for the transport.
                public void RetryPolicy() { }
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var found = search.Search("retry", new SymbolSearchOptions
        {
            Match = SymbolMatchMode.DocComment,
        });

        Assert.Equal(2, found.Count);
        Assert.Equal("RetryPolicy", found[0].Name);
        Assert.Equal("Drain", found[1].Name);
    }

    [Fact]
    public async Task AnOrdinarySearchCarriesNoCommentUntilItIsAskedTo()
    {
        // The flag exists so a result list does not double in height for a fact four
        // definitions in five do not have.
        WriteFile("src/net.cs", """
            class Transport
            {
                // Sends the frame again when the far end answers 502.
                public void Push() { }
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        Assert.Null(Assert.Single(search.Search("Push")).DocComment);

        var asked = search.Search("Push", new SymbolSearchOptions { IncludeDocComments = true });
        Assert.Contains("502", Assert.Single(asked).DocComment);
    }

    [Fact]
    public async Task ATieIsBrokenEvenWhenTheLimitCutsThroughIt()
    {
        // The importance signal is fetched per tie rather than held for every symbol, which
        // is what makes it free — but it means the fetch has to know which rows it needs
        // before the list is truncated. A run of equally-ranked hits can only be reordered
        // among themselves, so it matters nowhere except here: when the cut falls inside a
        // run, that order decides which of its members is returned at all.
        //
        // Same fixture as the tie test above, asked for ONE hit. The field is declared
        // first, so the unstable sort's own order would hand back the field.
        WriteFile("src/widgets.cs", """
            class Alpha
            {
                public int Widget;
            }

            class Widget
            {
            }

            class UserOne
            {
                private Widget _fieldOne;
            }
            """);

        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var hits = search.Search("Widget", new SymbolSearchOptions { Limit = 1 });
        var only = Assert.Single(hits);
        Assert.Equal("Widget", only.Name);
        Assert.Equal(SymbolKind.Class, only.Kind);
    }

    [Fact]
    public async Task AChattyCallerDoesNotStarveTheListBelowItsCap()
    {
        // The LIMIT counts entries, not raw reference rows. Before it did, one caller with
        // more sites than the cap ate the row budget and the list went hungry below its own
        // cap — 47 of the 50 heaviest listings on this repo lost entries that way.
        WriteFile("alpha/target.c", "int target(void) { return 1; }");
        WriteFile("beta/callers.c", """
            int aaa(void) { target(); target(); target(); target(); target(); return target(); }
            int bbb(void) { return target(); }
            int ccc(void) { return target(); }
            int ddd(void) { return target(); }
            int eee(void) { return target(); }
            """);

        var store = await IndexAsync();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        var graph = new GraphQueryService(store.Connection)
        {
            NeighboursPerDirection = 1, // RelatedLimit = 4, below the five callers
        };

        var targetId = search.Search("target").First(h => h.Name == "target").SymbolId;
        var detail = graph.GetDetail(targetId)!;

        // aaa's six sites are one entry; the cap has room for three more callers.
        Assert.Equal(4, detail.Callers.Count);
        Assert.Equal(5, detail.CallerTotal);
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

    [Fact]
    public async Task ATypedefAnonymousStructWritesDespiteItsForwardContainerReference()
    {
        // tree-sitter reports `typedef struct { … } Name;` only at the trailing name, so
        // the members precede their container in the symbol list and reference a LARGER
        // row id. With per-statement foreign-key checking this failed the whole write
        // batch — the shape of every STM32 vendor header and every Cython-generated .c,
        // and the writer fault behind the reported "frozen at 3802/4842" workspace. The
        // FK check is deferred to commit, where the container row exists.
        WriteFile("src/regs.h", """
            typedef struct {
                volatile unsigned int CR1;
                volatile unsigned int CR2;
            } USART_TypeDef;
            """);

        var store = await IndexAsync();

        using var command = store.Connection.CreateCommand();
        command.CommandText = """
            SELECT member.name, container.name
            FROM symbol member
            JOIN symbol container ON container.id = member.container_id
            WHERE member.name IN ('CR1', 'CR2')
            ORDER BY member.name
            """;

        using var reader = command.ExecuteReader();
        var rows = new List<(string Member, string Container)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal([("CR1", "USART_TypeDef"), ("CR2", "USART_TypeDef")], rows);
    }
}
