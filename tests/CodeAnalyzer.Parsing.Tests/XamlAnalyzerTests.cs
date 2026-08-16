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
    public void AResourceKeyIsADeclaration()
    {
        var result = Analyze("""
            <ResourceDictionary>
                <SolidColorBrush x:Key="PanelBrush" Color="#202020" />
            </ResourceDictionary>
            """);

        Assert.Equal(SymbolKind.MarkupElement, Symbol(result, "PanelBrush").Kind);
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

        Assert.Equal(SymbolKind.MarkupElement, Symbol(result, "RowButton").Kind);
    }

    [Fact]
    public void XClassIsRecordedVerbatimAndFullyQualified()
    {
        // Recorded, not resolved. It is kept fully qualified because that is what the file
        // says; shortening it here to make it match a definition would be the pack
        // inventing a link the source does not state. The refs pack header sets out why it
        // resolves to nothing today.
        var result = Analyze(Source);

        Assert.Contains("CodeAnalyzer.App.Views.MainWindow", ReferenceNames(result, ReferenceKind.TypeUse));
    }

    [Fact]
    public void NothingElseIsTurnedIntoAReference()
    {
        var result = Analyze(Source);

        // Title="CodeAnalyzer" names nothing; a markup extension is one opaque token to
        // this grammar and is not pretended to have been read.
        var referenced = result.References.Select(r => r.Name).ToList();
        Assert.DoesNotContain("SearchQuery", referenced);
        Assert.DoesNotContain("OnGoClicked", referenced);
        Assert.DoesNotContain("{Binding SearchQuery, Mode=TwoWay}", referenced);
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
