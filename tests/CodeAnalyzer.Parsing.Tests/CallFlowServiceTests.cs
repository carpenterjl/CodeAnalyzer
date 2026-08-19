using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The depth-first trace behind the flow view and the <c>flow</c> verb, run over the real
/// pipeline like <see cref="GraphFragmentTests"/>.
/// <para>
/// The rules under test are the ones a reader stakes decisions on: steps come in source
/// order, a repeated subtree collapses only to a drawing that is actually complete,
/// recursion never re-expands, an unresolved call is an explicit leaf rather than a
/// missing row, and every cap that fires is stated on the result.
/// </para>
/// </summary>
public class CallFlowServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-flow", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceSession _session;

    public CallFlowServiceTests()
    {
        Directory.CreateDirectory(_root);

        // Main's three calls sit on lines whose source order the trace must keep.
        // ReadText has no definition anywhere: an unresolved leaf. Parse is called from
        // two places, so its second occurrence exercises the collapse rule.
        WriteFile("app/Program.cs", """
            public class Program
            {
                public static void Main()
                {
                    var cfg = LoadConfig();
                    Process(cfg);
                    Save(cfg);
                }

                public static Config LoadConfig()
                {
                    var text = ReadText();
                    return Parse(text);
                }

                public static Config Parse(string text)
                {
                    Validate(text);
                    return null;
                }

                public static void Validate(string text) { }

                public static void Process(Config cfg)
                {
                    Validate(null);
                    var again = Parse(null);
                }

                public static void Save(Config cfg) { }
            }

            public class Config { }
            """);

        WriteFile("app/Rec.cs", """
            public class Rec
            {
                public static void Direct() { Direct(); }
                public static void Alpha() { Beta(); }
                public static void Beta() { Alpha(); }
            }
            """);

        WriteFile("app/Dup.cs", """
            public class DupOne { public static void Dup() { } }
            public class DupTwo { public static void Dup() { } }
            public class DupCaller
            {
                public static void CallsDup() { Dup(); }
            }
            """);

        _session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
    }

    public void Dispose()
    {
        _session.Dispose();
        WorkspaceCacheCleanup.Delete(_root);
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

    private async Task<long> IndexAndFindAsync(string name)
    {
        await _session.IndexAsync([string.Empty]);

        var hit = _session.Search
            .Search(name)
            .FirstOrDefault(h => h.Name == name && h.Kind == SymbolKind.Method);

        Assert.True(hit is not null, $"'{name}' was not indexed as a method");
        return hit!.SymbolId;
    }

    [Fact]
    public async Task StepsComeInSourceOrderWithTheirFates()
    {
        var flow = _session.CallFlows.GetCallFlow(await IndexAndFindAsync("Main"));

        Assert.True(flow.RootExists);
        Assert.Equal(
            new[] { "LoadConfig", "Process", "Save" },
            flow.Steps.Select(s => s.Name).ToArray());
        Assert.Equal(new[] { "1", "2", "3" }, flow.Steps.Select(s => s.Ordinal).ToArray());

        var load = flow.Steps[0];
        Assert.Equal(ResultFate.Assigned, load.Fate);
        Assert.Equal("cfg", load.FateName);
        Assert.Equal(ResultFate.Discarded, flow.Steps[1].Fate);
    }

    [Fact]
    public async Task AnUnresolvedCallIsAnExplicitLeaf()
    {
        var flow = _session.CallFlows.GetCallFlow(await IndexAndFindAsync("Main"));

        var readText = flow.Steps[0].Children.Single(s => s.Name == "ReadText");
        Assert.True(readText.IsUnresolved);
        Assert.Null(readText.TargetId);
        Assert.Empty(readText.Children);
    }

    [Fact]
    public async Task ARepeatedTargetCollapsesToItsFullDrawing()
    {
        var flow = _session.CallFlows.GetCallFlow(await IndexAndFindAsync("Main"), depth: 3);

        var firstParse = flow.Steps[0].Children.Single(s => s.Name == "Parse");
        Assert.Null(firstParse.CollapsedAt);
        Assert.Single(firstParse.Children); // Validate

        var secondParse = flow.Steps[1].Children.Single(s => s.Name == "Parse");
        Assert.Equal(firstParse.Ordinal, secondParse.CollapsedAt);
        Assert.Empty(secondParse.Children);
    }

    [Fact]
    public async Task ACutDrawingDoesNotRegisterForCollapse()
    {
        // At depth 2 Parse's own body is below the horizon, so its first occurrence is
        // truncated — and the second occurrence must therefore not point at it.
        var flow = _session.CallFlows.GetCallFlow(await IndexAndFindAsync("Main"), depth: 2);

        var firstParse = flow.Steps[0].Children.Single(s => s.Name == "Parse");
        Assert.True(firstParse.ChildrenTruncated);
        Assert.Equal(1, firstParse.CallSitesInBody);

        var secondParse = flow.Steps[1].Children.Single(s => s.Name == "Parse");
        Assert.Null(secondParse.CollapsedAt);
        Assert.True(secondParse.ChildrenTruncated);
        Assert.True(flow.WasTruncated);
    }

    [Fact]
    public async Task DirectAndMutualRecursionAreMarkedAndNeverReExpanded()
    {
        var direct = _session.CallFlows.GetCallFlow(await IndexAndFindAsync("Direct"));
        var self = Assert.Single(direct.Steps);
        Assert.True(self.IsCycle);
        Assert.Empty(self.Children);

        var mutual = _session.CallFlows.GetCallFlow(await IndexAndFindAsync("Alpha"), depth: 5);
        var beta = Assert.Single(mutual.Steps);
        Assert.False(beta.IsCycle);
        var back = Assert.Single(beta.Children);
        Assert.Equal("Alpha", back.Name);
        Assert.True(back.IsCycle);
        Assert.Empty(back.Children);
    }

    [Fact]
    public async Task AnAmbiguousStepFollowsOneCandidateAndCarriesTheRest()
    {
        var flow = _session.CallFlows.GetCallFlow(await IndexAndFindAsync("CallsDup"));

        var step = Assert.Single(flow.Steps);
        Assert.Equal(EdgeConfidence.Ambiguous, step.Confidence);
        Assert.NotNull(step.TargetId);
        var other = Assert.Single(step.OtherCandidates);
        Assert.NotEqual(step.TargetId, other.Id);
    }

    [Fact]
    public async Task APinRedirectsTheStepToTheChosenCandidate()
    {
        var rootId = await IndexAndFindAsync("CallsDup");
        var unpinned = _session.CallFlows.GetCallFlow(rootId);
        var step = Assert.Single(unpinned.Steps);
        var other = Assert.Single(step.OtherCandidates);

        var pinned = _session.CallFlows.GetCallFlow(
            rootId, pins: new Dictionary<long, long> { [step.RefId] = other.Id });

        var repinned = Assert.Single(pinned.Steps);
        Assert.Equal(other.Id, repinned.TargetId);
        Assert.Contains(repinned.OtherCandidates, c => c.Id == step.TargetId);
    }

    [Fact]
    public async Task TheStepBudgetCutsLoudly()
    {
        // A tightened cap needs its own service instance, so this test runs the pipeline
        // against a private store the way IndexStoreTests does.
        var databasePath = Path.Combine(_root, ".flow-index", "index.db");
        using var store = SqliteIndexStore.Open(databasePath, _root);
        store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var orchestrator = new IndexOrchestrator(
            new FileCrawler(factory.IsSupportedExtension), factory);
        await orchestrator.IndexAsync(
            WorkspaceSelection.EntireWorkspace(_root), store, incrementalGate: null);
        new ReferenceResolver(store.Connection).ResolveAll();

        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        var rootId = search.Search("Main").First(h => h.Name == "Main").SymbolId;

        var service = new CallFlowService(store.Connection) { MaxSteps = 2 };
        var flow = service.GetCallFlow(rootId, depth: 3);

        Assert.Equal(2, flow.TotalSteps);
        Assert.True(flow.WasTruncated);
        Assert.True(flow.RootTruncated);
        Assert.Equal(3, flow.RootCallSites);
    }

    [Fact]
    public async Task AUserMarkStampsItsStepAsABoundary()
    {
        var rootId = await IndexAndFindAsync("Main");
        var flow = _session.CallFlows.GetCallFlow(
            rootId,
            io: _session.IoBoundaries,
            catalog: [],
            marks: [new IoMark { Name = "Save", Direction = IoDirection.Output }]);

        var save = flow.Steps.Single(s => s.Name == "Save");
        Assert.True(save.IsIoBoundary);
        Assert.Equal(IoDirection.Output, save.IoDirection);
    }

    [Fact]
    public async Task AMissingRootAnswersAboutTheQuestion()
    {
        await _session.IndexAsync([string.Empty]);
        var flow = _session.CallFlows.GetCallFlow(999_999_999);

        Assert.False(flow.RootExists);
        Assert.Empty(flow.Steps);
    }
}
