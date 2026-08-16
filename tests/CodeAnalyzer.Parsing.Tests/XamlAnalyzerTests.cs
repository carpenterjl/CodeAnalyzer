using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Extraction checks for the XAML query pack, which runs on the HTML grammar because the
/// bundle has no XAML or XML one. Several of these pin the borrowing's edges rather than
/// its successes — what the pack cannot see matters more here than in a pack whose
/// grammar was written for its language.
/// </summary>
public class XamlAnalyzerTests() : LanguagePackFixture(LanguageRegistry.Xaml, "Views/MainWindow.xaml")
{
    private const string Source = """
        <Window x:Class="CodeAnalyzer.App.Views.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Title="CodeAnalyzer" Height="720">
            <Grid>
                <TextBox x:Name="SearchBox" Text="{Binding SearchQuery, Mode=TwoWay}" />
                <Button Name="GoButton" Click="OnGoClicked">Go</Button>
                <ListView x:Name="Results" />
            </Grid>
        </Window>
        """;

    [Fact]
    public void NamedElementsAreTheDeclarationsAndTheTagIsTheType()
    {
        var result = Analyze(Source);

        var box = Symbol(result, "SearchBox");
        Assert.Equal(SymbolKind.MarkupElement, box.Kind);
        Assert.Equal("TextBox", box.TypeText);

        Assert.Equal("ListView", Symbol(result, "Results").TypeText);
    }

    [Fact]
    public void BareNameCountsTheSameAsXName()
    {
        // In WPF they are the same declaration written two ways, and a file mixing them
        // would otherwise be half indexed.
        var result = Analyze(Source);

        Assert.Equal("Button", Symbol(result, "GoButton").TypeText);
    }

    [Fact]
    public void AResourceKeyIsADeclarationOfItsOwnKind()
    {
        var result = Analyze("""
            <ResourceDictionary>
                <SolidColorBrush x:Key="PanelBrush" Color="#202020" />
            </ResourceDictionary>
            """);

        // Not MarkupElement: a key and an element name are different namespaces, and
        // sharing a kind let a StaticResource lookup land on an x:Name.
        Assert.Equal(SymbolKind.ResourceKey, Symbol(result, "PanelBrush").Kind);
    }

    /// <summary>
    /// The distinction, in one file: the same word used both ways declares two symbols of
    /// two kinds, which is what lets a resource lookup pick the right one.
    /// </summary>
    [Fact]
    public void AKeyAndAnElementNameSharingAWordStayTwoDistinctSymbols()
    {
        var result = Analyze("""
            <Grid>
                <Grid.Resources>
                    <SolidColorBrush x:Key="Accent" Color="#202020" />
                </Grid.Resources>
                <Border x:Name="Accent" />
            </Grid>
            """);

        var kinds = result.Symbols.Where(s => s.Name == "Accent").Select(s => s.Kind).ToList();

        Assert.Contains(SymbolKind.ResourceKey, kinds);
        Assert.Contains(SymbolKind.MarkupElement, kinds);
    }

    [Fact]
    public void AStyleKeepsItsKeyEvenThoughItsBodyIsSwallowed()
    {
        // <Style> is HTML's CSS <style>, so the grammar hands back its children as one
        // raw_text blob. The key survives because it is on the tag itself, and the key is
        // what a StaticResource reference is written against.
        var result = Analyze("""
            <ResourceDictionary>
                <Style x:Key="RowButton" TargetType="Button">
                    <Setter Property="Background" Value="Transparent" />
                </Style>
            </ResourceDictionary>
            """);

        Assert.Equal(SymbolKind.ResourceKey, Symbol(result, "RowButton").Kind);
    }

    [Fact]
    public void XClassSplitsIntoASegmentNameAndAQualifierReceiver()
    {
        // The last segment is what a C# class definition can match; the qualifier is the
        // receiver, meaning what it means everywhere else — the locating was done in the
        // source, not by the file the reference sits in. Nothing is shortened away: the
        // two halves together are the verbatim attribute value.
        var result = Analyze(Source);

        var reference = Assert.Single(result.References, r => r.Kind == ReferenceKind.TypeUse);
        Assert.Equal("MainWindow", reference.Name);
        Assert.Equal("CodeAnalyzer.App.Views", reference.ReceiverText);
    }

    [Fact]
    public void TheRootElementIsADeclarationUnderItsVerbatimQualifiedName()
    {
        // Deliberately the full dotted form: a markup element sharing the class's short
        // name would make every exact lookup of the class ambiguous again — the exact
        // regression deleting the stale template folder fixed.
        var result = Analyze(Source);

        var root = Symbol(result, "CodeAnalyzer.App.Views.MainWindow");
        Assert.Equal(SymbolKind.MarkupElement, root.Kind);
        Assert.Equal("Window", root.TypeText);

        // The root spans the file, so the named elements become its members and the
        // outline shows the markup tree rooted at the class it compiles into.
        Assert.Contains("SearchBox", MembersOf(result, "CodeAnalyzer.App.Views.MainWindow"));
    }

    [Fact]
    public void TheXClassReferenceIsOwnedByTheRootElement()
    {
        // Without an owner the reference resolves but appears in no caller list — the
        // round-three report's word for that state was "inert".
        var result = Analyze(Source);

        var reference = Assert.Single(result.References, r => r.Kind == ReferenceKind.TypeUse);
        Assert.NotNull(reference.FromSymbolLocalIndex);

        var owner = result.Symbols[reference.FromSymbolLocalIndex!.Value];
        Assert.Equal("CodeAnalyzer.App.Views.MainWindow", owner.Name);
    }

    [Fact]
    public void ABindingPathIsARealReferenceNamedByItsFirstSegment()
    {
        var result = Analyze(Source);

        var binding = Assert.Single(result.References, r => r.Kind == ReferenceKind.Binding);
        Assert.Equal("SearchQuery", binding.Name);

        // Owned by the innermost named element, so it shows up in a caller list.
        Assert.NotNull(binding.FromSymbolLocalIndex);
        Assert.Equal("SearchBox", result.Symbols[binding.FromSymbolLocalIndex!.Value].Name);
    }

    [Fact]
    public void APlainAttributeIsStillNotAReference()
    {
        var result = Analyze(Source);

        // Title="CodeAnalyzer" names nothing, and no rule here claims it does.
        var referenced = result.References.Select(r => r.Name).ToList();
        Assert.DoesNotContain("CodeAnalyzer", referenced);
        Assert.DoesNotContain("{Binding SearchQuery, Mode=TwoWay}", referenced);
    }

    /// <summary>
    /// A handler is nominated by convention, since XAML marks an event attribute with no
    /// syntax at all. The pack's half of the bargain is only to nominate; the resolver
    /// requires the target be a method on this file's own <c>x:Class</c>, which is what
    /// discards the wrong nominations.
    /// </summary>
    [Fact]
    public void AnEventAttributeWithABareIdentifierIsAHandlerReference()
    {
        var result = Analyze("""
            <Grid>
                <Button x:Name="Go" Click="OnGoClicked" Content="Go" />
            </Grid>
            """);

        var handler = Assert.Single(result.References, r => r.Kind == ReferenceKind.Handler);
        Assert.Equal("OnGoClicked", handler.Name);

        // Content="Go" is a bare identifier too, but Content is not an event name.
        Assert.DoesNotContain(result.References, r => r.Name == "Go");
    }

    /// <summary>An event attribute whose value is not a bare identifier names no method.</summary>
    [Fact]
    public void AnEventAttributeWithANonIdentifierValueIsNotNominated()
    {
        var result = Analyze("""
            <Grid>
                <Button x:Name="Go" IsEnabled="{Binding CanGo}" MouseEnter="{x:Null}" />
            </Grid>
            """);

        Assert.DoesNotContain(result.References, r => r.Kind == ReferenceKind.Handler);
    }

    [Fact]
    public void AStaticResourceKeyIsAResourceReferenceNotABinding()
    {
        // Separate kinds because they resolve into different worlds: a key names a
        // markup element, a path names a property. One kind for both let
        // Style="{StaticResource SearchBox}" resolve to the TextBox named SearchBox —
        // an element name, not a style key — the moment this repo's own MainWindow was
        // indexed.
        var result = Analyze("""
            <Grid>
                <Border Background="{StaticResource PanelBrush}" x:Name="Panel" />
            </Grid>
            """);

        var resource = Assert.Single(result.References, r => r.Kind == ReferenceKind.Resource);
        Assert.Equal("PanelBrush", resource.Name);
        Assert.DoesNotContain(result.References, r => r.Kind == ReferenceKind.Binding);
    }

    [Fact]
    public void ABindingCarriesTheWholeExtensionAsItsArgumentText()
    {
        // The verbatim extension is the evidence a call-site listing shows beside the
        // edge: the claim and its source, on one line.
        var result = Analyze(Source);

        var binding = Assert.Single(result.References, r => r.Kind == ReferenceKind.Binding);
        Assert.Equal("{Binding SearchQuery, Mode=TwoWay}", binding.ArgumentText);
    }

    [Fact]
    public void AnExtensionTheParserCannotReadMakesNoClaim()
    {
        var result = Analyze("""
            <Grid>
                <TextBlock x:Name="Version"
                           Text="{x:Static local:AppInfo.Version}"
                           Foreground="{Binding (Validation.Errors)[0].ErrorContent}" />
            </Grid>
            """);

        // x:Static is not one of the four read extensions; an attached-property path
        // does not start with a plain identifier. Both stay verbatim values.
        Assert.DoesNotContain(result.References, r => r.Kind == ReferenceKind.Binding);
    }

    [Fact]
    public void NamesInsideAPropertyElementSurviveTheParseError()
    {
        // <Grid.RowDefinitions> is valid XAML and invalid HTML, so the grammar reports an
        // error at it. Everything around it still extracts — which is the whole reason
        // this pack is worth having rather than refusing the language.
        var result = Analyze("""
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition x:Name="TopRow" Height="Auto" />
                </Grid.RowDefinitions>
                <Button x:Name="AfterTheProperty" />
            </Grid>
            """);

        Assert.Equal("RowDefinition", Symbol(result, "TopRow").TypeText);
        Assert.Equal("Button", Symbol(result, "AfterTheProperty").TypeText);
    }

    [Fact]
    public void ThePropertyElementErrorIsRewordedRatherThanBlamedOnTheAuthor()
    {
        var note = GrammarNotes.For(LanguageNames.Xaml);

        Assert.NotNull(note);
        Assert.Contains("HTML grammar", note);
        Assert.Contains("still indexed", note);
    }

    [Fact]
    public void ALanguageReadByItsOwnGrammarGetsNoSuchExcuse()
    {
        // If this ever returns a sentence, a real syntax error in a real C# file would be
        // explained away as somebody else's fault.
        Assert.Null(GrammarNotes.For(LanguageNames.CSharp));
        Assert.Null(GrammarNotes.For(LanguageNames.Html));
    }
}
