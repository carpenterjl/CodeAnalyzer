using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

public class ModifierFactsTests
{
    [Theory]
    [InlineData("public sealed", "public")]
    [InlineData("internal const", "internal")]
    [InlineData("private static readonly", "private")]
    [InlineData("protected internal virtual", "protected internal")]
    [InlineData("private protected", "private protected")]
    [InlineData("static readonly", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void VisibilityTokenIsTheVerbatimKeywordOrNothing(string? modifiers, string? expected)
    {
        // No default is invented: an unmodified C# class is internal by the language's
        // rule, but the source does not say so, so neither does the index.
        Assert.Equal(expected, ModifierFacts.VisibilityToken(modifiers));
    }

    [Theory]
    [InlineData("public override", "override", true)]
    [InlineData("public override", "public", true)]
    [InlineData("override", "override", true)]
    [InlineData("public overrides", "override", false)]
    [InlineData("public", "override", false)]
    [InlineData(null, "override", false)]
    public void HasTokenMatchesWholeTokensOnly(string? modifiers, string token, bool expected)
    {
        Assert.Equal(expected, ModifierFacts.HasToken(modifiers, token));
    }

    [Fact]
    public void InternalDoesNotMatchInsideProtectedInternal()
    {
        // "protected internal" is one visibility, not two: the compound token must win,
        // and the parts must still be findable as tokens because they are ones.
        Assert.Equal("protected internal", ModifierFacts.VisibilityToken("protected internal"));
        Assert.True(ModifierFacts.HasToken("protected internal", "internal"));
        Assert.True(ModifierFacts.HasToken("protected internal", "protected internal"));
    }
}
