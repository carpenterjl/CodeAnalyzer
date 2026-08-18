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
    public void AStyleDeclaresItsKeyLikeAnyOtherKeyedResource()
    {
        // This test was called AStyleKeepsItsKeyEvenThoughItsBodyIsSwallowed, and its
        // comment read "<Style> is HTML's CSS <style>, so the grammar hands back its
        // children as one raw_text blob". That was true for nine rounds and stopped being
        // true in M29.1, when .xaml moved to a grammar with the tag table switched off —
        // ADeclarationInsideAStyleElementSurvives is where the body is now checked. The
        // assertion below was always about the key, which is on the tag either way; only
        // the name and the reason had gone stale.
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
    public void ABindingInsideATypedTemplateCarriesTheTemplatesTypeAsItsReceiver()
    {
        // The DataTemplate states its item type, and that statement is the fact that
        // makes {Binding Descriptor} resolvable: the type name lands in the receiver
        // slot, where the resolver's receiver-is-a-type-name rank prefers a property
        // whose container is SearchResultItem over one that merely shares the name.
        var result = Analyze("""
            <Window x:Class="CodeAnalyzer.App.Views.MainWindow">
                <Window.Resources>
                    <DataTemplate x:Key="SearchResultTemplate" DataType="{x:Type vm:SearchResultItem}">
                        <TextBlock x:Name="Row" Text="{Binding Descriptor}" />
                    </DataTemplate>
                </Window.Resources>
            </Window>
            """);

        var binding = Assert.Single(result.References, r => r.Kind == ReferenceKind.Binding);
        Assert.Equal("Descriptor", binding.Name);
        Assert.Equal("SearchResultItem", binding.ReceiverText);
    }

    [Fact]
    public void ARootDesignContextTypesEveryBindingOutsideATemplate()
    {
        // d:DataContext is the root element declaring what its own bindings resolve
        // against — in-file, parseable, no code-behind data flow required.
        var result = Analyze("""
            <Window x:Class="CodeAnalyzer.App.Views.MainWindow"
                    d:DataContext="{d:DesignInstance Type=vm:MainViewModel}">
                <TextBox x:Name="SearchBox" Text="{Binding SearchQuery}" />
            </Window>
            """);

        var binding = Assert.Single(result.References, r => r.Kind == ReferenceKind.Binding);
        Assert.Equal("MainViewModel", binding.ReceiverText);
    }

    [Fact]
    public void AnUndeclaredTemplateIsAWallNotAWindow()
    {
        // The template's real item type is whatever its ItemsSource holds — something
        // this index cannot know. Inheriting the window's context instead would be an
        // invented claim, so a typeless template blocks it: the binding inside carries
        // no receiver, while its sibling outside still carries the root's.
        var result = Analyze("""
            <Window x:Class="CodeAnalyzer.App.Views.MainWindow"
                    d:DataContext="{d:DesignInstance Type=vm:MainViewModel}">
                <TextBlock x:Name="Header" Text="{Binding Status}" />
                <ItemsControl x:Name="Rows">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <TextBlock x:Name="Row" Text="{Binding Language}" />
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </Window>
            """);

        var outside = Assert.Single(result.References, r => r.Name == "Status");
        Assert.Equal("MainViewModel", outside.ReceiverText);

        var inside = Assert.Single(result.References, r => r.Name == "Language");
        Assert.Null(inside.ReceiverText);
    }

    /// <summary>
    /// The same wall reasoning one level down. A binding that says where it reads from
    /// does not read the ambient DataContext, so the enclosing context is not its
    /// receiver — even though the binding sits squarely inside a typed template. The
    /// sibling on the same element is the rival that keeps this honest: it has no source
    /// of its own, so it must still carry the template's type.
    /// </summary>
    [Theory]
    [InlineData("{Binding Command, RelativeSource={RelativeSource AncestorType=Window}}")]
    [InlineData("{Binding Command, ElementName=Root}")]
    [InlineData("{Binding Command, Source={StaticResource Bag}}")]
    [InlineData("{Binding Path=Command, RelativeSource={RelativeSource Self}}")]
    public void ABindingThatNamesItsOwnSourceDoesNotTakeTheAmbientContext(string extension)
    {
        var result = Analyze($$"""
            <Window x:Class="CodeAnalyzer.App.Views.MainWindow"
                    d:DataContext="{d:DesignInstance Type=vm:MainViewModel}">
                <DataTemplate DataType="{x:Type vm:RowItem}">
                    <Button x:Name="Go" Command="{{extension}}" Content="{Binding Label}" />
                </DataTemplate>
            </Window>
            """);

        var sourced = Assert.Single(result.References, r => r.Name == "Command");
        Assert.Null(sourced.ReceiverText);

        var ambient = Assert.Single(result.References, r => r.Name == "Label");
        Assert.Equal("RowItem", ambient.ReceiverText);
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
    public void APropertyElementIsReadRatherThanReported()
    {
        // <Grid.RowDefinitions> is valid XAML and invalid HTML: a tag name took letters,
        // digits, '-' and ':' but not '.', so the dot ended the tag and raised an error.
        // The names around it always survived — what did not was the file's good name, and
        // for three rounds this was the only thing four of this repo's files were flagged
        // for. Measured before it was fixed: the same file with and without the property
        // element indexed the same declarations, and 303 of 661 XAML references already sat
        // inside one.
        var result = Analyze("""
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition x:Name="TopRow" Height="Auto" />
                </Grid.RowDefinitions>
                <Button x:Name="AfterTheProperty" />
            </Grid>
            """);

        Assert.Null(result.ErrorLine);
        Assert.Equal("RowDefinition", Symbol(result, "TopRow").TypeText);
        Assert.Equal("Button", Symbol(result, "AfterTheProperty").TypeText);
    }

    [Fact]
    public void TheXmlPrologueIsNotAnError()
    {
        // XAML is XML and may open with one. HTML has no rule for a processing
        // instruction, so this used to be an error on line 1 of any file carrying it.
        var result = Analyze("""
            <?xml version="1.0" encoding="utf-8"?>
            <ResourceDictionary>
                <Border x:Name="AfterTheXmlDeclaration" />
            </ResourceDictionary>
            """);

        Assert.Null(result.ErrorLine);
        Assert.Equal("Border", Symbol(result, "AfterTheXmlDeclaration").TypeText);
    }

    [Fact]
    public void ACDataSectionDoesNotTakeItsOwnElementsKeyWithIt()
    {
        // The one of the three that cost data rather than credibility: with no rule for
        // <![CDATA[…]]>, the element holding the section failed to parse and its x:Key went
        // with it — two declared, one indexed. Same shape as the <Style> swallow, which is
        // why it is asserted on the key of the element containing the section, not on the
        // one after it.
        var result = Analyze("""
            <ResourceDictionary>
                <sys:String x:Key="TheKeyOnTheElementHoldingIt"><![CDATA[a < b && c > d]]></sys:String>
                <Border x:Name="AndTheOneAfterIt" />
            </ResourceDictionary>
            """);

        Assert.Null(result.ErrorLine);
        Assert.Equal(SymbolKind.ResourceKey, Symbol(result, "TheKeyOnTheElementHoldingIt").Kind);
        Assert.Equal("Border", Symbol(result, "AndTheOneAfterIt").TypeText);
    }

    [Fact]
    public void NoLanguageGetsAnExcuseForItsParseErrors()
    {
        // If this ever returns a sentence, a real syntax error in a real file is explained
        // away as somebody else's fault. XAML held the only excuse there had ever been,
        // and lost it in round seventeen when the last of the three divergences was closed
        // in the grammar. Closing the gap is strictly better than annotating it: an excuse
        // that fires on every error is what let a genuine swallow read as a known
        // limitation for nine rounds.
        Assert.Null(GrammarNotes.For(LanguageNames.Xaml));
        Assert.Null(GrammarNotes.For(LanguageNames.CSharp));
        Assert.Null(GrammarNotes.For(LanguageNames.Html));
    }

    [Fact]
    public void ADeclarationInsideAStyleElementSurvives()
    {
        // The whole reason grammars/xaml exists. <style> is a raw-text element in HTML —
        // the parser stops reading markup at the start tag and resumes at the end tag,
        // because in HTML the content is CSS. WPF's <Style> holds markup, and for nine
        // rounds everything inside one was discarded: 32 declarations in this repo's own
        // Controls.xaml, with no error raised to notice it by.
        //
        // If the build ever falls back to the stock HTML grammar, this is what fails.
        var result = Analyze("""
            <ResourceDictionary>
                <Style x:Key="RowButton" TargetType="Button">
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="Button">
                                <Border x:Name="Bd" />
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style x:Key="DeclaredAfterTheFirstStyle" TargetType="Button" />
            </ResourceDictionary>
            """);

        Assert.Equal(SymbolKind.ResourceKey, Symbol(result, "RowButton").Kind);
        Assert.Equal("Border", Symbol(result, "Bd").TypeText);
        Assert.Equal(SymbolKind.ResourceKey, Symbol(result, "DeclaredAfterTheFirstStyle").Kind);
    }

    [Fact]
    public void ASelfClosingStyleDoesNotSwallowTheRestOfTheFile()
    {
        // The same defect's worst case, and the one that made it hard to see: a
        // self-closing <Style ... /> never reaches an end tag, so the raw-text scan ran to
        // end of file and every later declaration vanished — again with no error. A file
        // opening with one indexed zero names and reported nothing wrong.
        var result = Analyze("""
            <ResourceDictionary>
                <Style x:Key="OnlyBasedOn" BasedOn="{StaticResource Caption}" />
                <Style x:Key="AndTheOneAfterIt" TargetType="Button" />
                <Border x:Name="AndThisToo" />
            </ResourceDictionary>
            """);

        Assert.Equal(SymbolKind.ResourceKey, Symbol(result, "OnlyBasedOn").Kind);
        Assert.Equal(SymbolKind.ResourceKey, Symbol(result, "AndTheOneAfterIt").Kind);
        Assert.Equal(SymbolKind.MarkupElement, Symbol(result, "AndThisToo").Kind);
    }

    [Fact]
    public void AXamlTagThatCollidesWithAnHtmlOneIsStillJustATag()
    {
        // HTML's tag table is matched case-insensitively, so <Button>, <Label> and <Menu>
        // all hit rows in it — <Label> and <Menu> carry implicit-closing rules, and an
        // implicit close would end an element the markup did not end. Nothing in XAML
        // closes implicitly, which is why the variant forces every tag to CUSTOM rather
        // than only turning off raw text.
        var result = Analyze("""
            <ResourceDictionary>
                <Menu x:Name="Outer">
                    <Label x:Name="First" />
                    <Label x:Name="Second" />
                    <Button x:Name="Inner" />
                </Menu>
            </ResourceDictionary>
            """);

        Assert.Equal(["First", "Second", "Inner"], MembersOf(result, "Outer"));
    }
}
