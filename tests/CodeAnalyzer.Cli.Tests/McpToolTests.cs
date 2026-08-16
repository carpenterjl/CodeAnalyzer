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

        Assert.StartsWith("[index:", text);
        Assert.Contains("call reindex to refresh", text);
        Assert.Contains("uart_init", text);
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
