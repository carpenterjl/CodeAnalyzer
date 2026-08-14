using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

public class SymbolFactsTests
{
    [Fact]
    public void ACallableStatesItsModifiersAndKind()
    {
        Assert.Equal(
            "public method",
            SymbolFacts.Describe(SymbolKind.Method, "public", "int", hasParameterList: true));
    }

    [Fact]
    public void ACallableDoesNotRepeatItsReturnType()
    {
        // The parameters are already on the node, carrying their own types; a return type
        // in the descriptor would be the same information twice in less space.
        var described = SymbolFacts.Describe(
            SymbolKind.Function, null, "static void", hasParameterList: true);

        Assert.Equal("function", described);
    }

    [Fact]
    public void ADeclarationWithNoParametersStatesItsType()
    {
        Assert.Equal(
            "private string field",
            SymbolFacts.Describe(SymbolKind.Field, "private", "string", hasParameterList: false));
    }

    [Fact]
    public void NothingIsInventedForADeclarationThatStatesNothing()
    {
        // An unmodified C# class is internal by the language's rule. The source did not
        // say so, so the descriptor does not either.
        Assert.Equal(
            "class",
            SymbolFacts.Describe(SymbolKind.Class, null, null, hasParameterList: false));

        Assert.Equal(
            "namespace",
            SymbolFacts.Describe(SymbolKind.Namespace, "", "  ", hasParameterList: false));
    }

    [Fact]
    public void AnOverloadSaysWhichOneItIs()
    {
        Assert.Equal(
            "public method · overload 2 of 3",
            SymbolFacts.Describe(
                SymbolKind.Method, "public", null, hasParameterList: true,
                overloadCount: 3, overloadOrdinal: 2));
    }

    [Fact]
    public void ANameThatIsNotOverloadedSaysNothingAboutOverloading()
    {
        Assert.Equal(
            "function",
            SymbolFacts.Describe(
                SymbolKind.Function, null, null, hasParameterList: true,
                overloadCount: 1, overloadOrdinal: 1));
    }

    [Fact]
    public void AMultiLineTypeIsFlattenedToOneLine()
    {
        // The descriptor is drawn as one row of a graph node label, where a newline
        // carried out of the source would silently become an extra row.
        var described = SymbolFacts.Describe(
            SymbolKind.Field,
            "private",
            "Dictionary<string,\n    int>",
            hasParameterList: false);

        Assert.Equal("private Dictionary<string, int> field", described);
    }

    [Fact]
    public void AVeryLongTypeIsCutWithAVisibleEllipsis()
    {
        var described = SymbolFacts.Describe(
            SymbolKind.Variable, null, new string('T', 60), hasParameterList: false);

        Assert.Equal(new string('T', 40) + "… variable", described);
    }
}
