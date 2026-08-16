using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Exercises the exact sequence the UI performs: open a workspace, index a selection,
/// search, then load a symbol's details and source. Guards the wiring the shell depends on.
/// </summary>
public class WorkspaceSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeanalyzer-session", Guid.NewGuid().ToString("N"));
    private WorkspaceSession? _session;

    public WorkspaceSessionTests()
    {
        Directory.CreateDirectory(_root);

        WriteFile("drivers/uart.c", """
            #include "uart.h"

            #define UART_BAUD 115200

            static int uart_configure(int baud) {
                return baud == UART_BAUD;
            }

            int uart_init(void) {
                return uart_configure(UART_BAUD);
            }
            """);

        WriteFile("drivers/uart.h", "int uart_init(void);");

        // Deliberately not named "vendor": that is in the default ignore list, which would
        // make the selection test pass for the wrong reason.
        WriteFile("app/main.c", "int app_entry(void) { return 0; }");
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
            // Temp cleanup failures are not test failures.
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private WorkspaceSession OpenSession()
    {
        _session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
        return _session;
    }

    /// <summary>
    /// The gate is right about the only thing it can see — the file — and wrong whenever
    /// the analyzer changed underneath it, which is what a query-pack edit does. These two
    /// facts are the whole of <c>--full</c>: by default an untouched file is skipped, and
    /// with the flag it is read again.
    /// </summary>
    [Fact]
    public async Task AFullIndexReparsesFilesTheGateWouldHaveSkipped()
    {
        var session = OpenSession();

        var first = await session.IndexAsync([string.Empty]);
        Assert.True(first.Outcome.FilesParsed >= 3);

        var incremental = await session.IndexAsync([string.Empty]);
        Assert.Equal(0, incremental.Outcome.FilesParsed);
        Assert.Equal(first.Outcome.FilesParsed, incremental.Outcome.FilesUnchanged);

        var full = await session.IndexAsync([string.Empty], full: true);
        Assert.Equal(first.Outcome.FilesParsed, full.Outcome.FilesParsed);
        Assert.Equal(0, full.Outcome.FilesUnchanged);

        // Re-reading the same bytes must produce the same index, not a doubled one.
        Assert.Equal(first.Outcome.SymbolsFound, full.Outcome.SymbolsFound);
        Assert.Equal(first.EdgesCreated, full.EdgesCreated);
    }

    [Fact]
    public async Task IndexThenSearchThenDetail_IsTheFlowTheShellUses()
    {
        var session = OpenSession();

        var reports = new List<IndexProgress>();
        var result = await session.IndexAsync([string.Empty], new Progress<IndexProgress>(reports.Add));

        Assert.False(result.Outcome.WasCancelled);
        Assert.True(
            result.Outcome.FilesParsed >= 3,
            $"parsed={result.Outcome.FilesParsed} discovered={result.Outcome.FilesDiscovered} " +
            $"unchanged={result.Outcome.FilesUnchanged} failed={result.Outcome.FilesFailed} " +
            $"symbols={result.Outcome.SymbolsFound}");
        Assert.True(result.EdgesCreated > 0);

        // Search finds the symbol by a fuzzy abbreviation, as the search box would.
        var hit = Assert.Single(session.Search.Search("uinit"), h => h.Name == "uart_init");

        var detail = session.Graph.GetDetail(hit.SymbolId);
        Assert.NotNull(detail);
        Assert.Equal("drivers/uart.c", detail!.RelativePath);

        // The callee link and the macro constant are both facts the detail pane shows.
        Assert.Contains(detail.Callees, c => c.Name == "uart_configure");
        Assert.Contains(detail.Callees, c => c.Name == "UART_BAUD");

        // Source loads for the preview pane.
        var source = session.TryReadSource(detail.RelativePath);
        Assert.NotNull(source);
        Assert.Contains("uart_init", source);
    }

    [Fact]
    public async Task IndexingOnlySelectedDirectoriesExcludesTheRest()
    {
        var session = OpenSession();
        await session.IndexAsync(["drivers"]);

        Assert.NotEmpty(session.Search.Search("uart_init"));
        Assert.DoesNotContain(session.Search.Search("app_entry"), h => h.Name == "app_entry");
    }

    [Fact]
    public async Task NarrowingTheSelectionDropsWhatWasUnchecked()
    {
        var session = OpenSession();
        await session.IndexAsync([string.Empty]);
        Assert.Contains(session.Search.Search("app_entry"), h => h.Name == "app_entry");

        // The user unticks "app" and re-indexes. Leaving its symbols behind would keep
        // answering searches and drawing callers for source they said not to look at.
        var result = await session.IndexAsync(["drivers"]);

        Assert.Equal(1, result.FilesRemoved);
        Assert.DoesNotContain(session.Search.Search("app_entry"), h => h.Name == "app_entry");
        Assert.NotEmpty(session.Search.Search("uart_init"));
    }

    [Fact]
    public async Task RecheckingADirectoryBringsItBack()
    {
        var session = OpenSession();
        await session.IndexAsync(["drivers"]);
        Assert.DoesNotContain(session.Search.Search("app_entry"), h => h.Name == "app_entry");

        await session.IndexAsync([string.Empty]);

        Assert.Contains(session.Search.Search("app_entry"), h => h.Name == "app_entry");
    }

    [Fact]
    public async Task ACancelledRunKeepsWhatIsAlreadyIndexed()
    {
        // The pruning pass reads "not crawled" as "not wanted", which is only true of a
        // complete run. A cancelled crawl must not be mistaken for a mass deletion.
        var session = OpenSession();
        await session.IndexAsync([string.Empty]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = await session.IndexAsync([string.Empty], cancellationToken: cts.Token);

        Assert.True(cancelled.Outcome.WasCancelled);
        Assert.Equal(0, cancelled.FilesRemoved);
        Assert.Contains(session.Search.Search("app_entry"), h => h.Name == "app_entry");
        Assert.NotEmpty(session.Search.Search("uart_init"));
    }

    [Fact]
    public async Task SelectionIsRestoredWhenTheWorkspaceIsReopened()
    {
        var session = OpenSession();
        await session.IndexAsync(["drivers"]);
        session.Dispose();

        _session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());

        Assert.Equal(new[] { "drivers" }, _session.LoadSelectedDirectories());

        // The cached index is searchable immediately, without re-indexing.
        Assert.NotEmpty(_session.Search.Search("uart_init"));
    }

    [Fact]
    public async Task ReindexingAfterAnEditPicksUpTheChange()
    {
        var session = OpenSession();
        await session.IndexAsync([string.Empty]);

        Assert.DoesNotContain(session.Search.Search("uart_reset"), h => h.Name == "uart_reset");

        WriteFile("drivers/uart.c", """
            int uart_reset(void) {
                return 0;
            }
            """);

        var result = await session.IndexAsync([string.Empty]);

        // Only the edited file needs parsing; the rest are skipped by content stamp.
        Assert.True(result.Outcome.FilesUnchanged > 0);
        Assert.Single(session.Search.Search("uart_reset"), h => h.Name == "uart_reset");
        Assert.DoesNotContain(session.Search.Search("uart_configure"), h => h.Name == "uart_configure");
    }

    [Fact]
    public async Task CancellationLeavesTheSessionUsable()
    {
        for (var i = 0; i < 300; i++)
        {
            WriteFile($"bulk/file{i}.c", $"int bulk_{i}(void) {{ return {i}; }}");
        }

        var session = OpenSession();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        var result = await session.IndexAsync([string.Empty], cancellationToken: cts.Token);

        // Whether or not the run got far enough to cancel, the session must still work.
        var followUp = await session.IndexAsync([string.Empty]);
        Assert.False(followUp.Outcome.WasCancelled);
        Assert.NotEmpty(session.Search.Search("uart_init"));
    }
}
