using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Cli.Session;

namespace CodeAnalyzer.Cli.Commands;

/// <summary>
/// The shared open-query-report frame around every read command: resolve the workspace
/// root, open the cache read-only, print the provenance header to stderr, run the body,
/// and translate the two failure families into their exit codes.
/// </summary>
internal static class CommandEnvironment
{
    /// <summary>The workspace root a command operates on: <c>--root</c>, else the current directory.</summary>
    public static string ResolveRoot(ArgReader args) =>
        args.Value("root") ?? Directory.GetCurrentDirectory();

    public static async Task<int> WithSession(ArgReader args, Func<AgentToolset, Task<int>> body)
    {
        if (args.Error is not null)
        {
            Console.Error.WriteLine(args.Error);
            return ExitCodes.Error;
        }

        var opened = ReadOnlyIndexSession.TryOpen(ResolveRoot(args));
        if (opened.Session is null)
        {
            Console.Error.WriteLine(opened.Problem);
            return opened.Status == IndexOpenStatus.NoCache ? ExitCodes.NoIndex : ExitCodes.Error;
        }

        using var session = opened.Session;

        // Provenance on stderr: humans see it, pipes reading stdout do not.
        Console.Error.WriteLine("# " + session.DescribeIndex());

        try
        {
            return await body(new AgentToolset(session)).ConfigureAwait(false);
        }
        catch (IndexUnavailableException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine("retry shortly; if this persists, run: codeanalyzer index");
            return ExitCodes.IndexBusy;
        }
    }
}
