using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Workspaces;
using CodeAnalyzer.Parsing;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Cli.Commands;

/// <summary>
/// <c>codeanalyzer index [path]</c> — builds or refreshes a workspace's cache headlessly,
/// through the exact pipeline the GUI runs: same crawler, same guards, same resolver.
/// The read commands never index implicitly; this is the one explicit way in.
/// </summary>
internal static class IndexCommand
{
    public static CommandSpec Spec { get; } = new(
        "index",
        "index [path] [--full]",
        "build or refresh the workspace's index (defaults to the current directory); "
            + "--full re-parses every file instead of skipping unchanged ones",
        Run);

    private static async Task<int> Run(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, [], ["full"]);
        if (args.Error is not null)
        {
            Console.Error.WriteLine(args.Error);
            return ExitCodes.Error;
        }

        if (args.Positionals.Count > 1)
        {
            Console.Error.WriteLine("usage: codeanalyzer " + Spec.Usage);
            return ExitCodes.Error;
        }

        var root = Path.GetFullPath(args.Positionals.Count == 1
            ? args.Positionals[0]
            : Directory.GetCurrentDirectory());

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"{root} is not a directory");
            return ExitCodes.Error;
        }

        try
        {
            using var session = WorkspaceSession.Open(root, new TreeSitterAnalyzerFactory());

            // The stored selection survives across runs, so a workspace whose tree the GUI
            // narrowed stays narrowed here; a fresh workspace defaults to everything.
            var selected = session.LoadSelectedDirectories();

            // --quiet drops the progress chatter, never the summary: the summary is the
            // answer, and a run that reported nothing at all could not be checked.
            var progress = new ConsoleIndexProgress(silent: args.Switch("quiet"));
            var result = await session
                .IndexAsync(selected, progress, cancellationToken, full: args.Switch("full"))
                .ConfigureAwait(false);
            progress.FinishLine();

            if (result.Outcome.WasCancelled)
            {
                Console.Error.WriteLine("cancelled — the index keeps what was already written");
                return ExitCodes.Error;
            }

            var outcome = result.Outcome;

            // A run that found nothing to do used to print the parse tally anyway —
            // "0 parsed … 0 symbols" — which reads as an empty index rather than an
            // up-to-date one (flagged in rounds seven and eight before this fix). The
            // no-op says what it means; runs that parsed anything keep the full tally,
            // whose counts are visibly about this run.
            if (outcome.FilesParsed == 0 && outcome.FilesFailed == 0 && result.FilesRemoved == 0)
            {
                Console.WriteLine(
                    $"indexed {root}: all {outcome.FilesUnchanged} files up to date — "
                    + $"index holds {result.SymbolsStored:N0} definitions, {result.EdgesCreated:N0} links "
                    + $"(checked in {result.Elapsed.TotalSeconds:0.0}s)");
            }
            else
            {
                // Two numbers were both called "symbols" and differed: this line printed
                // what the parse yielded, and stats prints what the index holds as
                // definitions. The gap is the non-definition rows — 38 here (34 method
                // prototypes, 4 function declarations), 102 on a 1,000-file workspace —
                // and a reader comparing the two had no way to know that. Same defect
                // M25.3 fixed for "imports", and the same fix: say which is which, on the
                // surface where they meet.
                //
                // Both halves have to be worded, not just one. The first draft printed
                // "N symbols (M definitions)" and a test caught it immediately: SymbolsFound
                // counts only THIS run's parses while SymbolsStored is the whole index, so
                // on an incremental run the parenthesis compared unrelated numbers and would
                // have read as arithmetic. Naming each one's population makes it true on a
                // full run and a one-file refresh alike.
                Console.WriteLine(
                    $"indexed {root}: {outcome.FilesParsed} parsed, {outcome.FilesUnchanged} unchanged, "
                    + $"{outcome.FilesFailed} failed, {outcome.FilesWithSyntaxErrors} with syntax errors, "
                    + $"{result.FilesRemoved} removed · {outcome.SymbolsFound:N0} symbols parsed, "
                    + $"index holds {result.SymbolsStored:N0} definitions and "
                    + $"{result.EdgesCreated:N0} links, in {result.Elapsed.TotalSeconds:0.0}s");
            }

            // Not suppressed by --quiet: the summary is the answer, and if the index just
            // changed shape that is part of the answer, not chatter about how it got there.
            if (result.Cost is not null && IndexCostProbe.Describe(result.Cost) is { } note)
            {
                Console.WriteLine(note);
            }

            return ExitCodes.Ok;
        }
        catch (SqliteException e)
        {
            Console.Error.WriteLine(
                $"the index is locked or unreadable — is the GUI mid-index? ({e.Message})");
            return ExitCodes.IndexBusy;
        }
    }
}

/// <summary>
/// Progress on stderr: a rewritten status line on a console, phase-change lines when
/// redirected — a log file full of carriage returns helps nobody.
/// </summary>
internal sealed class ConsoleIndexProgress(bool silent = false) : IProgress<IndexProgress>
{
    private readonly bool _interactive = !Console.IsErrorRedirected;
    private IndexPhase _lastPhase = IndexPhase.Idle;
    private long _lastWriteTicks;
    private bool _wroteAnything;

    public void Report(IndexProgress value)
    {
        if (silent)
        {
            return;
        }

        var phaseChanged = value.Phase != _lastPhase;
        _lastPhase = value.Phase;

        // The pipeline already throttles to ~10 Hz; a console repaint every ~200 ms is
        // plenty, but a phase change always lands.
        var now = Environment.TickCount64;
        if (!phaseChanged && now - _lastWriteTicks < 200)
        {
            return;
        }

        _lastWriteTicks = now;

        var processed = value.FilesParsed + value.FilesUnchanged + value.FilesFailed;
        var slow = value.SlowFile is null
            ? string.Empty
            : $" — still on {Path.GetFileName(value.SlowFile)} ({value.SlowFileSeconds}s)";

        var line = $"{value.Phase.ToString().ToLowerInvariant()}: {processed}/{value.FilesDiscovered} files, "
            + $"{value.SymbolsFound:N0} symbols{slow}";

        if (_interactive)
        {
            Console.Error.Write($"\r{line,-100}");
            _wroteAnything = true;
        }
        else if (phaseChanged)
        {
            Console.Error.WriteLine(line);
        }
    }

    /// <summary>Ends the rewritten line so the summary starts on its own.</summary>
    public void FinishLine()
    {
        if (_interactive && _wroteAnything)
        {
            Console.Error.WriteLine();
        }
    }
}
