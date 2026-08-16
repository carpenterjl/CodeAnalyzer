using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// A bare-identifier use binds only to what can be named from another scope, and these
/// tests pin both halves of what that means. A member of a <em>type</em> — a class's
/// constant, an enum's member — exists to be named elsewhere and must resolve. A local
/// declared inside a <em>function</em> must not, or every <c>i</c> in the workspace
/// becomes a candidate for every other <c>i</c>.
/// <para>
/// One rule used to do both jobs by requiring the target be file-scope or the caller's own
/// member, which kept the locals out and took every type member with them: this
/// workspace's own <c>SymbolKind.MarkupElement</c> reported no callers while six call
/// sites read it. Found by asking the index about the code that was about to change it.
/// </para>
/// </summary>
public class MemberAddressabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeanalyzer-members", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private SqliteIndexStore? _store;

    public MemberAddressabilityTests()
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

    private IReadOnlyList<string> CallerNames(SqliteIndexStore store, string name, string relativePath)
    {
        var detail = new GraphQueryService(store.Connection).GetDetail(SymbolId(store, name, relativePath));
        return detail!.Callers.Select(c => c.Name).ToList();
    }

    /// <summary>
    /// Callers of a symbol that search cannot reach. A local is indexed as a member of the
    /// method that declares it but is deliberately kept out of search results — nobody
    /// wants to look up <c>i</c> — so the only way to it is through its container.
    /// </summary>
    private IReadOnlyList<string> CallerNamesOfLocal(
        SqliteIndexStore store, string owner, string relativePath, string local)
    {
        var graph = new GraphQueryService(store.Connection);
        var ownerDetail = graph.GetDetail(SymbolId(store, owner, relativePath));
        var member = ownerDetail!.Members.First(m => m.Name == local);
        return graph.GetDetail(member.Id)!.Callers.Select(c => c.Name).ToList();
    }

    /// <summary>
    /// The reduced form of this workspace's own case: a constant on a static class, read
    /// through its qualified name from another file.
    /// </summary>
    [Fact]
    public async Task AConstantOnAClassIsReachableFromAnotherFile()
    {
        WriteFile("src/Limits.cs", """
            public static class Limits
            {
                public const int MaxLength = 120;
            }
            """);
        WriteFile("src/Writer.cs", """
            public class Writer
            {
                public int Cap() { return Limits.MaxLength; }
            }
            """);

        var store = await IndexAsync();

        Assert.Contains("Cap", CallerNames(store, "MaxLength", "src/Limits.cs"));
    }

    /// <summary>
    /// <c>Kind.Element</c> is the shape that made the gap visible — an enum member whose
    /// container is the enum, addressed by a qualified name from elsewhere.
    /// </summary>
    [Fact]
    public async Task AnEnumMemberIsReachableThroughItsQualifiedName()
    {
        WriteFile("src/Kind.cs", """
            public enum Kind
            {
                Element = 1,
            }
            """);
        WriteFile("src/Rule.cs", """
            public class Rule
            {
                public Kind Pick() { return Kind.Element; }
            }
            """);

        var store = await IndexAsync();

        Assert.Contains("Pick", CallerNames(store, "Element", "src/Kind.cs"));
    }

    /// <summary>
    /// A field is reachable the same way, and this is the case with the most room to go
    /// wrong: fields are the most numerous members and share names most freely.
    /// </summary>
    [Fact]
    public async Task AFieldOnAClassIsReachableFromAnotherFile()
    {
        WriteFile("src/Config.cs", """
            public class Config
            {
                public int RetryBudget = 3;
            }
            """);
        WriteFile("src/Caller.cs", """
            public class Caller
            {
                private Config config = new Config();
                public int Budget() { return config.RetryBudget; }
            }
            """);

        var store = await IndexAsync();

        Assert.Contains("Budget", CallerNames(store, "RetryBudget", "src/Config.cs"));
    }

    /// <summary>
    /// The guard the old rule existed for, which must survive the change: a local in one
    /// function is not a target for a same-named local in another.
    /// </summary>
    [Fact]
    public async Task ALocalInAnotherFunctionIsNotAReferenceTarget()
    {
        WriteFile("src/First.cs", """
            public class First
            {
                public int Run()
                {
                    int counter = 0;
                    return counter;
                }
            }
            """);
        WriteFile("src/Second.cs", """
            public class Second
            {
                public int Go()
                {
                    int counter = 1;
                    return counter;
                }
            }
            """);

        var store = await IndexAsync();

        // First.Run's local is read by First.Run and by nothing else in the workspace.
        Assert.DoesNotContain("Go", CallerNamesOfLocal(store, "Run", "src/First.cs", "counter"));
    }

    /// <summary>
    /// The same guard in a second language, because the rule is written once in SQL and
    /// applies to every pack. C nests locals under the function exactly as C# does, so a
    /// change that let one leak would let both.
    /// </summary>
    [Fact]
    public async Task ACLocalInAnotherFunctionIsNotAReferenceTarget()
    {
        WriteFile("src/left.c", "int take(void) { int amount = 1; return amount; }\n");
        WriteFile("src/right.c", "int give(void) { int amount = 2; return amount; }\n");

        var store = await IndexAsync();

        Assert.DoesNotContain("give", CallerNamesOfLocal(store, "take", "src/left.c", "amount"));
    }
}
