using CodeAnalyzer.Cli.Mcp;
using CodeAnalyzer.Cli.Output;
using CodeAnalyzer.Cli.Querying;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// The headless side of value tracing. The fixture writes one command byte as <c>0xA5</c>
/// in C and again in C#, with nothing connecting them — so a hit here proves the query is
/// finding an agreement rather than following a reference.
/// </summary>
[Collection("cli-workspace")]
public class ValueCommandTests(CliWorkspaceFixture fixture)
{
    private AgentToolset Toolset => new(fixture.Session);

    [Fact]
    public void OneNotationFindsTheOther()
    {
        var found = Toolset.FindByValue("0xA5", 20);

        Assert.NotNull(found);
        Assert.Equal("165", found.Canonical);
        Assert.Contains(found.Matches, m => m.Name == "CMD_READ" && m.Verbatim == "0xA5");
        Assert.Contains(found.Matches, m => m.Name == "CmdRead");
    }

    [Fact]
    public void TheSameValueIsFoundByItsDecimalForm()
    {
        // A reader who knows only the number the receiver compares against still finds
        // the sender's hex constant.
        var found = Toolset.FindByValue("165", 20);

        Assert.NotNull(found);
        Assert.Equal(2, found.Matches.Count);
    }

    [Fact]
    public void SomethingThatIsNotALiteralIsRefusedRatherThanGuessedAt()
    {
        // Falling back to a name search would answer a question the caller did not ask.
        Assert.Null(Toolset.FindByValue("CMD_READ", 20));
    }

    [Fact]
    public void ARefusalNamesTheNotationsItAccepts()
    {
        var text = TerseFormatter.Values("CMD_READ", null);

        Assert.Contains("not a literal value", text);
        Assert.Contains("0xA5", text);
        Assert.Contains("8'hA5", text);
    }

    [Fact]
    public void EveryRowStatesItsOwnNotationAndWhatItDenotes()
    {
        var text = TerseFormatter.Values("0xA5", Toolset.FindByValue("0xA5", 20));

        Assert.StartsWith("165 —", text);
        Assert.Contains("CMD_READ = 0xA5", text);
        Assert.Contains("[C#]", text);
    }

    [Fact]
    public void ATightLimitSaysThatItCut()
    {
        var text = TerseFormatter.Values("0xA5", Toolset.FindByValue("0xA5", 1));

        Assert.Contains("list capped at 1", text);
        Assert.Contains("more definitions carry this value", text);
    }

    [Fact]
    public void ASymbolsFactSheetCarriesItsCrossLanguageTwin()
    {
        var located = Assert.IsType<LocateResult.Resolved>(Toolset.Locate("CMD_READ"));
        var detail = Toolset.GetDetail(located.Symbol.Id);

        var text = TerseFormatter.Detail(detail!, Toolset.SameValue(located.Symbol.Id));

        Assert.Contains("same value elsewhere (1, 165)", text);
        Assert.Contains("CmdRead", text);
    }

    [Fact]
    public void ASymbolWithNoCertifiableLiteralHasNoSuchSection()
    {
        var located = Assert.IsType<LocateResult.Resolved>(Toolset.Locate("uart_write"));
        var detail = Toolset.GetDetail(located.Symbol.Id);

        var text = TerseFormatter.Detail(detail!, Toolset.SameValue(located.Symbol.Id));

        Assert.DoesNotContain("same value elsewhere", text);
    }

    [Fact]
    public void TheMcpToolExplainsWhatSharingAValueDoesAndDoesNotProve()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        var text = tools.FindByValue("0xA5");

        Assert.StartsWith("[index:", text);
        Assert.Contains("CMD_READ", text);
        Assert.Contains("CmdRead", text);
    }
}
