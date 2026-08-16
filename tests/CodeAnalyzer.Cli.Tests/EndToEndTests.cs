using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// The only tests that spawn the real process: one proves the CLI round trip and JSON
/// contract, one proves the MCP stdio handshake — which is the only way to prove stdout
/// carries nothing but JSON-RPC.
/// </summary>
[Collection("cli-workspace")]
[Trait("Category", "EndToEnd")]
public class EndToEndTests(CliWorkspaceFixture fixture)
{
    /// <summary>The exe rides along via the project reference into the test output.</summary>
    private static string CliDllPath => Path.Combine(AppContext.BaseDirectory, "codeanalyzer.dll");

    [Fact]
    public async Task SearchJsonRoundTripsThroughTheRealProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(CliDllPath);
        psi.ArgumentList.Add("search");
        psi.ArgumentList.Add("uart_init");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(fixture.Root);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, process.ExitCode);

        using var document = JsonDocument.Parse(stdout);
        var hits = document.RootElement.GetProperty("hits");
        Assert.True(hits.GetArrayLength() >= 1);
        Assert.Equal("uart_init", hits[0].GetProperty("name").GetString());
        Assert.Equal("drivers/uart.c", hits[0].GetProperty("path").GetString());

        // Provenance travels inside the JSON too, not only on stderr.
        Assert.Equal(fixture.Root, document.RootElement.GetProperty("index").GetProperty("root").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APipedRunKeepsTheHeaderOffStdout(bool quiet)
    {
        // A spawned process always has stdout redirected, which is exactly the case the
        // stderr rule exists for: whatever else changes, the pipe gets only the answer.
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(CliDllPath);
        psi.ArgumentList.Add("search");
        psi.ArgumentList.Add("uart_init");
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(fixture.Root);
        if (quiet)
        {
            psi.ArgumentList.Add("--quiet");
        }

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(60));
        var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("uart_init", stdout);
        Assert.DoesNotContain("# index:", stdout);

        // Quiet removes it altogether; without it, it is still there to be read.
        if (quiet)
        {
            Assert.DoesNotContain("# index:", stderr);
        }
        else
        {
            Assert.Contains("# index:", stderr);
        }
    }

    [Fact]
    public async Task TheMcpHandshakeAnswersOnAPureStdout()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(CliDllPath);
        psi.ArgumentList.Add("mcp");
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(fixture.Root);

        using var process = Process.Start(psi)!;
        try
        {
            var timeout = TimeSpan.FromSeconds(60);

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");
            await process.StandardInput.FlushAsync();

            var initialize = await process.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            Assert.NotNull(initialize);

            // Anything that is not JSON-RPC on this stream corrupts the protocol; parsing
            // the line IS the purity assertion.
            using (var frame = JsonDocument.Parse(initialize))
            {
                Assert.Equal(1, frame.RootElement.GetProperty("id").GetInt32());
                Assert.Equal("codeanalyzer",
                    frame.RootElement.GetProperty("result").GetProperty("serverInfo")
                        .GetProperty("name").GetString());
            }

            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            await process.StandardInput.FlushAsync();

            var toolsList = await process.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            Assert.NotNull(toolsList);
            Assert.Contains("search_symbols", toolsList);
            Assert.Contains("io_boundaries", toolsList);
            Assert.Contains("reindex", toolsList);

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_callers","arguments":{"symbol":"uart_write"}}}""");
            await process.StandardInput.FlushAsync();

            var call = await process.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            Assert.NotNull(call);
            Assert.Contains("main", call);
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }
    }
}
