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
/// What <c>new X()</c> is allowed to bind to. The kind was introduced for Verilog, where
/// instantiation means a module, and the compatibility rule said Module and nothing else —
/// so every <c>new Foo()</c> in a JavaScript workspace resolved to nothing, silently and
/// for as long as the rule stood.
/// <para>
/// Both directions are pinned here, because only one of them is an improvement. Binding a
/// constructor call to the constructor is the fix; binding <c>new Map()</c> to a method
/// that happens to be called Map would raise the resolved count and be wrong.
/// </para>
/// </summary>
public class InstantiationResolutionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "codeanalyzer-new", Guid.NewGuid().ToString("N"));

    private SqliteIndexStore? _store;

    public InstantiationResolutionTests() => Directory.CreateDirectory(_root);

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

    private async Task<SqliteIndexStore> IndexAsync()
    {
        _store ??= SqliteIndexStore.Open(Path.Combine(_root, ".index", "index.db"), _root);
        _store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var orchestrator = new IndexOrchestrator(new FileCrawler(factory.IsSupportedExtension), factory);

        await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), _store);
        new ReferenceResolver(_store.Connection).ResolveAll();
        return _store;
    }

    private long SymbolId(SqliteIndexStore store, string name, string relativePath)
    {
        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        return search.Search(name).First(h => h.Name == name && h.RelativePath == relativePath).SymbolId;
    }

    private static IReadOnlyList<RelatedSymbol> Callers(GraphQueryService graph, long symbolId) =>
        graph.GetDetail(symbolId)?.Callers ?? [];

    [Fact]
    public async Task AnEs5ConstructorIsFoundByTheCodeThatConstructsIt()
    {
        // The shape that measured as 100% unresolved against this repo's own JavaScript:
        // the definition is a Function, not a Class, so a rule admitting only class-like
        // kinds would still have fixed nothing.
        WriteFile("node.js", """
            function CoSENode(graph) {
                this.graph = graph;
            }
            module.exports = CoSENode;
            """);
        WriteFile("layout.js", """
            var CoSENode = require("./node.js");
            function build(graph) {
                return new CoSENode(graph);
            }
            """);

        var store = await IndexAsync();
        var graph = new GraphQueryService(store.Connection);

        var callers = Callers(graph,SymbolId(store, "CoSENode", "node.js"));

        Assert.Contains(callers, c =>
            c.ReferenceKind == ReferenceKind.Instantiate && c.RelativePath == "layout.js");
    }

    [Fact]
    public async Task ConstructingARuntimeBuiltinDoesNotBindToASameNamedMethod()
    {
        // `new Map()` means the runtime's Map. This workspace defines a *method* called
        // Map, which is exactly the false friend that made Method an unsafe kind to admit:
        // binding here would have counted as 71 newly resolved references in this repo and
        // every one of them would have pointed at unrelated code.
        //
        // The class form is deliberate. `Registry.prototype.Map = function (fn) { … }` is
        // captured as a Function, not a Method — the pack sees a function expression bound
        // to a name and has no syntax telling it that a prototype assignment is a method —
        // so that spelling *would* be admitted here. The limitation is real and untouched
        // by this milestone; it costs nothing on this workspace, where the Map definitions
        // are a Method and a Property, and it is worth knowing before someone reads the
        // rule as "constructors only".
        WriteFile("registry.js", """
            class Registry {
                Map(fn) { return fn; }
            }
            """);
        WriteFile("use.js", """
            function makeIndex() {
                return new Map();
            }
            """);

        var store = await IndexAsync();
        var graph = new GraphQueryService(store.Connection);

        var callers = Callers(graph,SymbolId(store, "Map", "registry.js"));

        Assert.DoesNotContain(callers, c => c.ReferenceKind == ReferenceKind.Instantiate);
    }
}
