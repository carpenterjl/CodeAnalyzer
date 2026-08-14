using System.Collections.Concurrent;
using CodeAnalyzer.Core.Analysis;
using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Indexing;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Exercises the crawl → parse → write pipeline against a real temporary workspace,
/// with the real tree-sitter analyzers behind it.
/// </summary>
public class IndexPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeanalyzer-tests", Guid.NewGuid().ToString("N"));

    public IndexPipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static (IndexOrchestrator Orchestrator, CollectingSink Sink) CreatePipeline()
    {
        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension);
        return (new IndexOrchestrator(crawler, factory), new CollectingSink());
    }

    [Fact]
    public async Task IndexesAllSourceFilesAndSkipsIgnoredDirectories()
    {
        WriteFile("src/main.c", "int main(void) { return helper(); }");
        WriteFile("src/helper.c", "int helper(void) { return 42; }");
        WriteFile("src/notes.txt", "not source");
        WriteFile("obj/generated.c", "int generated(void) { return 0; }");
        WriteFile(".git/config.c", "int vcs(void) { return 0; }");

        var (orchestrator, sink) = CreatePipeline();

        var outcome = await orchestrator.IndexAsync(
            WorkspaceSelection.EntireWorkspace(_root),
            sink);

        Assert.False(outcome.WasCancelled);
        Assert.Equal(2, outcome.FilesParsed);

        var paths = sink.Results.Select(r => r.RelativePath).OrderBy(p => p).ToList();
        Assert.Equal(new[] { "src/helper.c", "src/main.c" }, paths);

        // Build output and version control directories must never reach a parser.
        Assert.DoesNotContain(sink.Results, r => r.RelativePath.Contains("obj/"));
        Assert.DoesNotContain(sink.Results, r => r.RelativePath.Contains(".git/"));
    }

    [Fact]
    public async Task IndexesOnlySelectedSubdirectories()
    {
        WriteFile("drivers/uart.c", "int uart_init(void) { return 0; }");
        WriteFile("vendor/third_party.c", "int vendor_fn(void) { return 0; }");

        var (orchestrator, sink) = CreatePipeline();

        await orchestrator.IndexAsync(
            new WorkspaceSelection(_root, ["drivers"]),
            sink);

        var result = Assert.Single(sink.Results);
        Assert.Equal("drivers/uart.c", result.RelativePath);
    }

    [Fact]
    public async Task NestedSelectionsDoNotProduceDuplicates()
    {
        WriteFile("src/core/a.c", "int a(void) { return 0; }");

        var (orchestrator, sink) = CreatePipeline();

        // "src/core" sits inside "src"; the file must still be indexed exactly once.
        await orchestrator.IndexAsync(
            new WorkspaceSelection(_root, ["src", "src/core"]),
            sink);

        Assert.Single(sink.Results);
    }

    [Fact]
    public async Task CarriesContentHashAndFileMetadataThrough()
    {
        WriteFile("src/a.c", "int a(void) { return 0; }");

        var (orchestrator, sink) = CreatePipeline();
        await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), sink);

        var result = Assert.Single(sink.Results);

        // The hash is what drives incremental re-indexing, so it must survive the pipeline.
        Assert.NotEmpty(result.ContentHash);
        Assert.True(result.Size > 0);
        Assert.True(result.ModifiedUnixMs > 0);
    }

    [Fact]
    public async Task IncrementalGateSkipsUnchangedFiles()
    {
        WriteFile("src/a.c", "int a(void) { return 0; }");
        WriteFile("src/b.c", "int b(void) { return 0; }");

        var (orchestrator, sink) = CreatePipeline();

        var outcome = await orchestrator.IndexAsync(
            WorkspaceSelection.EntireWorkspace(_root),
            sink,
            incrementalGate: new StubGate(unchangedPath: "src/a.c"));

        Assert.Equal(1, outcome.FilesUnchanged);
        Assert.Equal(1, outcome.FilesParsed);

        var result = Assert.Single(sink.Results);
        Assert.Equal("src/b.c", result.RelativePath);
    }

    [Fact]
    public async Task ReportsProgressAndCompletes()
    {
        for (var i = 0; i < 40; i++)
        {
            WriteFile($"src/file{i}.c", $"int fn_{i}(void) {{ return {i}; }}");
        }

        var (orchestrator, sink) = CreatePipeline();

        var reports = new ConcurrentBag<IndexProgress>();
        var progress = new Progress<IndexProgress>(reports.Add);

        var outcome = await orchestrator.IndexAsync(
            WorkspaceSelection.EntireWorkspace(_root),
            sink,
            progress: progress);

        Assert.Equal(40, outcome.FilesParsed);
        Assert.Equal(40, outcome.SymbolsFound);

        // Progress is delivered via the synchronization context, so allow it to drain.
        await Task.Delay(200);
        Assert.Contains(reports, r => r.Phase == IndexPhase.Complete);
    }

    [Fact]
    public async Task CancellationStopsIndexingAndReportsIt()
    {
        for (var i = 0; i < 400; i++)
        {
            WriteFile($"src/file{i}.c", $"int fn_{i}(void) {{ return {i}; }}");
        }

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension);
        var orchestrator = new IndexOrchestrator(crawler, factory);

        using var cts = new CancellationTokenSource();
        var sink = new CollectingSink(onWrite: () => cts.Cancel());

        var outcome = await orchestrator.IndexAsync(
            WorkspaceSelection.EntireWorkspace(_root),
            sink,
            cancellationToken: cts.Token);

        Assert.True(outcome.WasCancelled);

        // Cancelling must stop early rather than draining the whole workspace.
        Assert.True(outcome.FilesParsed < 400);
    }

    [Fact]
    public async Task MalformedFileDoesNotAbortTheRun()
    {
        WriteFile("src/good.c", "int good(void) { return 1; }");
        WriteFile("src/broken.c", "int broken(void) { return");

        var (orchestrator, sink) = CreatePipeline();
        var outcome = await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), sink);

        // A syntax error is not a skipped file: both are parsed and both reach the sink.
        Assert.Equal(2, outcome.FilesParsed);
        Assert.Equal(0, outcome.FilesFailed);
        Assert.Equal(1, outcome.FilesWithSyntaxErrors);

        Assert.Contains(sink.Results, r => r.RelativePath == "src/good.c" && r.Status == FileStatus.Ok);
        Assert.Contains(sink.Results, r => r.RelativePath == "src/broken.c" && r.Status == FileStatus.ParseError);
    }

    [Fact]
    public async Task BinaryFileWithSourceExtensionIsSkipped()
    {
        var full = Path.Combine(_root, "src", "fake.c");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01, 0x02]);

        var (orchestrator, sink) = CreatePipeline();
        var outcome = await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), sink);

        Assert.Empty(sink.Results);
        Assert.Equal(1, outcome.FilesFailed);
    }

    private sealed class CollectingSink(Action? onWrite = null) : IParseResultSink
    {
        private readonly ConcurrentQueue<ParseResult> _results = new();

        public IReadOnlyCollection<ParseResult> Results => _results;

        public bool Completed { get; private set; }

        public Task WriteAsync(ParseResult result, CancellationToken cancellationToken)
        {
            _results.Enqueue(result);
            onWrite?.Invoke();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StubGate(string unchangedPath) : IIncrementalGate
    {
        public bool IsUnchanged(string relativePath, long size, long modifiedUnixMs) =>
            relativePath == unchangedPath;
    }
}
