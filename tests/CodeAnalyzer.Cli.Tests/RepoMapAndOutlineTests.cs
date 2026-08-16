using CodeAnalyzer.Cli.Output;
using CodeAnalyzer.Cli.Querying;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

[Collection("cli-workspace")]
public class RepoMapAndOutlineTests(CliWorkspaceFixture fixture)
{
    private AgentToolset Toolset => new(fixture.Session);

    [Fact]
    public void TheMapRanksByDistinctCallers()
    {
        var map = Toolset.Map();

        // uart_write is called by main and extra (2 distinct callers); everything else
        // has at most one. The top entry is therefore not a matter of taste.
        Assert.NotEmpty(map.Entries);
        Assert.Equal("uart_write", map.Entries[0].Name);
        Assert.Equal(2, map.Entries[0].FanIn);
    }

    [Fact]
    public void TwoRunsPrintTheSameMap()
    {
        var first = TerseFormatter.RepoMap(Toolset.Map(), 4000);
        var second = TerseFormatter.RepoMap(Toolset.Map(), 4000);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ATightBudgetCutsWithAnExplicitMarker()
    {
        var map = Toolset.Map();
        var text = TerseFormatter.RepoMap(map, 150);

        Assert.Contains("truncated at 150 chars", text);
        Assert.Contains("raise --budget", text);

        // The marker states exactly how much was left out. An entry line is
        // "  12< name …" — the header's "(N< = N referencers)" must not count.
        var emittedLines = text.Split('\n')
            .Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s+\d+< "));
        Assert.Contains($"{map.Entries.Count - emittedLines} more ranked", text);
    }

    [Fact]
    public void TheOutlineIndentsMembersUnderTheirContainer()
    {
        var outline = Toolset.Outline("drivers/uart.c");

        Assert.NotNull(outline);
        Assert.Null(outline.CandidatePaths);

        var config = Assert.Single(outline.Entries, e => e.Name == "uart_config");
        var baud = Assert.Single(outline.Entries, e => e.Name == "baud");
        Assert.Equal(config.Depth + 1, baud.Depth);

        // Source order, not alphabetical.
        var names = outline.Entries.Select(e => e.Name).ToList();
        Assert.True(names.IndexOf("uart_init") < names.IndexOf("uart_write"));
    }

    [Fact]
    public void AnAmbiguousFileNameListsTheCandidatesInsteadOfPickingOne()
    {
        // Both drivers/uart.c and sim/uart.c end in uart.c.
        var outline = Toolset.Outline("uart.c");

        Assert.NotNull(outline);
        Assert.NotNull(outline.CandidatePaths);
        Assert.Equal(2, outline.CandidatePaths.Count);
        Assert.Contains("drivers/uart.c", outline.CandidatePaths);
        Assert.Contains("sim/uart.c", outline.CandidatePaths);
    }

    [Fact]
    public void AnUnknownFileIsNullNotAnEmptyOutline()
    {
        Assert.Null(Toolset.Outline("nowhere/nothing.c"));
    }
}
