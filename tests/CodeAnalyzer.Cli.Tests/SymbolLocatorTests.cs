using CodeAnalyzer.Cli.Querying;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

[Collection("cli-workspace")]
public class SymbolLocatorTests(CliWorkspaceFixture fixture)
{
    private AgentToolset Toolset => new(fixture.Session);

    [Fact]
    public void AUniqueNameResolvesDirectly()
    {
        var result = Toolset.Locate("uart_init");

        var resolved = Assert.IsType<LocateResult.Resolved>(result);
        Assert.Equal("uart_init", resolved.Symbol.Name);
        Assert.Equal("drivers/uart.c", resolved.Symbol.RelativePath);
    }

    [Fact]
    public void AnIdFromAPreviousResultResolves()
    {
        var byName = Assert.IsType<LocateResult.Resolved>(Toolset.Locate("uart_init"));

        var byId = Toolset.Locate($"#{byName.Symbol.Id}");

        var resolved = Assert.IsType<LocateResult.Resolved>(byId);
        Assert.Equal(byName.Symbol.Id, resolved.Symbol.Id);
    }

    [Fact]
    public void ANameDefinedTwiceIsAnsweredWithBothCandidates_NeverAGuess()
    {
        var result = Toolset.Locate("init");

        var ambiguous = Assert.IsType<LocateResult.Ambiguous>(result);
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.False(ambiguous.More);

        var paths = ambiguous.Candidates.Select(c => c.RelativePath).ToList();
        Assert.Contains("app/main.c", paths);
        Assert.Contains("drivers/init.c", paths);
    }

    [Fact]
    public void APathPrefixDisambiguates()
    {
        var result = Toolset.Locate("drivers/init.c:init");

        var resolved = Assert.IsType<LocateResult.Resolved>(result);
        Assert.Equal("drivers/init.c", resolved.Symbol.RelativePath);
    }

    [Fact]
    public void ThePathMayBeAnySuffixOfTheRelativePath()
    {
        var result = Toolset.Locate("init.c:init");

        var resolved = Assert.IsType<LocateResult.Resolved>(result);
        Assert.Equal("drivers/init.c", resolved.Symbol.RelativePath);
    }

    [Fact]
    public void AnUnknownNameGetsSuggestionsInsteadOfSilence()
    {
        var result = Toolset.Locate("uart_wrte");

        var notFound = Assert.IsType<LocateResult.NotFound>(result);
        Assert.Contains("uart_wrte", notFound.Message);
        Assert.Contains(notFound.Suggestions, s => s.Name == "uart_write");
    }

    [Fact]
    public void AStaleIdSaysWhatHappenedAndWhatToDo()
    {
        var result = Toolset.Locate("#999999");

        var notFound = Assert.IsType<LocateResult.NotFound>(result);
        Assert.Contains("no longer in the index", notFound.Message);
        Assert.Contains("re-run search", notFound.Message);
    }
}
