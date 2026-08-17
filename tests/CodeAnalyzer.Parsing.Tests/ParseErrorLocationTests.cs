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
/// this project it was wrong to — every flagged C# file tripped on one construct the
/// bundled grammar was older than.
/// </para>
/// <para>
/// M28.3 removed that construct from the list by vendoring a newer grammar, which is why
/// every fixture here now uses a genuine syntax error instead. The cost of the old
/// behaviour is worth recording: it was not lost symbols — measured at zero, on this repo
/// and on the reported shape alike — but lost trust. A reader who correctly noticed 136
/// truncated files then reasoned from them to a cause that was not there.
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
    public void AnEmptyCollectionExpressionIsReadWithoutComplaint()
    {
        // This test used to assert the opposite, and its inversion is the whole point of
        // M28.3. `= []` is what flagged 57 of this project's 61 imperfect parses and 119 of
        // JGraph's 136; the grammar under grammars/csharp reads it. Kept as the regression
        // guard on the vendored grammar: if a build ever falls back to the NuGet package's
        // older copy, this is the test that says so.
        var result = Analyze("""
            class A
            {
                private readonly List<int> _x = [];
            }
            """);

        Assert.Equal(FileStatus.Ok, result.Status);
        Assert.Null(result.ErrorLine);
        Assert.Null(result.ErrorText);
        Assert.Contains(result.Symbols, s => s.Name == "_x");
    }

    [Fact]
    public void TheQuotedTextIsTheInnermostConstructWithAnExtent()
    {
        // The token the grammar wanted and did not get — here a `;` — is zero-width.
        // Quoting it would print nothing, so what is quoted is the nearest enclosing
        // construct that has any text, which is what a reader recognises.
        var result = Analyze("""
            class A
            {
                void M() { int x = 1 }
            }
            """);

        Assert.NotNull(result.ErrorText);
        Assert.DoesNotContain("class A", result.ErrorText);
        Assert.Contains("int x = 1", result.ErrorText);
    }

    [Fact]
    public void TheFirstErrorInSourceOrderIsTheOneReported()
    {
        var result = Analyze("""
            class A
            {
                public int Y() => 1;
                void First() { int a = 1 }
                void Second() { int b = 2 }
            }
            """);

        Assert.Equal(4, result.ErrorLine);
    }

    [Fact]
    public void AQuoteIsCappedSoOneErrorCannotPrintAFile()
    {
        var wide = new string('x', 400);
        var result = Analyze($"class A {{ void M() {{ int x = \"{wide}\" }} }}");

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
