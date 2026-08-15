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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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

        // Nothing here is slow, so no report may carry the slow-file heartbeat fields.
        Assert.All(reports, r => Assert.Null(r.SlowFile));
    }

    [Fact]
    public async Task ASlowParseIsReportedByNameInsteadOfLookingWedged()
    {
        WriteFile("src/slow.c", "int slow(void) { return 0; }");

        using var release = new ManualResetEventSlim(false);
        var factory = new BlockingFactory(release);
        var crawler = new FileCrawler(factory.IsSupportedExtension);

        var orchestrator = new IndexOrchestrator(crawler, factory, new IndexOptions
        {
            WorkerCount = 1,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            SlowParseThreshold = TimeSpan.FromMilliseconds(100),
        });

        var reports = new ConcurrentBag<IndexProgress>();
        using var sawSlowReport = new SemaphoreSlim(0);

        // A raw IProgress implementation: the Progress<T> class posts through the sync
        // context, which this test cannot pump while it waits.
        var progress = new InlineProgress(p =>
        {
            reports.Add(p);
            if (p.SlowFile is not null)
            {
                sawSlowReport.Release();
            }
        });

        var run = orchestrator.IndexAsync(
            WorkspaceSelection.EntireWorkspace(_root),
            new CollectingSink(),
            progress: progress);

        try
        {
            // The heartbeat must name the file while the parse is still in progress.
            Assert.True(await sawSlowReport.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Set();
        }

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, outcome.FilesParsed);

        var slow = reports.First(r => r.SlowFile is not null);
        Assert.Equal("src/slow.c", slow.SlowFile);
        Assert.NotNull(slow.SlowFileSeconds);
    }

    private sealed class InlineProgress(Action<IndexProgress> handler) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => handler(value);
    }

    private sealed class BlockingFactory(ManualResetEventSlim release) : IAnalyzerFactory
    {
        public bool IsSupportedExtension(string extension) =>
            extension.Equals(".c", StringComparison.OrdinalIgnoreCase);

        public string? GetLanguageForExtension(string extension) =>
            IsSupportedExtension(extension) ? "C" : null;

        public ILanguageAnalyzer Create(string language) => new BlockingAnalyzer(release);

        private sealed class BlockingAnalyzer(ManualResetEventSlim release) : ILanguageAnalyzer
        {
            public string Language => "C";

            public ParseResult Analyze(string relativePath, string source, CancellationToken cancellationToken)
            {
                release.Wait(TimeSpan.FromSeconds(30));

                return new ParseResult
                {
                    RelativePath = relativePath,
                    Language = "C",
                    ContentHash = [],
                    Status = FileStatus.Ok,
                };
            }

            public void Dispose()
            {
            }
        }
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

    [Fact]
    public async Task ACheckedInPythonEnvironmentIsSkippedWhateverItIsNamed()
    {
        // The reported workspace froze on a venv committed as "Environment" — a name no
        // ignore list can anticipate. The venv's own pyvenv.cfg is the fact that says
        // what the directory is.
        WriteFile("src/app.py", "def run():\n    return 1\n");
        WriteFile("Python/Environment/pyvenv.cfg", "home = C:\\Python312");
        WriteFile("Python/Environment/Lib/pkg/mod.py", "def hidden():\n    return 2\n");
        WriteFile("conda-env/conda-meta/history", "==> log <==");
        WriteFile("conda-env/script.py", "def also_hidden():\n    return 3\n");

        // A sibling with an unrelated .cfg must still crawl.
        WriteFile("tools/setup.cfg", "[metadata]");
        WriteFile("tools/helper.py", "def kept():\n    return 4\n");

        var (orchestrator, sink) = CreatePipeline();
        await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), sink);

        var paths = sink.Results.Select(r => r.RelativePath).OrderBy(p => p).ToList();
        Assert.Equal(new[] { "src/app.py", "tools/helper.py" }, paths);
    }

    [Fact]
    public async Task AJunctionIsNotDescended()
    {
        WriteFile("src/real.c", "int real(void) { return 1; }");
        WriteFile("elsewhere/dupe.c", "int dupe(void) { return 2; }");

        // A junction from inside the crawl scope to a sibling: without the guard the
        // same physical file indexes twice (fabricated ambiguity), and a junction cycle
        // would not terminate at all. Junctions need no elevation, unlike symlinks.
        var link = Path.Combine(_root, "src", "linked");
        var target = Path.Combine(_root, "elsewhere");

        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        if (mklink.ExitCode != 0)
        {
            // Junction creation unavailable on this volume; nothing to verify here.
            // (xunit 2.x has no dynamic skip, so this passes vacuously instead.)
            return;
        }

        try
        {
            var (orchestrator, sink) = CreatePipeline();
            await orchestrator.IndexAsync(
                new WorkspaceSelection(_root, ["src"]),
                sink);

            var result = Assert.Single(sink.Results);
            Assert.Equal("src/real.c", result.RelativePath);
        }
        finally
        {
            // A recursive delete refuses to walk into a junction; removing the link
            // itself (non-recursive) is what lets the fixture's cleanup succeed.
            Directory.Delete(link, recursive: false);
        }
    }

    [Fact]
    public async Task GitIgnoreRulesExcludeFilesWhenHonored()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        WriteFile(".gitignore", "Debug/\n*.gen.c\n!keep.gen.c\n");
        WriteFile("src/main.c", "int main(void) { return 0; }");
        WriteFile("src/lut.gen.c", "int lut[] = {1};");
        WriteFile("src/keep.gen.c", "int kept(void) { return 1; }");
        WriteFile("Firmware/Debug/build.c", "int not_source(void) { return 0; }");

        var factory = new TreeSitterAnalyzerFactory();

        // Honored: the repository's statement of what is not source is followed.
        var honoring = new IndexOrchestrator(
            new FileCrawler(factory.IsSupportedExtension, gitIgnore: GitIgnoreRules.TryDiscover(_root)),
            factory);

        var sink = new CollectingSink();
        await honoring.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), sink);

        Assert.Equal(
            new[] { "src/keep.gen.c", "src/main.c" },
            sink.Results.Select(r => r.RelativePath).OrderBy(p => p));

        // Not honored (no rules passed): today's behaviour, byte for byte.
        var plain = new IndexOrchestrator(new FileCrawler(factory.IsSupportedExtension), factory);
        var plainSink = new CollectingSink();
        await plain.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), plainSink);

        Assert.Equal(4, plainSink.Results.Count);
    }

    [Fact]
    public async Task AFaultedWriterFailsTheRunInsteadOfWedgingIt()
    {
        // The original shutdown order awaited the writer last, so a writer fault left
        // every worker blocked forever on the full result channel and the exception was
        // never observed — the run froze at N/M with no error. This test deadlocks
        // against that code; the WaitAsync is what turns the wedge into a failure.
        for (var i = 0; i < 50; i++)
        {
            WriteFile($"src/file{i}.c", $"int fn_{i}(void) {{ return {i}; }}");
        }

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension);

        // A tiny result queue makes the workers hit the blocked channel almost at once.
        var orchestrator = new IndexOrchestrator(crawler, factory, new IndexOptions
        {
            ResultQueueCapacity = 2,
        });

        var sink = new ThrowingSink();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), sink)
                .WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("sink is broken", thrown.Message);

        // A faulted run must not pretend it finished: no completion flush.
        Assert.False(sink.Completed);
    }

    [Fact]
    public async Task AFailedAnalyzerFactoryListsEveryAffectedFileAndFinishes()
    {
        // An analyzer that cannot be constructed (say, a missing grammar DLL) used to
        // throw straight out of the worker loop, killing the worker and — with the
        // crawler blocked on the work channel — wedging the run. It must instead cost
        // one construction attempt per worker, with every affected file counted and
        // listed as skipped with the reason.
        WriteFile("src/a.c", "int a(void) { return 0; }");
        WriteFile("src/b.c", "int b(void) { return 0; }");
        WriteFile("src/c.c", "int c(void) { return 0; }");

        var factory = new BrokenFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension);
        var orchestrator = new IndexOrchestrator(crawler, factory, new IndexOptions
        {
            WorkerCount = 1,
        });

        var sink = new CollectingSink();

        var outcome = await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), sink)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(outcome.WasCancelled);
        Assert.Equal(3, outcome.FilesFailed);
        Assert.Equal(0, outcome.FilesParsed);

        // One attempt per worker per language, not one per file.
        Assert.Equal(1, factory.CreateCalls);

        Assert.Equal(3, sink.Results.Count);
        Assert.All(sink.Results, r =>
        {
            Assert.Equal(FileStatus.Skipped, r.Status);
            Assert.Contains("could not be created", r.ErrorMessage);
            Assert.Contains("no grammar", r.ErrorMessage);
        });
    }

    private sealed class ThrowingSink : IParseResultSink
    {
        public bool Completed { get; private set; }

        public Task WriteAsync(ParseResult result, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sink is broken");

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class BrokenFactory : IAnalyzerFactory
    {
        private int _createCalls;

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public bool IsSupportedExtension(string extension) =>
            extension.Equals(".c", StringComparison.OrdinalIgnoreCase);

        public string? GetLanguageForExtension(string extension) =>
            IsSupportedExtension(extension) ? "C" : null;

        public ILanguageAnalyzer Create(string language)
        {
            Interlocked.Increment(ref _createCalls);
            throw new InvalidOperationException("no grammar");
        }
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
