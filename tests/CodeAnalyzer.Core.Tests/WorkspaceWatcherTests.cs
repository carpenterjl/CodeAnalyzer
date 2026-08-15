using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Watching;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// Drives a real <see cref="FileSystemWatcher"/> over a real temp tree, because the parts
/// worth testing — coalescing, the quiet period, what a rename looks like from outside — are
/// exactly the parts a fake would get to define for itself.
/// <para>
/// Timings are scaled down but still generous relative to the work being done. Every wait is
/// on a signal with a timeout rather than a fixed sleep, so a slow machine makes the test
/// slower, not flaky.
/// </para>
/// </summary>
public class WorkspaceWatcherTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-watch", Guid.NewGuid().ToString("N"));

    private WorkspaceWatcher? _watcher;

    public WorkspaceWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _watcher?.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }
    }

    private static bool IsSource(string extension) =>
        extension is ".c" or ".h" or ".py";

    private BatchCollector StartWatching(params string[] selection) =>
        StartWatching(gitIgnoreProvider: null, selection);

    private BatchCollector StartWatching(Func<GitIgnoreRules?>? gitIgnoreProvider, params string[] selection)
    {
        _watcher = new WorkspaceWatcher(_root, IsSource)
        {
            QuietPeriod = Quiet,
            MaxDelay = TimeSpan.FromSeconds(2),
            GitIgnoreProvider = gitIgnoreProvider,
        };

        var collector = new BatchCollector(_watcher);
        _watcher.Start(selection.Length == 0 ? [string.Empty] : selection);
        return collector;
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void ACreatedFileIsReported()
    {
        var collector = StartWatching();

        Write("main.c", "int main(void) { return 0; }");

        var batch = collector.WaitForNext();
        Assert.Contains("main.c", batch.ChangedFiles);
        Assert.False(batch.ResyncRequired);
    }

    [Fact]
    public void AGitIgnoreEditForcesAResyncWhenHonored()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write(".gitignore", "gen/\n");

        var collector = StartWatching(() => GitIgnoreRules.TryDiscover(_root));

        // Editing the rules invalidates crawl decisions the index already embodies, and
        // nothing tracks which — a full re-index is the honest answer.
        Write(".gitignore", "gen/\n*.tmp\n");

        var batch = collector.WaitForNext();
        Assert.True(batch.ResyncRequired);
        Assert.Empty(batch.ChangedFiles);
    }

    [Fact]
    public void AGitIgnoreEditChangesNothingWhenNotHonored()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write(".gitignore", "gen/\n");

        var collector = StartWatching();

        Write(".gitignore", "gen/\n*.tmp\n");
        Write("main.c", "int main(void) { return 0; }");

        var batch = collector.WaitForNext();
        Assert.Equal(["main.c"], batch.ChangedFiles);
        Assert.False(batch.ResyncRequired);
    }

    [Fact]
    public void AGitIgnoredFileDoesNotWakeTheIndexer()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write(".gitignore", "gen/\n");

        var collector = StartWatching(() => GitIgnoreRules.TryDiscover(_root));

        Write("gen/lut.c", "int lut[] = {1};");
        Write("src/main.c", "int main(void) { return 0; }");

        var batch = collector.WaitForBatchWhere(b => b.ChangedFiles.Contains("src/main.c"));
        Assert.NotNull(batch);
        Assert.DoesNotContain("gen/lut.c", batch.ChangedFiles);
    }

    [Fact]
    public void SeveralWritesToOneFileArriveAsOneBatch()
    {
        var path = Write("main.c", "int main(void) { return 0; }");
        var collector = StartWatching();

        // What an editor's save actually looks like: a burst, not a single event.
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(path, $"int main(void) {{ return {i}; }}");
            Thread.Sleep(10);
        }

        var batch = collector.WaitForNext();
        Assert.Equal(["main.c"], batch.ChangedFiles);
    }

    [Fact]
    public void ADeletedFileIsReportedAsRemovedRatherThanChanged()
    {
        var path = Write("main.c", "int main(void) { return 0; }");
        var collector = StartWatching();

        File.Delete(path);

        var batch = collector.WaitForNext();
        Assert.Equal(["main.c"], batch.RemovedPaths);
        Assert.Empty(batch.ChangedFiles);
    }

    [Fact]
    public void ARenameIsReportedAsBothEnds()
    {
        var path = Write("old.c", "int f(void) { return 0; }");
        var collector = StartWatching();

        File.Move(path, Path.Combine(_root, "new.c"));

        var batch = collector.WaitForBatchWhere(b =>
            b.RemovedPaths.Contains("old.c") && b.ChangedFiles.Contains("new.c"));

        Assert.NotNull(batch);
    }

    [Fact]
    public void ADeletedDirectoryIsReportedByItsOwnPath()
    {
        Write("drivers/uart.c", "int uart_init(void) { return 0; }");
        var collector = StartWatching();

        Directory.Delete(Path.Combine(_root, "drivers"), recursive: true);

        // Windows reports the files and the directory, or just the directory, depending on
        // timing. Either way the directory itself has to be in the batch, because that is
        // the only entry that can stand for files the watcher never mentioned.
        var batch = collector.WaitForBatchWhere(b => b.RemovedPaths.Contains("drivers"));
        Assert.NotNull(batch);
    }

    [Fact]
    public void AnUnsupportedExtensionIsNotReported()
    {
        var collector = StartWatching();

        Write("notes.txt", "not source");
        Write("main.c", "int main(void) { return 0; }");

        // main.c is the tracer: if it arrives and notes.txt did not, the filter held.
        var batch = collector.WaitForBatchWhere(b => b.ChangedFiles.Contains("main.c"));

        Assert.NotNull(batch);
        Assert.DoesNotContain(collector.All.SelectMany(b => b.ChangedFiles), p => p.EndsWith(".txt"));
    }

    [Fact]
    public void AnIgnoredDirectoryIsNotReported()
    {
        var collector = StartWatching();

        Write("obj/generated.c", "int generated(void) { return 0; }");
        Write("main.c", "int main(void) { return 0; }");

        var batch = collector.WaitForBatchWhere(b => b.ChangedFiles.Contains("main.c"));

        Assert.NotNull(batch);
        Assert.DoesNotContain(collector.All.SelectMany(b => b.ChangedFiles), p => p.StartsWith("obj/"));
    }

    [Fact]
    public void ChangesOutsideTheSelectionAreNotReported()
    {
        Write("drivers/uart.c", "int uart_init(void) { return 0; }");
        Write("app/main.c", "int main(void) { return 0; }");

        var collector = StartWatching("drivers");

        File.WriteAllText(Path.Combine(_root, "app", "main.c"), "int main(void) { return 1; }");
        File.WriteAllText(Path.Combine(_root, "drivers", "uart.c"), "int uart_init(void) { return 1; }");

        var batch = collector.WaitForBatchWhere(b => b.ChangedFiles.Contains("drivers/uart.c"));

        Assert.NotNull(batch);
        Assert.DoesNotContain(collector.All.SelectMany(b => b.ChangedFiles), p => p.StartsWith("app/"));
    }

    [Fact]
    public void AnExtraIgnoredDirectoryIsNotReported()
    {
        _watcher = new WorkspaceWatcher(
            _root,
            IsSource,
            new Core.Crawling.WorkspaceSettings { ExtraIgnoredDirectories = ["generated"] })
        {
            QuietPeriod = Quiet,
            MaxDelay = TimeSpan.FromSeconds(2),
        };

        var collector = new BatchCollector(_watcher);
        _watcher.Start([string.Empty]);

        Write("generated/machine.c", "int machine(void) { return 0; }");
        Write("main.c", "int main(void) { return 0; }");

        var batch = collector.WaitForBatchWhere(b => b.ChangedFiles.Contains("main.c"));

        Assert.NotNull(batch);
        Assert.DoesNotContain(
            collector.All.SelectMany(b => b.ChangedFiles.Concat(b.ChangedDirectories)),
            p => p.StartsWith("generated"));
    }

    [Fact]
    public void StoppingSilencesTheWatcher()
    {
        var collector = StartWatching();
        _watcher!.Stop();

        Write("main.c", "int main(void) { return 0; }");

        Assert.False(collector.WaitForAny(TimeSpan.FromMilliseconds(400)));
    }

    /// <summary>Collects batches off the watcher's timer thread.</summary>
    private sealed class BatchCollector
    {
        private readonly List<WorkspaceChangeBatch> _batches = [];
        private readonly SemaphoreSlim _arrived = new(0);

        public BatchCollector(WorkspaceWatcher watcher) =>
            watcher.ChangesReady += (_, batch) =>
            {
                lock (_batches)
                {
                    _batches.Add(batch);
                }

                _arrived.Release();
            };

        public IReadOnlyList<WorkspaceChangeBatch> All
        {
            get
            {
                lock (_batches)
                {
                    return _batches.ToList();
                }
            }
        }

        public bool WaitForAny(TimeSpan timeout) => _arrived.Wait(timeout);

        public WorkspaceChangeBatch WaitForNext()
        {
            Assert.True(_arrived.Wait(Wait), "no change batch arrived");

            lock (_batches)
            {
                return _batches[^1];
            }
        }

        /// <summary>
        /// Waits until some batch satisfies the predicate. The filesystem is free to split one
        /// logical change across batches, so a test that cares about content must not assume
        /// it all lands at once.
        /// </summary>
        public WorkspaceChangeBatch? WaitForBatchWhere(Func<WorkspaceChangeBatch, bool> predicate)
        {
            var deadline = DateTime.UtcNow + Wait;

            while (DateTime.UtcNow < deadline)
            {
                if (All.Any(predicate))
                {
                    return All.First(predicate);
                }

                _arrived.Wait(TimeSpan.FromMilliseconds(100));
            }

            Assert.Fail("no batch matched: " +
                string.Join(" | ", All.Select(b =>
                    $"changed=[{string.Join(",", b.ChangedFiles)}] " +
                    $"dirs=[{string.Join(",", b.ChangedDirectories)}] " +
                    $"removed=[{string.Join(",", b.RemovedPaths)}]")));

            return null;
        }
    }
}
