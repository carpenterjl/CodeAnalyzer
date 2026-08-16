using CodeAnalyzer.Cli.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeAnalyzer.Cli.Mcp;

/// <summary>
/// <c>codeanalyzer mcp [--root path]</c> — the stdio MCP server an agent client registers:
/// <code>claude mcp add codeanalyzer -- codeanalyzer mcp --root C:\path\to\workspace</code>
/// <para>
/// Stdout is the JSON-RPC wire and carries nothing else — no provenance header, and all
/// logging is forced to stderr. One stray printed line here corrupts the protocol.
/// </para>
/// </summary>
internal static class McpCommand
{
    public static CommandSpec Spec { get; } = new(
        "mcp",
        "mcp [--root path]",
        "run the MCP stdio server over the workspace's index (for AI agent clients)",
        Run);

    private static async Task<int> Run(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root"], []);
        if (args.Error is not null || args.Positionals.Count > 0)
        {
            Console.Error.WriteLine(args.Error ?? "usage: codeanalyzer " + Spec.Usage);
            return ExitCodes.Error;
        }

        var root = Path.GetFullPath(CommandEnvironment.ResolveRoot(args));
        using var holder = new McpSessionHolder(root);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        // Stdout is the wire either way; --quiet silences the stderr log as well, for a
        // client that shows a server's stderr to the user as if it were output.
        if (!args.Switch("quiet"))
        {
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        }

        builder.Services.AddSingleton(holder);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<CodeAnalyzerTools>();

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
        return ExitCodes.Ok;
    }
}
