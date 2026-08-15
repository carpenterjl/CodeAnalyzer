using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using CodeAnalyzer.Core.Analysis;
using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Indexing;

/// <summary>Receives parse results as they complete. Implemented by the SQLite writer.</summary>
public interface IParseResultSink
{
    /// <summary>
    /// Called from a single consumer task, so implementations need no internal locking.
    /// </summary>
    Task WriteAsync(ParseResult result, CancellationToken cancellationToken);

    /// <summary>Called once after the last result, to flush any open batch.</summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>Decides whether a crawled file still needs parsing.</summary>
public interface IIncrementalGate
{
    /// <summary>
    /// True when the file is unchanged since the last index and can be skipped.
    /// Checked before reading contents, using size and timestamp only.
    /// </summary>
    bool IsUnchanged(string relativePath, long size, long modifiedUnixMs);
}

public sealed record IndexOptions
{
    /// <summary>Parse workers. Defaults to the core count, which is what saturates CPU-bound parsing.</summary>
    public int WorkerCount { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>Bound on queued files, so the crawler cannot outrun parsing and exhaust memory.</summary>
    public int CrawlQueueCapacity { get; init; } = 1024;

    /// <summary>Bound on parsed-but-unwritten results.</summary>
    public int ResultQueueCapacity { get; init; } = 256;

    /// <summary>Minimum gap between progress reports, to keep UI updates cheap.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How often the heartbeat looks at the in-flight slots.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A parse older than this is reported by name. There is no per-file timeout (a user
    /// decision — see M13), so this is what keeps a slow parse distinguishable from a
    /// wedged pipeline: the status bar names the file and counts upward.
    /// </summary>
    public TimeSpan SlowParseThreshold { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed record IndexOutcome(
    int FilesDiscovered,
    int FilesParsed,
    int FilesUnchanged,
    int FilesFailed,
    int FilesWithSyntaxErrors,
    int SymbolsFound,
    bool WasCancelled);

/// <summary>
/// Runs the indexing pipeline: crawl → parse (N workers) → write (single consumer).
/// <para>
/// Stages are connected by bounded channels, so a slow writer applies backpressure to
/// parsing and parsing applies backpressure to the crawl. Memory stays flat regardless
/// of workspace size, which is what makes a 20k+ file repo tractable.
/// </para>
/// </summary>
public sealed class IndexOrchestrator
{
    private readonly IFileCrawler _crawler;
    private readonly IAnalyzerFactory _analyzerFactory;
    private readonly IndexOptions _options;

    public IndexOrchestrator(
        IFileCrawler crawler,
        IAnalyzerFactory analyzerFactory,
        IndexOptions? options = null)
    {
        _crawler = crawler;
        _analyzerFactory = analyzerFactory;
        _options = options ?? new IndexOptions();
    }

    public async Task<IndexOutcome> IndexAsync(
        WorkspaceSelection selection,
        IParseResultSink sink,
        IIncrementalGate? incrementalGate = null,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var counters = new Counters();
        var reporter = new ThrottledProgressReporter(progress, _options.ProgressInterval, counters);

        var crawlChannel = Channel.CreateBounded<FileWorkItem>(new BoundedChannelOptions(_options.CrawlQueueCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var resultChannel = Channel.CreateBounded<ParseResult>(new BoundedChannelOptions(_options.ResultQueueCapacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // A fault in any stage cancels this token, which unblocks every other stage still
        // waiting on a bounded channel. Without it, a faulted writer leaves the workers
        // blocked forever on the full result channel and its exception is never observed
        // — the run freezes at N/M with no error, which is exactly the bug this shape
        // replaces. Only the first fault is kept; everything after it is unwinding noise.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? firstFault = null;

        void Fail(Exception ex)
        {
            if (Interlocked.CompareExchange(ref firstFault, ex, null) is null)
            {
                linked.Cancel();
            }
        }

        // Each stage is wrapped so it never throws out of its task: an expected
        // cancellation unwind is swallowed, anything else records the fault and trips the
        // teardown. An OperationCanceledException nobody asked for is a fault too.
        async Task Guarded(Func<Task> stage)
        {
            try
            {
                await stage().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        var crawlTask = Guarded(() => RunCrawlAsync(selection, crawlChannel.Writer, incrementalGate, counters, reporter, linked.Token));

        // One slot per worker, holding the file currently inside its parser. Written with
        // volatile immutable records so the heartbeat can read them from its own task
        // without ever seeing a torn value.
        var slots = new InFlightSlot?[_options.WorkerCount];

        var workerTasks = Enumerable.Range(0, _options.WorkerCount)
            .Select(workerIndex => Guarded(() => RunWorkerAsync(crawlChannel.Reader, resultChannel.Writer, counters, reporter, slots, workerIndex, linked.Token)))
            .ToArray();

        var writerTask = Guarded(() => RunWriterAsync(resultChannel.Reader, sink, linked.Token));

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeatTask = RunHeartbeatAsync(slots, reporter, _options, heartbeatCts.Token);

        try
        {
            // Every task completes even under fault or cancellation: the linked token
            // unblocks channel waits, the crawl's finally completes the crawl channel, and
            // Guarded absorbs the unwind. Nothing is abandoned — the old cancel path dropped
            // the writer task un-awaited, racing it against the caller's post-index work.
            await crawlTask.ConfigureAwait(false);
            await Task.WhenAll(workerTasks).ConfigureAwait(false);

            // Workers are done, so no further results can arrive.
            resultChannel.Writer.TryComplete();
            await writerTask.ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeatTask.ConfigureAwait(false);
        }

        if (firstFault is not null)
        {
            // Surfaces through WorkspaceSession to the shell's error handling. The
            // completion flush, the deletion sweep and the resolve are all skipped, so a
            // faulted run can never masquerade as a finished one.
            ExceptionDispatchInfo.Capture(firstFault).Throw();
        }

        var wasCancelled = cancellationToken.IsCancellationRequested;

        if (!wasCancelled)
        {
            try
            {
                await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
            }
        }

        reporter.ReportFinal(wasCancelled ? IndexPhase.Cancelled : IndexPhase.Complete);

        return new IndexOutcome(
            counters.Discovered,
            counters.Parsed,
            counters.Unchanged,
            counters.Failed,
            counters.SyntaxErrors,
            counters.Symbols,
            wasCancelled);
    }

    private async Task RunCrawlAsync(
        WorkspaceSelection selection,
        ChannelWriter<FileWorkItem> writer,
        IIncrementalGate? incrementalGate,
        Counters counters,
        ThrottledProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        try
        {
            // Directory enumeration is blocking I/O; keep it off the caller's thread.
            await Task.Run(
                async () =>
                {
                    foreach (var item in _crawler.Crawl(selection, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        counters.IncrementDiscovered();

                        if (incrementalGate?.IsUnchanged(item.RelativePath, item.Size, item.ModifiedUnixMs) == true)
                        {
                            counters.IncrementUnchanged();
                            reporter.Report(IndexPhase.Crawling, item.RelativePath);
                            continue;
                        }

                        await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                        reporter.Report(IndexPhase.Crawling, item.RelativePath);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task RunWorkerAsync(
        ChannelReader<FileWorkItem> reader,
        ChannelWriter<ParseResult> writer,
        Counters counters,
        ThrottledProgressReporter reporter,
        InFlightSlot?[] slots,
        int slotIndex,
        CancellationToken cancellationToken)
    {
        await Task.Run(
            async () =>
            {
                // Analyzers are expensive to build and not thread-safe, so each worker
                // keeps its own per-language cache for its whole lifetime. A language
                // whose analyzer cannot be constructed (a missing grammar, a broken query
                // pack) is cached as failed so it costs one attempt per worker, not one
                // exception per file — and never kills the worker, which would wedge the
                // crawler on the work channel.
                var analyzers = new Dictionary<string, ILanguageAnalyzer>(StringComparer.OrdinalIgnoreCase);
                var failedLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        ParseResult? result;

                        Volatile.Write(ref slots[slotIndex], new InFlightSlot(item.RelativePath, Environment.TickCount64));
                        try
                        {
                            result = ParseOne(item, analyzers, failedLanguages, counters, cancellationToken);
                        }
                        finally
                        {
                            // Cleared even on an unwind: a stale slot would have the
                            // heartbeat naming a file nobody is parsing.
                            Volatile.Write(ref slots[slotIndex], null);
                        }

                        if (result is null)
                        {
                            continue;
                        }

                        await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
                        reporter.Report(IndexPhase.Parsing, item.RelativePath);
                    }
                }
                finally
                {
                    foreach (var analyzer in analyzers.Values)
                    {
                        analyzer.Dispose();
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private ParseResult? ParseOne(
        FileWorkItem item,
        Dictionary<string, ILanguageAnalyzer> analyzers,
        Dictionary<string, string> failedLanguages,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var language = _analyzerFactory.GetLanguageForExtension(Path.GetExtension(item.RelativePath));
        if (language is null)
        {
            // The crawler already filters unsupported extensions, so reaching here means
            // the crawler and the factory disagree.
            counters.IncrementFailed();
            return null;
        }

        var content = SourceFileReader.TryRead(item.FullPath, cancellationToken);
        if (content is null)
        {
            // Unreadable or binary despite a source extension.
            counters.IncrementFailed();
            return null;
        }

        if (!analyzers.TryGetValue(language, out var analyzer))
        {
            if (!failedLanguages.TryGetValue(language, out var creationError))
            {
                try
                {
                    analyzer = _analyzerFactory.Create(language);
                    analyzers[language] = analyzer;
                }
                catch (Exception ex)
                {
                    creationError = ex.Message;
                    failedLanguages[language] = creationError;
                }
            }

            if (analyzer is null)
            {
                // The file itself is fine; the tool could not bring up a parser for its
                // language. Stored as a skipped row so the error pane states exactly
                // that, rather than the file silently vanishing from the index.
                counters.IncrementFailed();
                return new ParseResult
                {
                    RelativePath = item.RelativePath,
                    Language = language,
                    ContentHash = content.ContentHash,
                    Size = item.Size,
                    ModifiedUnixMs = item.ModifiedUnixMs,
                    Status = FileStatus.Skipped,
                    ErrorMessage = $"analyzer for {language} could not be created: {creationError}",
                };
            }
        }

        var result = analyzer.Analyze(item.RelativePath, content.Text, cancellationToken);

        result = result with
        {
            ContentHash = content.ContentHash,
            Size = item.Size,
            ModifiedUnixMs = item.ModifiedUnixMs,
        };

        // A file that produced a result counts as parsed even when its tree had syntax
        // errors: tree-sitter recovers, and the partial symbols are still real facts.
        // Syntax errors are tracked separately so the UI can list them without implying
        // the file was skipped.
        counters.IncrementParsed(result.Symbols.Count);

        if (result.Status == FileStatus.ParseError)
        {
            counters.IncrementSyntaxError();
        }

        return result;
    }

    private static async Task RunWriterAsync(
        ChannelReader<ParseResult> reader,
        IParseResultSink sink,
        CancellationToken cancellationToken)
    {
        // A single consumer is what lets the SQLite writer batch into one transaction
        // without any locking of its own.
        await foreach (var result in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await sink.WriteAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What one worker is parsing right now, and since when. Immutable so a
    /// heartbeat read can never tear.</summary>
    private sealed record InFlightSlot(string RelativePath, long StartedTicks);

    /// <summary>
    /// Reports the oldest in-flight parse once it crosses the slow threshold, at 1 Hz —
    /// inside the reporter's own 10 Hz budget, so it bypasses the throttle deliberately.
    /// Progress is otherwise only reported *after* work completes, which is exactly why a
    /// long parse used to be indistinguishable from a deadlock.
    /// </summary>
    private static async Task RunHeartbeatAsync(
        InFlightSlot?[] slots,
        ThrottledProgressReporter reporter,
        IndexOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);

                InFlightSlot? oldest = null;
                for (var i = 0; i < slots.Length; i++)
                {
                    var slot = Volatile.Read(ref slots[i]);
                    if (slot is not null && (oldest is null || slot.StartedTicks < oldest.StartedTicks))
                    {
                        oldest = slot;
                    }
                }

                if (oldest is null)
                {
                    continue;
                }

                var ageMs = Environment.TickCount64 - oldest.StartedTicks;
                if (ageMs >= options.SlowParseThreshold.TotalMilliseconds)
                {
                    reporter.ReportSlow(oldest.RelativePath, (int)(ageMs / 1000));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown; the heartbeat has nothing to flush.
        }
    }

    /// <summary>Interlocked counters shared across workers.</summary>
    private sealed class Counters
    {
        private int _discovered;
        private int _parsed;
        private int _unchanged;
        private int _failed;
        private int _syntaxErrors;
        private int _symbols;

        public int Discovered => Volatile.Read(ref _discovered);
        public int Parsed => Volatile.Read(ref _parsed);
        public int Unchanged => Volatile.Read(ref _unchanged);
        public int Failed => Volatile.Read(ref _failed);
        public int SyntaxErrors => Volatile.Read(ref _syntaxErrors);
        public int Symbols => Volatile.Read(ref _symbols);

        public void IncrementDiscovered() => Interlocked.Increment(ref _discovered);
        public void IncrementUnchanged() => Interlocked.Increment(ref _unchanged);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);
        public void IncrementSyntaxError() => Interlocked.Increment(ref _syntaxErrors);

        public void IncrementParsed(int symbolCount)
        {
            Interlocked.Increment(ref _parsed);
            Interlocked.Add(ref _symbols, symbolCount);
        }
    }

    /// <summary>
    /// Drops progress reports that arrive inside the throttle window. Without this a fast
    /// index would post tens of thousands of dispatcher callbacks and stall the UI.
    /// </summary>
    private sealed class ThrottledProgressReporter(
        IProgress<IndexProgress>? progress,
        TimeSpan interval,
        Counters counters)
    {
        private long _lastReportTicks;

        public void Report(IndexPhase phase, string? currentFile)
        {
            if (progress is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReportTicks);

            if (now - last < interval.TotalMilliseconds)
            {
                return;
            }

            // Only the thread that wins the exchange publishes, so reports stay rate-limited
            // even when every worker reports at once.
            if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last)
            {
                return;
            }

            progress.Report(Snapshot(phase, currentFile));
        }

        public void ReportFinal(IndexPhase phase) => progress?.Report(Snapshot(phase, null));

        /// <summary>
        /// Heartbeat report: unthrottled on purpose (its own cadence is 1 Hz), and the
        /// only kind of report that carries the slow-file fields.
        /// </summary>
        public void ReportSlow(string relativePath, int seconds) =>
            progress?.Report(Snapshot(IndexPhase.Parsing, relativePath) with
            {
                SlowFile = relativePath,
                SlowFileSeconds = seconds,
            });

        private IndexProgress Snapshot(IndexPhase phase, string? currentFile) => new()
        {
            Phase = phase,
            FilesDiscovered = counters.Discovered,
            FilesParsed = counters.Parsed,
            FilesUnchanged = counters.Unchanged,
            FilesFailed = counters.Failed,
            FilesWithSyntaxErrors = counters.SyntaxErrors,
            SymbolsFound = counters.Symbols,
            CurrentFile = currentFile,
        };
    }
}
