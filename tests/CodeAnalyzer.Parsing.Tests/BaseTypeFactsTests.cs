using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The fact sheet's inheritance lines. The data predates the surface — every pack has
/// captured inherit references since it was written — so what these tests pin is the
/// reporting rule: the base list appears as written, the resolved half located, the
/// unresolved half named rather than hidden, and the derived list agrees with the
/// caller list it is filtered from.
/// </summary>
public class BaseTypeFactsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "codeanalyzer-basetypes", Guid.NewGuid().ToString("N"));

    private SqliteIndexStore? _store;

    public BaseTypeFactsTests() => Directory.CreateDirectory(_root);

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

    private long SymbolId(SqliteIndexStore store, string name)
    {
        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        return search.Search(name).First(h => h.Name == name).SymbolId;
    }

    [Fact]
    public async Task TheBaseListAppearsAsWrittenResolvedOrNot()
    {
        WriteFile("src/Widget.cs", """
            public class Control { }
            public class Widget : Control, IDisposable
            {
                public void Dispose() { }
            }
            """);

        var store = await IndexAsync();
        var detail = new GraphQueryService(store.Connection).GetDetail(SymbolId(store, "Widget"));

        Assert.Equal(["Control", "IDisposable"], detail!.BaseTypes.Select(b => b.Name));

        var control = detail.BaseTypes[0];
        Assert.NotNull(control.TargetId);
        Assert.Equal("src/Widget.cs", control.TargetPath);

        // IDisposable lives outside the workspace: named, located nowhere, hidden never.
        var disposable = detail.BaseTypes[1];
        Assert.Null(disposable.TargetId);
    }

    [Fact]
    public async Task DerivedTypesAreTheInheritCallersAndNothingMore()
    {
        WriteFile("src/Base.cs", """
            public class Base
            {
                public void Helper() { }
            }
            """);
        WriteFile("src/Derived.cs", "public class Derived : Base { }");
        WriteFile("src/User.cs", """
            public class User
            {
                void Run(Base b) { b.Helper(); }
            }
            """);

        var store = await IndexAsync();
        var detail = new GraphQueryService(store.Connection).GetDetail(SymbolId(store, "Base"));

        // User references Base (parameter type) but does not derive from it.
        var derived = Assert.Single(detail!.DerivedTypes);
        Assert.Equal("Derived", derived.Name);
    }

    [Fact]
    public async Task ATypeWithNoBaseListClaimsNone()
    {
        WriteFile("src/Plain.cs", "public class Plain { }");

        var store = await IndexAsync();
        var detail = new GraphQueryService(store.Connection).GetDetail(SymbolId(store, "Plain"));

        Assert.Empty(detail!.BaseTypes);
        Assert.Empty(detail.DerivedTypes);
    }
}
