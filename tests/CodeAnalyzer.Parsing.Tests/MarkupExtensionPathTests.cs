using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The markup extension reader. Half of these pin refusals: a wrong guess here becomes a
/// wrong edge in the graph, so anything the parser is not certain of must return null
/// rather than its best attempt.
/// </summary>
public class MarkupExtensionPathTests
{
    [Theory]
    [InlineData("{Binding SearchQuery}", "SearchQuery")]
    [InlineData("{Binding SearchQuery, Mode=TwoWay}", "SearchQuery")]
    [InlineData("{Binding Path=SearchQuery}", "SearchQuery")]
    [InlineData("{Binding Path=SearchQuery, Mode=TwoWay}", "SearchQuery")]
    [InlineData("{Binding Mode=TwoWay, Path=SearchQuery}", "SearchQuery")]
    [InlineData("{ Binding SearchQuery }", "SearchQuery")]
    [InlineData("{Binding Selected.Name}", "Selected")]
    [InlineData("{Binding Items[0]}", "Items")]
    [InlineData("{TemplateBinding Width}", "Width")]
    [InlineData("{StaticResource PanelBrush}", "PanelBrush")]
    [InlineData("{DynamicResource AccentColor}", "AccentColor")]
    [InlineData("{Binding RelativeSource={RelativeSource Self}, Path=Tag}", "Tag")]
    public void ReadsTheOneNameTheExtensionRefersTo(string value, string expected)
    {
        var extracted = MarkupExtensionPath.Extract(value);

        Assert.NotNull(extracted);
        Assert.Equal(expected, extracted!.Value.Name);
    }

    [Theory]
    [InlineData("{StaticResource PanelBrush}", true)]
    [InlineData("{DynamicResource AccentColor}", true)]
    [InlineData("{Binding SearchQuery}", false)]
    [InlineData("{TemplateBinding Width}", false)]
    public void AKeyAndAPathAreToldApart(string value, bool isResource)
    {
        Assert.Equal(isResource, MarkupExtensionPath.Extract(value)!.Value.IsResource);
    }

    [Fact]
    public void TheOffsetPointsAtTheNameInsideTheValue()
    {
        var value = "{Binding Path=SearchQuery, Mode=TwoWay}";
        var extracted = MarkupExtensionPath.Extract(value)!.Value;

        Assert.Equal(extracted.Name, value.Substring(extracted.Offset, extracted.Name.Length));
    }

    [Theory]
    [InlineData("{Binding}")]                                   // no path at all
    [InlineData("{Binding Mode=TwoWay}")]                       // named args only
    [InlineData("{}{not an extension}")]                        // XAML's literal escape
    [InlineData("{x:Static local:AppInfo.Version}")]            // not a read extension
    [InlineData("{RelativeSource Self}")]                       // not a read extension
    [InlineData("{Binding (Validation.Errors)[0]}")]            // attached-property path
    [InlineData("{Binding RelativeSource={RelativeSource Self}}")] // nested, no path
    [InlineData("plain text")]                                  // not an extension
    [InlineData("{")]                                           // malformed
    public void RefusesWhatItCannotReadWithCertainty(string value)
    {
        Assert.Null(MarkupExtensionPath.Extract(value));
    }
}
