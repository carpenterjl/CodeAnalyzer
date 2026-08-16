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
/// Markup and its code-behind sharing one graph: the <c>x:Class</c> reference resolving
/// through the full pipeline to the C# class it names, at the confidence a cross-language
/// name match honestly deserves — and without costing the class its unambiguous exact
/// lookup, which is what naming the markup root by the short name would have done.
/// </summary>
public class XamlResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeanalyzer-xclass", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private SqliteIndexStore? _store;

    public XamlResolutionTests()
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

    private const string Markup = """
        <Window x:Class="Demo.Views.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid>
                <TextBox x:Name="SearchBox" />
            </Grid>
        </Window>
        """;

    private const string CodeBehind = """
        namespace Demo.Views;

        public partial class MainWindow
        {
            public MainWindow() { }
        }
        """;

    [Fact]
    public async Task TheMarkupRootAppearsAmongTheClasssCallersAsACrossLanguageMatch()
    {
        WriteFile("Views/MainWindow.xaml", Markup);
        WriteFile("Views/MainWindow.xaml.cs", CodeBehind);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var classId = search.Search("MainWindow")
            .First(h => h.Name == "MainWindow" && h.Kind == SymbolKind.Class).SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(classId);

        var fromMarkup = Assert.Single(detail!.Callers, c => c.Name == "Demo.Views.MainWindow");
        Assert.Equal(ReferenceKind.TypeUse, fromMarkup.ReferenceKind);
        // Cross-language is as far as the index can honestly go: it never checked the
        // namespace against a folder, so this must not present as an exact edge.
        Assert.Equal(EdgeConfidence.Weak, fromMarkup.Confidence);
    }

    [Fact]
    public async Task TheShortNameStillLocatesExactlyOneDefinition()
    {
        WriteFile("Views/MainWindow.xaml", Markup);
        WriteFile("Views/MainWindow.xaml.cs", CodeBehind);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        // The markup root is stored under its qualified name precisely so that nothing
        // new carries the bare name: whole-name matching must still see one class (plus
        // its own constructor, which the locator already absorbs), and no markup element.
        var named = search.Search("MainWindow").Where(h => h.Name == "MainWindow").ToList();
        Assert.DoesNotContain(named, h => h.Kind == SymbolKind.MarkupElement);

        var hit = Assert.Single(named, h => h.Kind == SymbolKind.Class);
        Assert.Equal("Views/MainWindow.xaml.cs", hit.RelativePath);
    }
}
