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
/// What the index calls an overload set, and how the three read paths that state it —
/// the graph node, the detail pane and the search list — agree about it.
/// </summary>
public class OverloadTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "codeanalyzer-overloads", Guid.NewGuid().ToString("N"));

    private SqliteIndexStore? _store;

    public OverloadTests() => Directory.CreateDirectory(_root);

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

    private static SymbolSearchService SearchOver(SqliteIndexStore store)
    {
        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        return search;
    }

    private const string TwoOverloads = """
        namespace Hardware;

        public class Radio
        {
            public int Send(byte[] payload) => payload.Length;

            public int Send(byte[] payload, int limit) => limit;
        }
        """;

    [Fact]
    public async Task TwoMethodsOfOneNameInOneClassAreAnOverloadSet()
    {
        WriteFile("src/Radio.cs", TwoOverloads);
        var store = await IndexAsync();

        var search = SearchOver(store);
        var graph = new GraphQueryService(store.Connection);

        var hits = search.Search("Send").Where(h => h.Name == "Send").ToList();
        Assert.Equal(2, hits.Count);

        // Both rows say the same name; the parameter list and the ordinal are what make
        // them two different answers rather than one answer printed twice.
        Assert.All(hits, h => Assert.Equal(2, h.OverloadCount));
        Assert.Equal([1, 2], hits.Select(h => h.OverloadOrdinal).Order());
        Assert.Contains(hits, h => h.ParameterText == "(byte[] payload)");
        Assert.Contains(hits, h => h.ParameterText == "(byte[] payload, int limit)");

        var first = hits.Single(h => h.ParameterText == "(byte[] payload)");
        Assert.Equal("public method · overload 1 of 2", first.Descriptor);

        // The graph node states the same two facts, from its own query.
        var node = Assert.Single(
            graph.GetNeighbourhood(first.SymbolId).Nodes, n => n.Id == first.SymbolId);
        Assert.Equal(2, node.OverloadCount);
        Assert.Equal(1, node.OverloadOrdinal);
        Assert.Equal("(byte[] payload)", node.ParameterText);

        // And so does the detail pane, which additionally lists the siblings.
        var detail = graph.GetDetail(first.SymbolId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Overloads.Count);
        Assert.Equal(1, detail.OverloadOrdinal);
        Assert.Equal([true, false], detail.Overloads.Select(o => o.IsCurrent));
        Assert.Equal(
            ["(byte[] payload)", "(byte[] payload, int limit)"],
            detail.Overloads.Select(o => o.ParameterText));
    }

    [Fact]
    public async Task ANameThatIsNotOverloadedListsNoOverloads()
    {
        WriteFile("src/Radio.cs", """
            public class Radio
            {
                public int Send(byte[] payload) => payload.Length;
            }
            """);

        var store = await IndexAsync();
        var search = SearchOver(store);

        var hit = Assert.Single(search.Search("Send"), h => h.Name == "Send");
        Assert.Equal(1, hit.OverloadCount);
        Assert.Equal("public method", hit.Descriptor);

        // A set of one is not a set. Returning the symbol's own single self would put an
        // "Overloads" heading over a fact the pane already states at the top.
        var detail = new GraphQueryService(store.Connection).GetDetail(hit.SymbolId);
        Assert.Empty(detail!.Overloads);
    }

    [Fact]
    public async Task SameNamedMethodsInDifferentClassesAreNotOverloadsOfEachOther()
    {
        WriteFile("src/Devices.cs", """
            public class Radio
            {
                public int Send(byte[] payload) => payload.Length;
            }

            public class Display
            {
                public int Send(string text) => text.Length;
            }
            """);

        var store = await IndexAsync();

        var hits = SearchOver(store).Search("Send").Where(h => h.Name == "Send").ToList();

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(1, h.OverloadCount));
    }

    [Fact]
    public async Task FreeFunctionsSharingAFileAndANameAreAnOverloadSet()
    {
        // C++ overloading at file scope: no container, so the file is the scope.
        WriteFile("src/math.cpp", """
            int clamp(int value) { return value; }

            int clamp(int value, int limit) { return value < limit ? value : limit; }
            """);

        var store = await IndexAsync();

        var hits = SearchOver(store).Search("clamp").Where(h => h.Name == "clamp").ToList();

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(2, h.OverloadCount));
    }

    [Fact]
    public async Task SameNamedFunctionsInDifferentFilesAreNotAnOverloadSet()
    {
        // C has no overloading, so two same-named static helpers in different files are
        // two unrelated functions. Calling them an overload set would be an invention.
        WriteFile("alpha/impl.c", "static int helper(int a) { return a; }");
        WriteFile("beta/impl.c", "static int helper(int a, int b) { return a + b; }");

        var store = await IndexAsync();

        var hits = SearchOver(store).Search("helper").Where(h => h.Name == "helper").ToList();

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(1, h.OverloadCount));
    }

    [Fact]
    public async Task ANonCallableIsNeverPartOfAnOverloadSet()
    {
        WriteFile("src/Radio.cs", """
            public class Radio
            {
                private int _count;

                public int Count() => _count;
            }
            """);

        var store = await IndexAsync();
        var search = SearchOver(store);

        var field = Assert.Single(search.Search("_count"), h => h.Name == "_count");
        Assert.Equal(1, field.OverloadCount);
        Assert.Equal("private int field", field.Descriptor);
    }

    [Fact]
    public async Task OverloadsSplitAcrossPartialFilesAreNotGrouped()
    {
        // A documented limit rather than a wish: a partial class is one symbol row per
        // file, so the two halves are two different containers as far as the index can
        // see. Recorded here so a change in behaviour is a decision, not a surprise.
        WriteFile("src/Radio.Send.cs", """
            public partial class Radio
            {
                public int Send(byte[] payload) => payload.Length;
            }
            """);
        WriteFile("src/Radio.SendText.cs", """
            public partial class Radio
            {
                public int Send(string text) => text.Length;
            }
            """);

        var store = await IndexAsync();

        var hits = SearchOver(store).Search("Send").Where(h => h.Name == "Send").ToList();

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(1, h.OverloadCount));
    }

    /// <summary>
    /// Every edge a call by this name produced, as (call line, what it landed on, how
    /// sure the resolver was). Reading the edge table directly because the point of these
    /// tests is which of two same-named definitions was chosen.
    /// </summary>
    private static List<(int Line, string? Parameters, EdgeConfidence Confidence)> CallEdges(
        SqliteIndexStore store, string name)
    {
        using var command = store.Connection.CreateCommand();
        command.CommandText = """
            SELECT r.line, s.param_text, e.confidence
            FROM ref r
            JOIN edge e ON e.ref_id = r.id
            JOIN symbol s ON s.id = e.target_symbol_id
            WHERE r.name = $name AND r.kind = $call
            ORDER BY r.line, s.start_line
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$call", (int)ReferenceKind.Call);

        var results = new List<(int, string?, EdgeConfidence)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                (EdgeConfidence)reader.GetInt32(2)));
        }

        return results;
    }

    [Fact]
    public async Task ACallPicksTheOverloadWhoseParameterCountItMatches()
    {
        WriteFile("src/Radio.cs", """
            public class Radio
            {
                public int Send(byte[] payload) => payload.Length;

                public int Send(byte[] payload, int limit) => limit;

                public int Run(byte[] data)
                {
                    Send(data);
                    return Send(data, 3);
                }
            }
            """);

        var store = await IndexAsync();
        var edges = CallEdges(store, "Send");

        // One edge per call site rather than one per overload: the argument count says
        // which definition was meant, and both halves of that are read out of the source.
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(EdgeConfidence.Unique, e.Confidence));
        Assert.Equal(
            ["(byte[] payload)", "(byte[] payload, int limit)"],
            edges.Select(e => e.Parameters));
    }

    [Fact]
    public async Task TheSameHoldsWhenTheOverloadsAreInAnotherFile()
    {
        WriteFile("src/clamp.cpp", """
            int clamp(int value) { return value; }

            int clamp(int value, int limit) { return value < limit ? value : limit; }
            """);
        WriteFile("src/user.cpp", "int use(void) { return clamp(1) + clamp(2, 3); }");

        var store = await IndexAsync();
        var edges = CallEdges(store, "clamp");

        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(EdgeConfidence.Unique, e.Confidence));
        Assert.Equal(["(int value)", "(int value, int limit)"], edges.Select(e => e.Parameters));
    }

    [Fact]
    public async Task ACallMatchingNoOverloadKeepsEveryCandidate()
    {
        WriteFile("src/Radio.cs", """
            public class Radio
            {
                public int Send(byte[] payload, int limit) => limit;

                public int Send(byte[] payload, int limit, int retries) => retries;

                public int Run(byte[] data) => Send(data);
            }
            """);

        var store = await IndexAsync();
        var edges = CallEdges(store, "Send");

        // An arity mismatch does not prove a non-match — a default argument or a params
        // array closes the gap — so with nothing matching, nothing is narrowed and the
        // ambiguity is reported rather than resolved by guess.
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(EdgeConfidence.Ambiguous, e.Confidence));
    }

    [Fact]
    public async Task ACVoidParameterListStillResolvesItsZeroArgumentCalls()
    {
        // int reset(void) parses as one named parameter, and every call site writes
        // reset(). Read as a hard filter that would unresolve most calls in a C codebase;
        // the soft rule keeps the candidate because nothing else matched either.
        WriteFile("src/board.c", """
            static int reset(void) { return 0; }

            int boot(void) { return reset(); }
            """);

        var store = await IndexAsync();

        var edge = Assert.Single(CallEdges(store, "reset"));
        Assert.Equal(EdgeConfidence.Unique, edge.Confidence);
    }

    [Fact]
    public async Task TheGraphPayloadCarriesTheParametersAndTheDescriptor()
    {
        WriteFile("src/Radio.cs", TwoOverloads);
        var store = await IndexAsync();

        var search = SearchOver(store);
        var graph = new GraphQueryService(store.Connection);

        var hit = search.Search("Send").First(h => h.ParameterText == "(byte[] payload, int limit)");
        var payload = GraphPayloadBuilder.Build(graph.GetNeighbourhood(hit.SymbolId));

        var node = Assert.Single(payload.Nodes, n => n.Id == hit.SymbolId.ToString());

        Assert.Equal("(byte[] payload, int limit)", node.ParameterText);
        Assert.Equal("public method · overload 2 of 2", node.Descriptor);
    }
}
