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
/// The same-file resolution tier's locality claim — a call in this file probably means
/// the definition in this file — is sound for <c>foo()</c> and <c>this.foo()</c>, and
/// unsound for <c>obj.foo()</c>. These tests pin the receiver rule that separates the
/// three, including the round-three false edge that motivated it: a call through an
/// orchestrator field resolving <em>exact</em> to a same-named method on the caller's
/// own class.
/// </summary>
public class ReceiverResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeanalyzer-receiver", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private SqliteIndexStore? _store;

    public ReceiverResolutionTests()
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

    private async Task<SqliteIndexStore> IndexAsync()
    {
        _store ??= SqliteIndexStore.Open(_databasePath, _root);
        _store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension);
        var orchestrator = new IndexOrchestrator(crawler, factory);

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

    /// <summary>
    /// The round-three false edge, reduced: Session.Index exists beside a call written
    /// as <c>orchestrator.Index(…)</c>, whose real target is another class's method.
    /// The same-file tier used to see one local name match and stamp it Unique; with the
    /// receiver stored, every definition of the name competes and the edge is honest
    /// about being a name match.
    /// </summary>
    [Fact]
    public async Task ACallThroughAReceiverIsNotClaimedExactByTheSameFileTier()
    {
        WriteFile("src/Session.cs", """
            public class Session
            {
                private Orchestrator orchestrator = new Orchestrator();

                public void Index(int a) { }

                public void Apply()
                {
                    orchestrator.Index(1);
                }
            }
            """);
        WriteFile("src/Orchestrator.cs", """
            public class Orchestrator
            {
                public void Index(int a) { }
            }
            """);

        var store = await IndexAsync();
        var sessionIndex = SymbolId(store, "Index", "src/Session.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(sessionIndex);

        // Apply may still list Session.Index — it is a legitimate candidate by name —
        // but never as an exact edge: the receiver says the file proves nothing.
        var fromApply = detail!.Callers.Where(c => c.Name == "Apply").ToList();
        Assert.All(fromApply, c => Assert.Equal(EdgeConfidence.Ambiguous, c.Confidence));

        // And the real target is in the candidate set too, at the same confidence.
        var orchestratorIndex = SymbolId(store, "Index", "src/Orchestrator.cs");
        var orchestratorDetail = new GraphQueryService(store.Connection).GetDetail(orchestratorIndex);
        var candidate = Assert.Single(orchestratorDetail!.Callers, c => c.Name == "Apply");
        Assert.Equal(EdgeConfidence.Ambiguous, candidate.Confidence);
    }

    /// <summary>
    /// A spelled-out self-receiver is the same local claim a bare name makes, and must
    /// keep the exact edge the bare form would get.
    /// </summary>
    [Fact]
    public async Task AThisReceiverKeepsItsExactLocalEdge()
    {
        WriteFile("src/Widget.cs", """
            public class Widget
            {
                public void Refresh() { }
                public void Update() { this.Refresh(); }
            }
            """);
        // A decoy elsewhere, to prove locality still wins for this.
        WriteFile("other/Decoy.cs", """
            public class Decoy
            {
                public void Refresh() { }
            }
            """);

        var store = await IndexAsync();
        var refresh = SymbolId(store, "Refresh", "src/Widget.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(refresh);

        var caller = Assert.Single(detail!.Callers, c => c.Name == "Update");
        Assert.Equal(EdgeConfidence.Unique, caller.Confidence);
    }

    /// <summary>
    /// Demoting the same-file tier must not cost resolution where the name has exactly
    /// one definition: the cross-file pass still finds it and it is still Unique.
    /// </summary>
    [Fact]
    public async Task AReceiveredCallWithOneDefinitionAnywhereStillResolvesUnique()
    {
        WriteFile("src/Consumer.cs", """
            public class Consumer
            {
                private Device device = new Device();
                public void Run() { device.Transmit(1); }
            }
            """);
        WriteFile("src/Device.cs", """
            public class Device
            {
                public void Transmit(int payload) { }
            }
            """);

        var store = await IndexAsync();
        var transmit = SymbolId(store, "Transmit", "src/Device.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(transmit);

        var caller = Assert.Single(detail!.Callers, c => c.Name == "Run");
        Assert.Equal(EdgeConfidence.Unique, caller.Confidence);
    }

    /// <summary>
    /// The receivered call's own file still competes: when the only definition of the
    /// name is local, the edge exists — ambiguity needs a second candidate, not a rule
    /// that erases the local one.
    /// </summary>
    [Fact]
    public async Task AReceiveredCallCanStillResolveToItsOwnFile()
    {
        WriteFile("src/Solo.cs", """
            public class Helper
            {
                public void Poke(int a) { }
            }

            public class Solo
            {
                private Helper helper = new Helper();
                public void Run() { helper.Poke(1); }
            }
            """);

        var store = await IndexAsync();
        var poke = SymbolId(store, "Poke", "src/Solo.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(poke);

        var caller = Assert.Single(detail!.Callers, c => c.Name == "Run");
        Assert.Equal(EdgeConfidence.Unique, caller.Confidence);
    }

    /// <summary>Bare calls keep the behaviour every existing edge was built on.</summary>
    [Fact]
    public async Task ABareCallStillPrefersTheSameFileDefinition()
    {
        WriteFile("src/Local.cs", """
            public class Local
            {
                public void Helper() { }
                public void Use() { Helper(); }
            }
            """);
        WriteFile("other/Far.cs", """
            public class Far
            {
                public void Helper() { }
            }
            """);

        var store = await IndexAsync();
        var helper = SymbolId(store, "Helper", "src/Local.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(helper);

        var caller = Assert.Single(detail!.Callers, c => c.Name == "Use");
        Assert.Equal(EdgeConfidence.Unique, caller.Confidence);
    }

    /// <summary>
    /// The receiver travels to the call-site listing verbatim — it is the evidence a
    /// reader needs to audit the confidence marker beside the edge.
    /// </summary>
    [Fact]
    public async Task TheReceiverIsRecordedVerbatimAtTheCallSite()
    {
        WriteFile("src/Consumer.cs", """
            public class Consumer
            {
                private Device device = new Device();
                public void Run() { device.Transmit(1); }
            }
            """);
        WriteFile("src/Device.cs", """
            public class Device
            {
                public void Transmit(int payload) { }
            }
            """);

        var store = await IndexAsync();
        var run = SymbolId(store, "Run", "src/Consumer.cs");
        var transmit = SymbolId(store, "Transmit", "src/Device.cs");

        var sites = new GraphQueryService(store.Connection)
            .GetEdgeCallSites(run, transmit, ReferenceKind.Call);

        var site = Assert.Single(sites);
        Assert.Equal("device", site.ReceiverText);
        Assert.Equal("(1)", site.ArgumentText);
    }
}
