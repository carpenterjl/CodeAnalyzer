using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

public class ValueFactsTests
{
    [Fact]
    public void ANoteStatesBothFormsWhenTheyAreWrittenDifferently()
    {
        Assert.Equal(
            "0xA5 = 165 — numerically equal",
            ValueFacts.EqualityNote("0xA5", 165, null));
    }

    [Fact]
    public void ANoteDoesNotRepeatALiteralThatIsAlreadyThePlainForm()
    {
        Assert.Equal("165 — numerically equal", ValueFacts.EqualityNote("165", 165, null));
    }

    [Fact]
    public void AStringNoteClaimsTextRatherThanMeaning()
    {
        Assert.Equal(
            "@\"COM3\" = \"COM3\" — identical text",
            ValueFacts.EqualityNote("@\"COM3\"", null, "COM3"));
    }

    [Fact]
    public void ADescriptorLeadsWithTheDeclarationsOwnWords()
    {
        Assert.Equal("value 0xA5 (= 165)", ValueFacts.Descriptor("0xA5", 165, null));
        Assert.Equal("value 165", ValueFacts.Descriptor("165", 165, null));
    }

    [Fact]
    public void AVerbatimSpanningLinesIsFlattenedForARow()
    {
        // These strings are drawn on one-line rows; a newline out of the source would
        // silently become an extra row.
        Assert.Equal("1 + 2 = 3 — numerically equal", ValueFacts.EqualityNote("1 +\n  2", 3, null));
    }
}
