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
        "index [path]",
        "build or refresh the workspace's index (defaults to the current directory)",
        Run);

    private static async Task<int> Run(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, [], []);
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

            var progress = new ConsoleIndexProgress();
            var result = await session.IndexAsync(selected, progress, cancellationToken)
                .ConfigureAwait(false);
            progress.FinishLine();

            if (result.Outcome.WasCancelled)
            {
                Console.Error.WriteLine("cancelled — the index keeps what was already written");
                return ExitCodes.Error;
            }

            var outcome = result.Outcome;
            Console.WriteLine(
                $"indexed {root}: {outcome.FilesParsed} parsed, {outcome.FilesUnchanged} unchanged, "
                + $"{outcome.FilesFailed} failed, {outcome.FilesWithSyntaxErrors} with syntax errors, "
                + $"{result.FilesRemoved} removed · {outcome.SymbolsFound:N0} symbols, "
                + $"{result.EdgesCreated:N0} links in {result.Elapsed.TotalSeconds:0.0}s");

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
internal sealed class ConsoleIndexProgress : IProgress<IndexProgress>
{
    private readonly bool _interactive = !Console.IsErrorRedirected;
    private IndexPhase _lastPhase = IndexPhase.Idle;
    private long _lastWriteTicks;
    private bool _wroteAnything;

    public void Report(IndexProgress value)
    {
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
