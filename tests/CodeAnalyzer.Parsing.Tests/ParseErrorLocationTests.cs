using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Parsing;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Where an imperfect parse says it went wrong.
/// <para>
/// The counted-but-never-examined number these tests came from: five rounds of reporting
/// said "57 files with syntax errors" over a workspace that compiles with no warnings at
/// all. A count with no position invites the reader to distrust their own source, and on
/// this project it was wrong to — every flagged C# file trips on one construct the bundled
/// grammar is older than.
/// </para>
/// </summary>
public class ParseErrorLocationTests : IDisposable
{
    private readonly TreeSitterAnalyzer _csharp =
        new(LanguageRegistry.ForName(LanguageRegistry.CSharp)!);

    public void Dispose() => _csharp.Dispose();

    private ParseResult Analyze(string source) =>
        _csharp.Analyze("test.cs", source, CancellationToken.None);

    [Fact]
    public void ACleanFileClaimsNoErrorPosition()
    {
        var result = Analyze("class A { public int Y() => 1; }");

        Assert.Equal(FileStatus.Ok, result.Status);
        Assert.Null(result.ErrorLine);
        Assert.Null(result.ErrorText);
    }

    [Fact]
    public void AnEmptyCollectionExpressionIsLocatedAndQuoted()
    {
        // The construct behind every C# file this project flags. The grammar predates it,
        // recovers, and indexes the file anyway — so the position is the only thing that
        // can tell the reader the file is fine and the grammar is old.
        var result = Analyze("""
            class A
            {
                private readonly List<int> _x = [];
            }
            """);

        Assert.Equal(FileStatus.ParseError, result.Status);
        Assert.Equal(3, result.ErrorLine);
        Assert.Equal("[]", result.ErrorText);

        // Recovered, not failed: no message, and the declaration survives.
        Assert.Null(result.ErrorMessage);
        Assert.Contains(result.Symbols, s => s.Name == "_x");
    }

    [Fact]
    public void TheQuotedTextIsTheInnermostConstructWithAnExtent()
    {
        // The failing node itself is zero-width — a token the grammar wanted and did not
        // get. Quoting it would print nothing, so what is quoted is the nearest enclosing
        // construct that has any text, which is what a reader recognises.
        var result = Analyze("class A { void M() { Use([]); } }");

        Assert.NotNull(result.ErrorText);
        Assert.DoesNotContain("class A", result.ErrorText);
        Assert.Contains("[]", result.ErrorText);
    }

    [Fact]
    public void TheFirstErrorInSourceOrderIsTheOneReported()
    {
        var result = Analyze("""
            class A
            {
                public int Y() => 1;
                private readonly List<int> _first = [];
                private readonly List<int> _second = [];
            }
            """);

        Assert.Equal(4, result.ErrorLine);
    }

    [Fact]
    public void AQuoteIsCappedSoOneErrorCannotPrintAFile()
    {
        var wide = new string('x', 400);
        var result = Analyze($"class A {{ void M() {{ Use([], \"{wide}\"); }} }}");

        Assert.NotNull(result.ErrorLine);
        Assert.True(
            result.ErrorText!.Length <= 49,
            $"quote was {result.ErrorText.Length} characters: {result.ErrorText}");
    }

    [Fact]
    public void ARequiredMemberIsReadWithoutComplaint()
    {
        // Guards a wrong conclusion that was available on the way here: `required` shows up
        // in far more flagged files than clean ones, which looks like a second unsupported
        // construct until you notice it is only ever keeping company with a collection
        // expression in the same file.
        var result = Analyze("""
            class A
            {
                public required string Name { get; init; }
            }
            """);

        Assert.Equal(FileStatus.Ok, result.Status);
        Assert.Null(result.ErrorLine);
        Assert.Contains(result.Symbols, s => s.Name == "Name");
    }
}
