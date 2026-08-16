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

    [Fact]
    public async Task ABindingPathResolvesToTheCSharpPropertyAsACrossLanguageMatch()
    {
        WriteFile("Views/SearchPanel.xaml", """
            <UserControl x:Class="Demo.Views.SearchPanel"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <TextBox x:Name="SearchBox" Text="{Binding SearchQuery, Mode=TwoWay}" />
            </UserControl>
            """);
        WriteFile("ViewModels/MainViewModel.cs", """
            namespace Demo.ViewModels;

            public class MainViewModel
            {
                public string SearchQuery { get; set; } = "";
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var property = search.Search("SearchQuery")
            .First(h => h.Name == "SearchQuery" && h.Kind == SymbolKind.Property).SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(property);

        // The markup never names MainViewModel, so cross-language name match is all the
        // index can honestly claim — but the edge exists, and it is owned by the element
        // the binding is written on.
        var caller = Assert.Single(detail!.Callers, c => c.Name == "SearchBox");
        Assert.Equal(ReferenceKind.Binding, caller.ReferenceKind);
        Assert.Equal(EdgeConfidence.Weak, caller.Confidence);
    }

    [Fact]
    public async Task ATypedTemplateBindingLandsOnItsDeclaredTypeDespiteARival()
    {
        // The rival is the point of this fixture (a lesson round eight bought): with one
        // Descriptor in the workspace the edge is unique for lack of competition and the
        // assertion proves nothing. Here two types carry the property, the template
        // declares which one it binds, and the declaration must beat the tie.
        WriteFile("Views/ResultList.xaml", """
            <UserControl x:Class="Demo.Views.ResultList"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <UserControl.Resources>
                    <DataTemplate x:Key="RowTemplate" DataType="{x:Type vm:SearchResultItem}">
                        <TextBlock x:Name="Row" Text="{Binding Descriptor}" />
                    </DataTemplate>
                </UserControl.Resources>
            </UserControl>
            """);
        WriteFile("ViewModels/SearchResultItem.cs", """
            namespace Demo.ViewModels;

            public class SearchResultItem
            {
                public string Descriptor { get; set; } = "";
            }
            """);
        WriteFile("ViewModels/OverloadItem.cs", """
            namespace Demo.ViewModels;

            public class OverloadItem
            {
                public string Descriptor { get; set; } = "";
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var graph = new GraphQueryService(store.Connection);
        var hits = search.Search("Descriptor").Where(h => h.Kind == SymbolKind.Property).ToList();
        var declared = hits.First(h => h.RelativePath == "ViewModels/SearchResultItem.cs").SymbolId;
        var rival = hits.First(h => h.RelativePath == "ViewModels/OverloadItem.cs").SymbolId;

        // One edge, to the declared type's property. This asserted Weak when M25.2 wrote
        // it — the edge crosses a language boundary, and that was the whole of the rule.
        // M26.2 separated the two things the rung was conflating: crossing a language is
        // only weak evidence when the name is all there was, and here the markup names
        // the type, so the edge is as good as any same-language unique match. The rival
        // below is what makes that claim checkable rather than generous.
        var caller = Assert.Single(graph.GetDetail(declared)!.Callers, c => c.Name == "Row");
        Assert.Equal(ReferenceKind.Binding, caller.ReferenceKind);
        Assert.Equal(EdgeConfidence.Unique, caller.Confidence);

        Assert.Empty(graph.GetDetail(rival)!.Callers);
    }

    /// <summary>
    /// The other side of M26.2's rule, and the reason it is a distinction rather than a
    /// promotion: a binding with no declared context has only the name, so its edge stays
    /// Weak. If both shapes came out Unique the confidence column would have stopped
    /// carrying information the moment the rung was widened.
    /// </summary>
    [Fact]
    public async Task ABindingWithNoDeclaredContextStaysWeak()
    {
        WriteFile("Views/Loose.xaml", """
            <UserControl x:Class="Demo.Views.Loose"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <UserControl.Resources>
                    <DataTemplate x:Key="RowTemplate">
                        <TextBlock x:Name="Row" Text="{Binding Descriptor}" />
                    </DataTemplate>
                </UserControl.Resources>
            </UserControl>
            """);
        WriteFile("ViewModels/SearchResultItem.cs", """
            namespace Demo.ViewModels;

            public class SearchResultItem
            {
                public string Descriptor { get; set; } = "";
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var declared = search.Search("Descriptor")
            .First(h => h.Kind == SymbolKind.Property).SymbolId;

        var caller = Assert.Single(
            new GraphQueryService(store.Connection).GetDetail(declared)!.Callers,
            c => c.Name == "Row");
        Assert.Equal(EdgeConfidence.Weak, caller.Confidence);
    }

    [Fact]
    public async Task AStaticResourceKeyResolvesToTheKeyedElement()
    {
        WriteFile("Views/Panel.xaml", """
            <UserControl x:Class="Demo.Views.Panel"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <UserControl.Resources>
                    <SolidColorBrush x:Key="PanelBrush" Color="#202020" />
                </UserControl.Resources>
                <Border x:Name="Chrome" Background="{StaticResource PanelBrush}" />
            </UserControl>
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var brush = search.Search("PanelBrush")
            .First(h => h.Name == "PanelBrush").SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(brush);

        // Same language, same file: this one is not even a weak claim.
        var caller = Assert.Single(detail!.Callers, c => c.Name == "Chrome");
        Assert.Equal(ReferenceKind.Resource, caller.ReferenceKind);
        Assert.Equal(EdgeConfidence.Unique, caller.Confidence);
    }

    /// <summary>
    /// This workspace's own bug, reduced. <c>Style="{StaticResource SearchBox}"</c> written
    /// on <c>&lt;TextBox x:Name="SearchBox"&gt;</c> used to resolve to the TextBox, because
    /// a key and an element name shared one symbol kind and the element was the nearer
    /// candidate. The reference and its target being the same symbol made the edge a
    /// self-edge, which the display rule hides — so the wrong answer was not merely wrong,
    /// it was silent. Split, the lookup can only see the key.
    /// </summary>
    [Fact]
    public async Task AResourceLookupCannotLandOnAnElementSharingItsName()
    {
        WriteFile("Themes/Controls.xaml", """
            <ResourceDictionary xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Style x:Key="SearchBox" TargetType="TextBox">
                    <Setter Property="Padding" Value="0,5" />
                </Style>
            </ResourceDictionary>
            """);
        WriteFile("Views/Main.xaml", """
            <UserControl x:Class="Demo.Views.Main"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <TextBox x:Name="SearchBox" Style="{StaticResource SearchBox}" />
            </UserControl>
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        var graph = new GraphQueryService(store.Connection);

        // The Style two files away is what the lookup names, and it says so.
        var style = search.Search("SearchBox")
            .First(h => h.Name == "SearchBox" && h.RelativePath == "Themes/Controls.xaml").SymbolId;
        var styleCallers = graph.GetDetail(style)!.Callers;
        Assert.Contains(styleCallers, c => c.ReferenceKind == ReferenceKind.Resource);

        // And the element that merely shares the word is not a resource target at all.
        var element = search.Search("SearchBox")
            .First(h => h.Name == "SearchBox" && h.RelativePath == "Views/Main.xaml").SymbolId;
        Assert.DoesNotContain(graph.GetDetail(element)!.Callers,
            c => c.ReferenceKind == ReferenceKind.Resource);
    }

    /// <summary>
    /// An event handler reaches the code-behind method it names. Cross-language, so the
    /// edge is a name match and says so.
    /// </summary>
    [Fact]
    public async Task AnEventHandlerReachesItsCodeBehindMethod()
    {
        WriteFile("Views/Panel.xaml", """
            <UserControl x:Class="Demo.Views.Panel"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Button x:Name="Accept" Click="OnAccept" />
            </UserControl>
            """);
        WriteFile("Views/Panel.xaml.cs", """
            namespace Demo.Views;

            public partial class Panel
            {
                private void OnAccept(object sender, System.EventArgs e) { }
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var handler = search.Search("OnAccept").First(h => h.Name == "OnAccept").SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(handler);

        var caller = Assert.Single(detail!.Callers, c => c.Name == "Accept");
        Assert.Equal(ReferenceKind.Handler, caller.ReferenceKind);
    }

    /// <summary>
    /// The pack nominates on a naming convention, so it nominates wrongly — and this is
    /// what makes that harmless. <c>IsExpanded="True"</c> matches the attribute rule and
    /// the value rule both, and still resolves to nothing, because no method on the
    /// code-behind is called <c>True</c>. It is not reported as unresolved either.
    /// </summary>
    [Fact]
    public async Task AnAttributeThatMerelyLooksLikeAHandlerResolvesToNothingAndIsNotReported()
    {
        WriteFile("Views/Tree.xaml", """
            <UserControl x:Class="Demo.Views.Tree"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <TreeViewItem x:Name="Root" IsExpanded="True" MouseEnter="OnHover" />
            </UserControl>
            """);
        WriteFile("Views/Tree.xaml.cs", """
            namespace Demo.Views;

            public partial class Tree
            {
                private void OnHover(object sender, System.EventArgs e) { }
            }
            """);
        // A decoy elsewhere carrying the tempting name, to prove the restriction is the
        // code-behind class and not merely "no method called True exists".
        WriteFile("Other/Flags.cs", """
            namespace Demo.Other;

            public class Flags
            {
                public void True() { }
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();
        var graph = new GraphQueryService(store.Connection);

        var decoy = search.Search("True").First(h => h.Name == "True").SymbolId;
        Assert.Empty(graph.GetDetail(decoy)!.Callers);

        // The real handler on the same element still lands, and the misfire is silent.
        var root = search.Search("Root").First(h => h.Name == "Root").SymbolId;
        var rootDetail = graph.GetDetail(root)!;
        Assert.Contains(rootDetail.Callees, c => c.Name == "OnHover");
        Assert.DoesNotContain(rootDetail.UnresolvedReferences, u => u.Name == "True");
    }

    [Fact]
    public async Task AMisspelledBindingPathIsListedAsUnresolvedNotDropped()
    {
        WriteFile("Views/SearchPanel.xaml", """
            <UserControl x:Class="Demo.Views.SearchPanel"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <TextBox x:Name="SearchBox" Text="{Binding SerachQuery}" />
            </UserControl>
            """);
        WriteFile("ViewModels/MainViewModel.cs", """
            namespace Demo.ViewModels;

            public class MainViewModel
            {
                public string SearchQuery { get; set; } = "";
            }
            """);

        var store = await IndexAsync();
        var search = new SymbolSearchService(store.Connection);
        search.Reload();

        var box = search.Search("SearchBox").First(h => h.Name == "SearchBox").SymbolId;
        var detail = new GraphQueryService(store.Connection).GetDetail(box);

        // This is the binding checker's territory becoming a query: the typo surfaces
        // on the element's own fact sheet instead of needing a reflection pass.
        Assert.Contains(detail!.UnresolvedReferences,
            u => u.Name == "SerachQuery" && u.Kind == ReferenceKind.Binding);
    }
}
