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
/// What a container has instead of callers.
/// <para>
/// "Every caller is a test" is the most actionable shape this tool produces — it is how a
/// session proves a feature is implemented and unreachable — and a static factory nobody
/// names by name wears the identical shape while its methods are called from the
/// application. A field report acted on that shape three times correctly and once wrongly
/// in one session, with nothing in the output to tell the four cases apart.
/// </para>
/// </summary>
public class ContainerReachTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeanalyzer-container", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private SqliteIndexStore? _store;

    public ContainerReachTests()
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
    /// A container's caller count is not the sum of its members', and the sheet has to say
    /// so or the zero reads as "dead". This is the exact shape a field report acted on
    /// three times correctly and once wrongly, with nothing in the output to tell the four
    /// cases apart.
    /// </summary>
    [Fact]
    public async Task AContainerNothingNamesStillReportsItsMembersReach()
    {
        WriteFile("app/Caller.cs", """
            public class Caller
            {
                public void Go()
                {
                    Shapes.Factory.Make(1);
                }
            }
            """);
        WriteFile("shapes/Factory.cs", """
            namespace Shapes
            {
                public static class Factory
                {
                    public static object Make(int sides) => null;
                }
            }
            """);

        var store = await IndexAsync();
        var factory = SymbolId(store, "Factory", "shapes/Factory.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(factory);

        Assert.Empty(detail!.Callers);
        Assert.Equal(1, detail.MemberCallerTotal);
    }

    /// <summary>
    /// And the number is not a consolation prize. A container whose members really are
    /// unreached reports nothing extra, so the line's presence is itself the signal.
    /// </summary>
    [Fact]
    public async Task AContainerWhoseMembersAreAlsoUnreachedReportsNoSuchNumber()
    {
        WriteFile("shapes/Unused.cs", """
            namespace Shapes
            {
                public static class Unused
                {
                    public static object Make(int sides) => null;
                }
            }
            """);

        var store = await IndexAsync();
        var unused = SymbolId(store, "Unused", "shapes/Unused.cs");
        var detail = new GraphQueryService(store.Connection).GetDetail(unused);

        Assert.Empty(detail!.Callers);
        Assert.Equal(0, detail.MemberCallerTotal);
    }
}
