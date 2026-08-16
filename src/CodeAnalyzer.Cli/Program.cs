using CodeAnalyzer.Cli.Commands;

namespace CodeAnalyzer.Cli;

/// <summary>
/// <c>codeanalyzer</c> — the headless interface over a workspace index: subcommands for
/// scripts and humans, and an MCP stdio server (<c>codeanalyzer mcp</c>) for AI agents.
/// <para>
/// Reads go against the same SQLite cache the GUI builds, through a read-only connection,
/// so the two can run side by side. Every answer carries the index's build time and the
/// same confidence vocabulary the GUI shows — a name match is labelled a name match here
/// too.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>One line per verb; the usage text is generated from this table so the two cannot drift.</summary>
    private static readonly IReadOnlyList<CommandSpec> Commands =
    [
        ReadCommands.Search,
        ReadCommands.Detail,
        ReadCommands.Report,
        ReadCommands.Callers,
        ReadCommands.Callees,
        ReadCommands.Trace,
        ReadCommands.Boundaries,
        ReadCommands.Value,
        ReadCommands.Constants,
        ReadCommands.Map,
        ReadCommands.Outline,
        ReadCommands.Errors,
        IndexCommand.Spec,
        Mcp.McpCommand.Spec,
    ];

    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (args.Length == 0)
        {
            PrintUsage(Console.Error);
            return ExitCodes.Error;
        }

        if (args[0] is "help" or "--help" or "-h")
        {
            PrintUsage(Console.Out);
            return ExitCodes.Ok;
        }

        var command = Commands.FirstOrDefault(
            c => string.Equals(c.Verb, args[0], StringComparison.OrdinalIgnoreCase));

        if (command is null)
        {
            Console.Error.WriteLine($"unknown command '{args[0]}'");
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return ExitCodes.Error;
        }

        try
        {
            return await command.Run(args.Skip(1).ToArray(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("cancelled");
            return ExitCodes.Error;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("codeanalyzer — facts from a CodeAnalyzer workspace index");
        writer.WriteLine();
        writer.WriteLine("usage: codeanalyzer <command> [arguments]");

        if (Commands.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("commands:");

            var width = Commands.Max(c => c.Usage.Length);
            foreach (var command in Commands)
            {
                writer.WriteLine($"  {command.Usage.PadRight(width)}  {command.Summary}");
            }
        }

        writer.WriteLine();
        writer.WriteLine("accepted by every command:");
        writer.WriteLine("  --quiet   print nothing but the answer (drops the index provenance header)");
        writer.WriteLine();
        writer.WriteLine("The provenance header goes to stdout at a terminal and to stderr when stdout is");
        writer.WriteLine("redirected or --json was asked for, so a pipe only ever receives the answer.");
    }
}

/// <summary>A subcommand: its verb, the usage line it prints, and what runs it.</summary>
internal sealed record CommandSpec(
    string Verb,
    string Usage,
    string Summary,
    Func<string[], CancellationToken, Task<int>> Run);
