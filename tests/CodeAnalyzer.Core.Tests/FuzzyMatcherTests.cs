using CodeAnalyzer.Core.Search;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// What the matcher promises — an editor's "go to symbol", where initials and hump
/// prefixes find the thing — and the bar that separates a match from a coincidence.
/// Every pair here carries a rival, because a scorer tested against one candidate proves
/// only that it returned a number.
/// </summary>
public class FuzzyMatcherTests
{
    private static int ScoreOf(string query, string candidate)
    {
        var score = FuzzyMatcher.Score(query, candidate);
        Assert.NotNull(score);
        return score!.Value;
    }

    [Fact]
    public void InitialsFindTheirHumpsRatherThanTheLettersInBetween()
    {
        // The whole advertised trick, and the one the leftmost-first pass gets wrong: `S`
        // occurs inside "Work-s-pace" before it occurs at "Settings", and `T` inside
        // "Se-t-tings" before "Tests". Read that way the query is three scattered letters
        // and scores 9 against a floor of 30; read at the humps it scores 35.
        Assert.True(
            ScoreOf("WST", "WorkspaceSettingsTests") >= FuzzyMatcher.StrongScoreFloor("WST"),
            "initials naming three humps must clear the bar");

        Assert.True(
            ScoreOf("uwr", "uart_write") >= FuzzyMatcher.StrongScoreFloor("uwr"),
            "the example in the tool's own description must clear the bar");
    }

    [Fact]
    public void ScatteredLettersInALongNameStayBelowTheBar()
    {
        // The hit this round exists for: a real query from a real session, whose only
        // answer was a test method sharing nine letters in order with it and nothing else.
        const string Sprawl = "ABindingInsideATypedTemplateCarriesTheTemplatesTypeAsItsReceiver";

        Assert.NotNull(FuzzyMatcher.Score("McpServer", Sprawl));
        Assert.True(
            ScoreOf("McpServer", Sprawl) < FuzzyMatcher.StrongScoreFloor("McpServer"),
            "the letters appearing in order is not a match");
    }

    [Fact]
    public void TheBarScalesWithTheQueryBecauseTheScoreDoes()
    {
        // A twenty-character query earns five times the points of a four-character one for
        // the same quality of match, so a single absolute number would call every short
        // query weak and every long one strong.
        Assert.Equal(
            FuzzyMatcher.StrongScorePerCharacter * 4, FuzzyMatcher.StrongScoreFloor("Xaml"));
        Assert.Equal(
            FuzzyMatcher.StrongScorePerCharacter * 13, FuzzyMatcher.StrongScoreFloor("ReferenceKind"));

        Assert.True(ScoreOf("Xaml", "Xaml") >= FuzzyMatcher.StrongScoreFloor("Xaml"));
        Assert.True(
            ScoreOf("ReferenceKind", "ReferenceKind") >= FuzzyMatcher.StrongScoreFloor("ReferenceKind"));
    }

    [Fact]
    public void ARivalNamedByTheSameLettersStillRanksBelow()
    {
        // The docstring's own claim, kept honest: both candidates contain u, w and r in
        // order, and the one whose humps they are has to win.
        Assert.True(ScoreOf("uwr", "uart_write") > ScoreOf("uwr", "outer_wrapper"));

        // And the structured reading may not promote a candidate over a better one: a
        // whole-name match outranks the initials it also happens to satisfy.
        Assert.True(ScoreOf("RefRes", "RefRes") > ScoreOf("RefRes", "ReferenceResolver"));
    }

    [Fact]
    public void AQueryThatIsNotASubsequenceMatchesNothingAtAll()
    {
        // The structured pass is a restriction of the leftmost one and must not widen what
        // matches — only how well a match already found is read.
        Assert.Null(FuzzyMatcher.Score("zqx", "uart_write"));
        Assert.Null(FuzzyMatcher.Score("writeu", "uart_write"));
    }
}
