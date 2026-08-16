using CodeAnalyzer.Cli.Commands;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// Where the index provenance header goes. It sat on stderr unconditionally until a
/// PowerShell session showed what that costs: the shell renders stderr as an error
/// record, so every successful query came back painted as a failure.
/// </summary>
public class ProvenanceStreamTests
{
    private static ArgReader Args(params string[] tokens) =>
        ArgReader.Parse(tokens, ["root"], ["json"]);

    [Fact]
    public void AtATerminalItGoesToStdoutWhereItIsNotMistakenForAnError()
    {
        Assert.Same(Console.Out, CommandEnvironment.ProvenanceStream(Args(), stdoutRedirected: false));
    }

    [Fact]
    public void RedirectedItGoesToStderrSoThePipeOnlyGetsTheAnswer()
    {
        Assert.Same(Console.Error, CommandEnvironment.ProvenanceStream(Args(), stdoutRedirected: true));
    }

    [Fact]
    public void AMachineDocumentKeepsItOffStdoutEvenAtATerminal()
    {
        // --json is a redirect in intent: the output is written to be parsed, and a
        // header above it is a syntax error to whatever reads it next.
        Assert.Same(
            Console.Error,
            CommandEnvironment.ProvenanceStream(Args("--json"), stdoutRedirected: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QuietPrintsItNowhere(bool redirected)
    {
        Assert.Null(CommandEnvironment.ProvenanceStream(Args("--quiet"), redirected));
    }

    [Fact]
    public void QuietIsAcceptedByACommandThatNeverDeclaredIt()
    {
        // The global switch list is what stops "--quiet" being an unknown-option error on
        // the one command whose own list nobody remembered to update.
        var args = ArgReader.Parse(["--quiet"], [], []);

        Assert.Null(args.Error);
        Assert.True(args.Switch("quiet"));
    }

    [Fact]
    public void AnUnknownOptionIsStillAnError()
    {
        var args = ArgReader.Parse(["--qiuet"], [], []);

        Assert.Equal("unknown option --qiuet", args.Error);
    }
}
