using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Cli.Session;

namespace CodeAnalyzer.Cli.Commands;

/// <summary>
/// The shared open-query-report frame around every read command: resolve the workspace
/// root, open the cache read-only, print the provenance header, run the body, and
/// translate the two failure families into their exit codes.
/// </summary>
internal static class CommandEnvironment
{
    /// <summary>The workspace root a command operates on: <c>--root</c>, else the current directory.</summary>
    public static string ResolveRoot(ArgReader args) =>
        args.Value("root") ?? Directory.GetCurrentDirectory();

    /// <summary>
    /// Which stream the provenance header goes to, or null for none.
    /// <para>
    /// It used to be stderr unconditionally, on the sound reasoning that a pipe reading
    /// stdout must receive only the answer. The flaw showed up the moment the tool was
    /// driven from PowerShell, which renders anything on stderr as an error record: every
    /// successful query came back looking like it had failed. A header is not a failure,
    /// and a tool whose whole argument is that it never overstates what it knows cannot
    /// spend its first line overstating that something went wrong.
    /// </para>
    /// <para>
    /// So the rule follows the destination rather than a fixed choice: stdout when stdout
    /// is a terminal, where the header is simply the first line a human reads; stderr when
    /// stdout is redirected, which is the case the original reasoning was actually about;
    /// and nothing at all under <c>--quiet</c>. A machine document (<c>--json</c>) counts
    /// as redirected even from a terminal — it is written to be parsed, and a header above
    /// it would be a syntax error in whatever reads it next.
    /// </para>
    /// </summary>
    public static TextWriter? ProvenanceStream(ArgReader args) =>
        ProvenanceStream(args, Console.IsOutputRedirected);

    /// <summary>
    /// The decision itself, with its one environmental input passed in — a test host's
    /// stdout is always redirected, so the terminal branch could not otherwise be proven
    /// by anything but running it by hand and looking.
    /// </summary>
    public static TextWriter? ProvenanceStream(ArgReader args, bool stdoutRedirected)
    {
        if (args.Switch("quiet"))
        {
            return null;
        }

        return stdoutRedirected || args.Switch("json") ? Console.Error : Console.Out;
    }

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

        ProvenanceStream(args)?.WriteLine("# " + session.DescribeIndex());

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
