using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Storage;
using CodeAnalyzer.Core.Workspaces;
using CodeAnalyzer.Parsing;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// One small indexed C workspace shared by every test class: index through the real
/// pipeline, dispose the writing session, and reopen through
/// <see cref="ReadOnlyIndexSession.TryOpen"/> — which also exercises the read-only WAL
/// open on a freshly closed database, the exact life a CLI invocation leads.
/// <para>
/// The layout bakes in the shapes the tests assert on: <c>init</c> defined in two files
/// (ambiguity), <c>uart_write</c> called from two distinct functions (fan-in ranking),
/// and a second <c>uart.c</c> base name (path disambiguation).
/// </para>
/// </summary>
public sealed class CliWorkspaceFixture : IDisposable
{
    public CliWorkspaceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "codeanalyzer-cli-tests-" + Guid.NewGuid().ToString("N")[..8]);

        Write("app/main.c", """
            int init(void) { return 1; }

            int main(void)
            {
                uart_init(9600);
                uart_write("hi");
                uart_write("lo");
                init();
                return 0;
            }
            """);

        Write("app/extra.c", """
            void extra(void)
            {
                uart_write("x");
            }
            """);

        Write("drivers/uart.c", """
            struct uart_config { int baud; };

            int uart_init(int baud) { return baud; }

            void uart_write(const char *s) { (void)s; }
            """);

        Write("drivers/init.c", """
            int init(void) { return 2; }
            """);

        Write("sim/uart.c", """
            int sim_uart_probe(void) { return 0; }
            """);

        using (var session = WorkspaceSession.Open(Root, new TreeSitterAnalyzerFactory()))
        {
            session.IndexAsync([]).GetAwaiter().GetResult();
        }

        var opened = ReadOnlyIndexSession.TryOpen(Root);
        Session = opened.Session
            ?? throw new InvalidOperationException($"fixture index did not reopen: {opened.Problem}");
    }

    public string Root { get; }

    // Internal because the type is: the fixture class must be public for xunit, but its
    // session is CLI-internal plumbing shared only within this assembly.
    internal ReadOnlyIndexSession Session { get; }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        Session.Dispose();

        try
        {
            Directory.Delete(WorkspacePaths.GetWorkspaceDirectory(Root), recursive: true);
        }
        catch (Exception e) when (e is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Cache cleanup failures are not test failures.
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }
}

[CollectionDefinition("cli-workspace")]
public sealed class CliWorkspaceCollection : ICollectionFixture<CliWorkspaceFixture>;
