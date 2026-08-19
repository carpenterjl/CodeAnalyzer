using CodeAnalyzer.Cli.Mcp;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// The MCP tools are ordinary methods; the transport is the SDK's business. These call
/// them directly, which is what makes the failure sentences and provenance header
/// assertable without a process.
/// </summary>
[Collection("cli-workspace")]
public class McpToolTests(CliWorkspaceFixture fixture)
{
    [Fact]
    public void EveryAnswerCarriesTheIndexVintage()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        var text = tools.SearchSymbols("uart");

        // The vintage is what every answer must carry — which index, how big, how old. The
        // fixture is freshly indexed, so it carries no advice to rebuild it: since M28.1 the
        // "call reindex" line is attached to drift, and drift is what the agent can act on.
        Assert.StartsWith("[index:", text);
        Assert.Contains(" definitions", text);
        Assert.Contains(", built ", text);
        Assert.Contains("indexed files unchanged on disk", text);
        Assert.DoesNotContain("to refresh", text);
        Assert.Contains("uart_init", text);

        // This fixture holds a deliberately broken file, so the vintage carries M28.4's
        // clause too — the count every answer below it is drawn from.
        Assert.Contains("imperfect parses (see: errors)", text);
    }

    [Fact]
    public void CallersComeBackWithFileAndLine()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        var text = tools.GetCallers("uart_write", include_sites: true);

        Assert.Contains("main", text);
        Assert.Contains("extra", text);
        Assert.Contains("app/main.c", text);
        // --sites adds the verbatim arguments of each call.
        Assert.Contains("(\"hi\")", text);
    }

    [Fact]
    public void TheCallFlowAnswersInSourceOrderWithItsHonestyFooter()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        var text = tools.GetCallFlow("main");

        Assert.Contains("flow of ", text);
        // main's body calls in written order; the flow must keep it.
        var uartInit = text.IndexOf("uart_init", StringComparison.Ordinal);
        var uartWrite = text.IndexOf("uart_write", StringComparison.Ordinal);
        Assert.True(uartInit >= 0 && uartWrite > uartInit, "source order was not kept");
        Assert.Contains("call sites in source order", text);
    }

    [Fact]
    public void TheCallFlowAnswersMermaidWhenAsked()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        var text = tools.GetCallFlow("main", mermaid: true);

        Assert.Contains("flowchart TD", text);
        Assert.Contains("root([", text);
        Assert.Contains("uart_init", text);
    }

    [Fact]
    public void AnUnknownFlowRootAnswersTheLocateSentence()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        var text = tools.GetCallFlow("no_such_function_anywhere");

        Assert.Contains("no definition named", text);
    }

    [Fact]
    public void AnAmbiguousNameAnswersWithTheCandidateList()
    {
        using var holder = new McpSessionHolder(fixture.Root);
        var tools = new CodeAnalyzerTools(holder);

        var text = tools.GetSymbol("init");

        Assert.Contains("2 definitions", text);
        Assert.Contains("app/main.c", text);
        Assert.Contains("drivers/init.c", text);
    }

    [Fact]
    public void AMissingIndexNamesReindexAsTheWayOut()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "codeanalyzer-mcp-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(emptyRoot);

        try
        {
            using var holder = new McpSessionHolder(emptyRoot);
            var tools = new CodeAnalyzerTools(holder);

            var text = tools.SearchSymbols("anything");

            Assert.Contains("no index", text);
            Assert.Contains("reindex", text);
        }
        finally
        {
            Directory.Delete(emptyRoot, recursive: true);
        }
    }
}
