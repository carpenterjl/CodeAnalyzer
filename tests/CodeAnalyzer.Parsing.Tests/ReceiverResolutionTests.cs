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
    /// <para>
    /// The receiver here is a parameter, which no pack indexes, so its type is not
    /// established and this stays a test of the tier rule alone. Where the type
    /// <em>is</em> established the answer is better than honest — see
    /// <see cref="ATypedReceiverPicksItsOwnClassesMethodExactly"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACallThroughAnUntypedReceiverIsNotClaimedExactByTheSameFileTier()
    {
        WriteFile("src/Session.cs", """
            public class Session
            {
                public void Index(int a) { }

                public void Apply(Orchestrator orchestrator)
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
    /// The other half of the same story. Storing the receiver stopped the false edge from
    /// being called exact; reading its declared type finds the true one. Both facts were
    /// already in the index — the receiver on the reference, the type on the field — and
    /// joining them is what the language itself does when it resolves a member access.
    /// </summary>
    [Fact]
    public async Task ATypedReceiverPicksItsOwnClassesMethodExactly()
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
        var graph = new GraphQueryService(store.Connection);

        // The receiver's type names Orchestrator, so Orchestrator.Index is the answer —
        // not one of two, which is all the tiers could say.
        var orchestratorIndex = SymbolId(store, "Index", "src/Orchestrator.cs");
        var real = Assert.Single(graph.GetDetail(orchestratorIndex)!.Callers, c => c.Name == "Apply");
        Assert.Equal(EdgeConfidence.Unique, real.Confidence);

        // And the same-named method on the calling class is no longer a candidate at all.
        var sessionIndex = SymbolId(store, "Index", "src/Session.cs");
        Assert.DoesNotContain(graph.GetDetail(sessionIndex)!.Callers, c => c.Name == "Apply");
    }

    /// <summary>
    /// The widening that measurement chose. A receiver that <em>is</em> a type name needs no
    /// declaration anywhere — it is a static or enum member access, and the type is the
    /// receiver. On this workspace that is 1,589 references against 4 the old same-file rule
    /// already had, which is why it beat the base-class and partial-class cases the previous
    /// round's report had nominated.
    /// </summary>
    [Fact]
    public async Task AReceiverThatIsATypeNameResolvesToThatTypesMember()
    {
        WriteFile("src/Kinds.cs", """
            public enum SymbolKind
            {
                Method,
            }
            """);
        WriteFile("src/Other.cs", """
            public class Decoy
            {
                public int Method;
            }
            """);
        WriteFile("src/Use.cs", """
            public class User
            {
                public void Run()
                {
                    var k = SymbolKind.Method;
                }
            }
            """);

        var store = await IndexAsync();
        var graph = new GraphQueryService(store.Connection);

        var enumMember = SymbolId(store, "Method", "src/Kinds.cs");
        var real = Assert.Single(graph.GetDetail(enumMember)!.Callers, c => c.Name == "Run");
        Assert.Equal(EdgeConfidence.Unique, real.Confidence);

        // The field that merely shares the name is not a candidate: the receiver named a
        // type, and Decoy is not it.
        var decoy = SymbolId(store, "Method", "src/Other.cs");
        Assert.DoesNotContain(graph.GetDetail(decoy)!.Callers, c => c.Name == "Run");
    }

    /// <summary>
    /// <c>this</c> is the one receiver whose type needs no lookup at all — it is whatever
    /// type the reference is written inside.
    /// </summary>
    [Fact]
    public async Task AThisReceiverMeansTheTypeTheCallIsWrittenInside()
    {
        WriteFile("src/Holder.cs", """
            public class Holder
            {
                public void Target(int a) { }

                public void Run()
                {
                    this.Target(1);
                }
            }
            """);
        WriteFile("src/Elsewhere.cs", """
            public class Elsewhere
            {
                public void Target(int a) { }
            }
            """);

        var store = await IndexAsync();
        var graph = new GraphQueryService(store.Connection);

        // Elsewhere.Target shares the name and loses: `this` says Holder.
        var elsewhere = SymbolId(store, "Target", "src/Elsewhere.cs");
        Assert.DoesNotContain(graph.GetDetail(elsewhere)!.Callers, c => c.Name == "Run");
    }

    /// <summary>
    /// Rank is precedence, not a vote. A local shadowing a type name must be read as the
    /// local, which is what the language does — so the declaration rank has to be consulted
    /// to the exclusion of the type-name rank, not averaged with it.
    /// </summary>
    [Fact]
    public async Task ALocalShadowingATypeNameIsReadAsTheLocal()
    {
        WriteFile("src/Config.cs", """
            public class Config
            {
                public void Load(int a) { }
            }
            """);
        WriteFile("src/Real.cs", """
            public class Real
            {
                public void Load(int a) { }
            }
            """);
        WriteFile("src/Caller.cs", """
            public class Caller
            {
                public void Run()
                {
                    Real Config = new Real();
                    Config.Load(1);
                }
            }
            """);

        var store = await IndexAsync();
        var graph = new GraphQueryService(store.Connection);

        // The declaration says Real, and it outranks the type that happens to be called
        // Config — so Config.Load is not the answer.
        var shadowedType = SymbolId(store, "Load", "src/Config.cs");
        Assert.DoesNotContain(graph.GetDetail(shadowedType)!.Callers, c => c.Name == "Run");

        var real = SymbolId(store, "Load", "src/Real.cs");
        Assert.Contains(graph.GetDetail(real)!.Callers, c => c.Name == "Run");
    }

    /// <summary>
    /// This workspace's own shape, and the one the v4 report predicted would be fixed by
    /// reading the receiver's type — it very nearly was not. Every real call here goes
    /// through <c>var orchestrator = new IndexOrchestrator(…)</c>, whose declared type is
    /// the word <c>var</c>; the type is in the constructor call beside it.
    /// </summary>
    [Fact]
    public async Task AVarReceiverTakesItsTypeFromTheConstructorCall()
    {
        WriteFile("src/Session.cs", """
            public class Session
            {
                public void Index(int a) { }

                public void Apply()
                {
                    var orchestrator = new Orchestrator();
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

        var orchestratorIndex = SymbolId(store, "Index", "src/Orchestrator.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(orchestratorIndex);
        var real = Assert.Single(detail!.Callers, c => c.Name == "Apply");
        Assert.Equal(EdgeConfidence.Unique, real.Confidence);
    }

    /// <summary>
    /// The inference reads a constructor call and nothing else. A <c>var</c> initialised
    /// from a method call establishes no type, and must leave the reference where the
    /// tiers put it rather than inventing one.
    /// </summary>
    [Fact]
    public async Task AVarInitialisedFromACallEstablishesNoType()
    {
        WriteFile("src/Session.cs", """
            public class Session
            {
                public void Index(int a) { }

                public void Apply()
                {
                    var orchestrator = Build();
                    orchestrator.Index(1);
                }

                public Orchestrator Build() { return new Orchestrator(); }
            }
            """);
        WriteFile("src/Orchestrator.cs", """
            public class Orchestrator
            {
                public void Index(int a) { }
            }
            """);

        var store = await IndexAsync();
        var graph = new GraphQueryService(store.Connection);

        // Both same-named methods stay candidates, and both say so.
        var orchestratorIndex = SymbolId(store, "Index", "src/Orchestrator.cs");
        var candidate = Assert.Single(graph.GetDetail(orchestratorIndex)!.Callers, c => c.Name == "Apply");
        Assert.Equal(EdgeConfidence.Ambiguous, candidate.Confidence);
    }

    /// <summary>
    /// The preference must stay a preference. A receiver whose type matches no container —
    /// an external type, a generic, a var — leaves the candidate set exactly as the tiers
    /// found it rather than emptying it.
    /// </summary>
    [Fact]
    public async Task AReceiverWhoseTypeMatchesNothingChangesNothing()
    {
        WriteFile("src/Client.cs", """
            public class Client
            {
                private System.Net.Http.HttpClient transport = new System.Net.Http.HttpClient();

                public void Send()
                {
                    transport.Dispatch(1);
                }
            }
            """);
        WriteFile("src/Relay.cs", """
            public class Relay
            {
                public void Dispatch(int a) { }
            }
            """);

        var store = await IndexAsync();

        // Nothing in the workspace is named HttpClient, so the type settles nothing and
        // the one candidate the tiers found survives.
        var dispatch = SymbolId(store, "Dispatch", "src/Relay.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(dispatch);
        Assert.Contains(detail!.Callers, c => c.Name == "Send");
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
