using CodeAnalyzer.Core.Export;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// The flow writer's promises: hostile source text cannot break out of a label, doubt is
/// worded on the arrow that carries it, a value's way back is drawn only where it says
/// something, and recursion loops back to the occurrence it actually re-enters.
/// </summary>
public class MermaidFlowWriterTests
{
    private static ExportedFlowDocument Document(params ExportedFlowStep[] steps) => new()
    {
        Root = new ExportedFlowRoot
        {
            Id = "86", Name = "Run", Kind = "method", Path = "src/App.cs", Line = 12,
        },
        Steps = steps,
    };

    [Fact]
    public void HostileArgumentTextIsEscapedNotInterpreted()
    {
        var text = MermaidFlowWriter.Write(Document(new ExportedFlowStep
        {
            Ordinal = "1",
            Name = "Log",
            Args = "(\"a|b\" + $\"<{x}>\")",
            Confidence = "unique",
        }));

        Assert.DoesNotContain("(\"a|b", text);
        Assert.Contains("#124;", text); // the pipe
        Assert.Contains("#lt;", text);
        Assert.Contains("#quot;", text);
    }

    [Fact]
    public void DoubtIsWordedOnTheCallArrow()
    {
        var text = MermaidFlowWriter.Write(Document(
            new ExportedFlowStep
            {
                Ordinal = "1", Name = "CheckPaths", Confidence = "ambiguous", Candidates = 2,
            },
            new ExportedFlowStep
            {
                Ordinal = "2", Name = "uart_send", Confidence = "weak",
            },
            new ExportedFlowStep
            {
                Ordinal = "3", Name = "gone", Unresolved = true,
            }));

        Assert.Contains("root -.->|\"one of 3 name matches\"| s1", text);
        Assert.Contains("root -.->|\"cross-language name match\"| s2", text);
        Assert.Contains("root -.->|\"unresolved\"| s3", text);
    }

    [Fact]
    public void AValueGoesBackOnlyWhereItSaysSomething()
    {
        var text = MermaidFlowWriter.Write(Document(
            new ExportedFlowStep
            {
                Ordinal = "1", Name = "Load", Confidence = "unique",
                Fate = "assigned", FateName = "cfg",
            },
            new ExportedFlowStep
            {
                Ordinal = "2", Name = "Tick", Confidence = "unique", Fate = "discarded",
            }));

        Assert.Contains("s1 -.->|\"→ cfg\"| root", text);

        // A discarded result draws no return edge — the absence is the statement.
        Assert.DoesNotContain("s2 -.->", text);
    }

    [Fact]
    public void RecursionLoopsBackToTheOccurrenceItReEnters()
    {
        var text = MermaidFlowWriter.Write(Document(
            new ExportedFlowStep { Ordinal = "1", Name = "Alpha", Confidence = "unique" },
            new ExportedFlowStep
            {
                Ordinal = "1.1", Name = "Alpha", Confidence = "unique",
                Cycle = true, CycleOf = "1",
            }));

        Assert.Contains("s1_1 -.->|\"recursion\"| s1", text);
    }

    [Fact]
    public void IoStepsAreParallelogramsAndCutsAreTerminals()
    {
        var text = MermaidFlowWriter.Write(Document(
            new ExportedFlowStep
            {
                Ordinal = "1", Name = "WriteLine", Confidence = "unique",
                IoDirection = "output", IoFamily = ".NET Console",
            },
            new ExportedFlowStep
            {
                Ordinal = "2", Name = "Deep", Confidence = "unique",
                Truncated = true, CallSites = 3,
            }));

        Assert.Contains("s1[/\"", text);
        Assert.Contains("output — .NET Console", text);
        Assert.Contains("s2_cut([\"", text);
        Assert.Contains("3 call(s) not expanded", text);
        Assert.Contains("s2 -.-> s2_cut", text);
    }

    [Fact]
    public void ACollapsedStepPointsAtTheDrawing()
    {
        var text = MermaidFlowWriter.Write(Document(
            new ExportedFlowStep { Ordinal = "1", Name = "Parse", Confidence = "unique" },
            new ExportedFlowStep
            {
                Ordinal = "2", Name = "Parse", Confidence = "unique", CollapsedAt = "1",
            }));

        Assert.Contains("s2 -.->|\"= subtree at 1\"| s1", text);
    }
}
