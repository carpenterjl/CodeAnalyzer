using CodeAnalyzer.Cli.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

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

        WrapToolsSoBadArgumentsAreExplained(builder.Services);

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Replaces every registered tool with an <see cref="ExplainingTool"/> around it.
    /// <para>
    /// Argument binding happens inside the SDK, before any of this project's code runs, so
    /// there is nowhere in <see cref="CodeAnalyzerTools"/> to catch a bad argument list —
    /// which is why the one place that already tries (<c>Reindex</c>'s catch-all) cannot
    /// help. Wrapping is the SDK's own suggested extension point:
    /// <c>DelegatingMcpServerTool</c> exists to be chained around a tool, and doing it here
    /// keeps the tool surface itself a list of plain methods.
    /// </para>
    /// <para>
    /// The registration is re-pointed rather than rebuilt: whatever <c>WithTools</c> did to
    /// generate schemas and resolve services is preserved exactly, and only the invocation
    /// is intercepted.
    /// </para>
    /// </summary>
    private static void WrapToolsSoBadArgumentsAreExplained(IServiceCollection services)
    {
        foreach (var registration in services.Where(s => s.ServiceType == typeof(McpServerTool)).ToList())
        {
            services.Remove(registration);
            services.Add(ServiceDescriptor.Singleton<McpServerTool>(
                provider => new ExplainingTool(Build(registration, provider))));
        }

        static McpServerTool Build(ServiceDescriptor registration, IServiceProvider provider) =>
            registration switch
            {
                { ImplementationInstance: McpServerTool tool } => tool,
                { ImplementationFactory: { } factory } => (McpServerTool)factory(provider),
                { ImplementationType: { } type } =>
                    (McpServerTool)ActivatorUtilities.CreateInstance(provider, type),
                _ => throw new InvalidOperationException(
                    "an McpServerTool was registered in a form this wrapper does not know how to build"),
            };
    }
}
