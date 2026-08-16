using CodeAnalyzer.Cli.Mcp;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Export;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// The markdown fact report over the headless session: the toolset assembles the same
/// aggregate the GUI's "copy facts" builds, and the MCP tool returns the same document
/// the CLI prints.
/// </summary>
[Collection("cli-workspace")]
public class ReportCommandTests(CliWorkspaceFixture fixture)
{
    private AgentToolset Toolset => new(fixture.Session);

    [Fact]
    public void TheReportCarriesDetailSourceAndProvenance()
    {
        var located = Assert.IsType<LocateResult.Resolved>(Toolset.Locate("uart_init"));
        var report = Toolset.Report(located.Symbol.Id);

        Assert.NotNull(report);
        Assert.Equal("uart_init", report!.Detail.Name);
        Assert.StartsWith("index built ", report.Provenance);
        Assert.NotNull(report.SourceExcerpt);

        var markdown = MarkdownFactWriter.Write(report);
        Assert.Contains("# `uart_init`", markdown);
        Assert.Contains("## Source", markdown);
    }

    [Fact]
    public void AStaleIdIsNullNotAnEmptyDocument()
    {
        Assert.Null(Toolset.Report(999_999));
    }

    [Fact]
    public void TheMcpToolAnswersWithTheMarkdownDocument()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        var text = tools.GetContext("uart_init");

        Assert.Contains("# `uart_init`", text);
        // The vintage line every MCP answer opens with still rides on top.
        Assert.StartsWith("[index:", text);
    }

    [Fact]
    public void TheMcpToolAnswersAmbiguityWithCandidatesNotAGuess()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        // "init" is defined in two files, so the honest answer is the candidate list.
        var text = tools.GetContext("init");

        Assert.Contains("definitions", text);
        Assert.Contains("#", text);
        Assert.DoesNotContain("## Source", text);
    }
}
