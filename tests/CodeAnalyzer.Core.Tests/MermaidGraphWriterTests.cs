using CodeAnalyzer.Core.Export;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// Pins the Mermaid text: shapes by visual family, doubt spelled out in dashed edge
/// labels, and escaping that keeps verbatim source from breaking or hijacking the
/// diagram. The writer is pure, so every case is a document in, a string out.
/// </summary>
public class MermaidGraphWriterTests
{
    private static ExportedGraphNode Node(
        string id, string name, string? group = "function", string? container = null,
        string? value = null, bool isFocus = false, ExportedIoBoundary? io = null) => new()
        {
            Id = id,
            Name = name,
            Group = group,
            Container = container,
            Value = value,
            IsFocus = isFocus,
            IoBoundary = io,
        };

    [Fact]
    public void HeaderSaysThisIsTheCanvasNotTheCodebase()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument());

        Assert.StartsWith("%% CodeAnalyzer export — the visible elements of the current graph", text);
        Assert.Contains("flowchart LR", text);
    }

    [Theory]
    [InlineData("function", "n0(\"uart_init\")")]
    [InlineData("type", "n0{{\"uart_init\"}}")]
    [InlineData("constant", "n0{\"uart_init\"}")]
    [InlineData("macro", "n0>\"uart_init\"]")]
    [InlineData("module", "n0[\"uart_init\"]")]
    [InlineData("variable", "n0([\"uart_init\"])")]
    public void ShapesFollowTheVisualFamily(string group, string expected)
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "uart_init", group)],
        });

        Assert.Contains(expected, text);
    }

    [Fact]
    public void AnIoStubIsTheClassicFlowchartParallelogramAndNamesItsDirectionSource()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes =
            [
                Node("io:1:HAL_UART_Transmit:2", "HAL_UART_Transmit", group: null,
                    io: new ExportedIoBoundary { Direction = "output", Source = "catalog: STM32 HAL" }),
            ],
        });

        Assert.Contains("n0[/\"HAL_UART_Transmit — output (catalog: STM32 HAL)\"/]", text);
    }

    [Fact]
    public void AConstantCarriesItsLiteralInTheLabel()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "CMD_READ", "constant", value: "0xA5")],
        });

        Assert.Contains("{\"CMD_READ = 0xA5\"}", text);
    }

    [Fact]
    public void ContainedSymbolsReadContainerDotName()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "Send", container: "Radio")],
        });

        Assert.Contains("(\"Radio.Send\")", text);
    }

    [Fact]
    public void AnExactEdgeIsSolidWithTheKindAlone()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "a"), Node("2", "b")],
            Edges =
            [
                new ExportedGraphEdge
                {
                    Source = "1", Target = "2", Kind = "call", Confidence = "unique",
                },
            ],
        });

        Assert.Contains("n0 -->|\"call\"| n1", text);
    }

    [Fact]
    public void AnAmbiguousEdgeIsDashedAndStatesItsCandidateCount()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "a"), Node("2", "b")],
            Edges =
            [
                new ExportedGraphEdge
                {
                    Source = "1", Target = "2", Kind = "call",
                    Confidence = "ambiguous", Candidates = 3,
                },
            ],
        });

        Assert.Contains("n0 -.->|\"call · one of 3 name matches\"| n1", text);
    }

    [Fact]
    public void AWeakEdgeIsDashedAndSaysCrossLanguage()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "a"), Node("2", "b")],
            Edges =
            [
                new ExportedGraphEdge
                {
                    Source = "1", Target = "2", Kind = "call", Confidence = "weak",
                },
            ],
        });

        Assert.Contains("n0 -.->|\"call · cross-language name match\"| n1", text);
    }

    [Fact]
    public void AnIoLinkHasNoConfidenceAndDrawsSolidWithoutALabel()
    {
        // The stub link is not a resolution; dashing it would read as doubt.
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes =
            [
                Node("1", "caller"),
                Node("io:1:x:2", "x", group: null, io: new ExportedIoBoundary()),
            ],
            Edges = [new ExportedGraphEdge { Source = "1", Target = "io:1:x:2" }],
        });

        Assert.Contains("n0 --> n1", text);
        Assert.DoesNotContain("-->|", text);
    }

    [Fact]
    public void ThePagesIoLinkKindIsNotRenderedAsALabel()
    {
        // What the page actually emits for a stub link: kind "io", confidence "unique".
        // The stub node states the direction and its source; the connector stays bare.
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes =
            [
                Node("1", "caller"),
                Node("io:1:x:2", "x", group: null, io: new ExportedIoBoundary()),
            ],
            Edges =
            [
                new ExportedGraphEdge
                {
                    Source = "1", Target = "io:1:x:2", Kind = "io", Confidence = "unique",
                },
            ],
        });

        Assert.Contains("n0 --> n1", text);
        Assert.DoesNotContain("\"io\"", text);
    }

    [Fact]
    public void SourceTextIsEscapedSoItCannotBreakOrHijackTheDiagram()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes =
            [
                Node("1", "operator<<"),
                Node("2", "CMD", "constant", value: "\"a#b`c|d\""),
            ],
        });

        Assert.Contains("operator#lt;#lt;", text);
        Assert.Contains("#quot;a#35;b#96;c#124;d#quot;", text);
        Assert.DoesNotContain("operator<<", text);
    }

    [Fact]
    public void TheFocusNodeIsMarkedWithAClass()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "a", isFocus: true), Node("2", "b")],
        });

        Assert.Contains("class n0 focus", text);
        Assert.Contains("classDef focus", text);
        Assert.DoesNotContain("class n1 focus", text);
    }

    [Fact]
    public void AnEdgeToANodeOutsideTheDocumentIsDroppedNotInvented()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "a")],
            Edges = [new ExportedGraphEdge { Source = "1", Target = "99", Kind = "call" }],
        });

        Assert.DoesNotContain("n99", text);
        Assert.DoesNotContain("-->", text);
    }

    [Fact]
    public void MultiLineParameterTextCollapsesToOneLabelLine()
    {
        var text = MermaidGraphWriter.Write(new ExportedGraphDocument
        {
            Nodes = [Node("1", "Configure", value: "a\n    + b")],
        });

        Assert.Contains("Configure = a + b", text);
    }
}
