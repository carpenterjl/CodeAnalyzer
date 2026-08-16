using CodeAnalyzer.Core.Export;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The report builder against a real indexed workspace: detail, per-callee sites, the
/// source excerpt with its cap, and the markdown render end to end.
/// </summary>
public class SymbolContextReportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-report", Guid.NewGuid().ToString("N"));
    private WorkspaceSession? _session;

    public SymbolContextReportTests()
    {
        Directory.CreateDirectory(_root);

        WriteFile("drivers/uart.c", """
            #define UART_BAUD 115200

            static int uart_configure(int baud) {
                return baud == UART_BAUD;
            }

            int uart_init(void) {
                return uart_configure(UART_BAUD);
            }
            """);

        // A body long enough to trip the 120-line excerpt cap.
        var longBody = string.Join('\n', Enumerable.Range(0, 150).Select(i => $"    x += {i};"));
        WriteFile("drivers/long.c", $$"""
            int long_running(void) {
                int x = 0;
            {{longBody}}
                return x;
            }
            """);
    }

    public void Dispose()
    {
        _session?.Dispose();
        WorkspaceCacheCleanup.Delete(_root);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<WorkspaceSession> OpenIndexedAsync()
    {
        _session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
        await _session.IndexAsync([string.Empty], progress: null);
        return _session;
    }

    [Fact]
    public async Task GathersDetailCalleeSitesValueMatchesAndSource()
    {
        var session = await OpenIndexedAsync();
        var hit = Assert.Single(session.Search.Search("uart_init"), h => h.Name == "uart_init");

        var report = SymbolContextReportBuilder.Build(
            session.Graph, session.Values, [], _root, hit.SymbolId,
            provenance: "test index");

        Assert.NotNull(report);
        Assert.Equal("uart_init", report!.Detail.Name);
        Assert.Equal(session.Graph.RelatedLimit, report.RelatedLimit);

        // The call to uart_configure carries its verbatim argument site.
        var configure = Assert.Single(
            report.CalleeSites, s => s.Callee.Name == "uart_configure");
        Assert.Contains(configure.Sites, s => s.ArgumentText == "(UART_BAUD)");

        Assert.NotNull(report.SourceExcerpt);
        Assert.Contains("uart_configure(UART_BAUD)", report.SourceExcerpt);
        Assert.False(report.SourceTruncated);

        var markdown = MarkdownFactWriter.Write(report);
        Assert.Contains("# `uart_init`", markdown);
        Assert.Contains("test index", markdown);
        Assert.Contains("```c", markdown);
        Assert.Contains("- `uart_configure` — call", markdown);
    }

    [Fact]
    public async Task ASymbolLongerThanTheCapGetsATruncatedExcerptThatSaysSo()
    {
        var session = await OpenIndexedAsync();
        var hit = Assert.Single(session.Search.Search("long_running"));

        var report = SymbolContextReportBuilder.Build(
            session.Graph, session.Values, [], _root, hit.SymbolId);

        Assert.NotNull(report);
        Assert.True(report!.SourceTruncated);
        Assert.Equal(
            SymbolContextReportBuilder.MaxSourceLines,
            report.SourceExcerpt!.Split('\n').Length);
        Assert.Contains("— truncated", MarkdownFactWriter.Write(report));
    }

    [Fact]
    public async Task AStaleIdReturnsNullRatherThanAnEmptyReport()
    {
        var session = await OpenIndexedAsync();

        var report = SymbolContextReportBuilder.Build(
            session.Graph, session.Values, [], _root, symbolId: 999_999);

        Assert.Null(report);
    }
}
