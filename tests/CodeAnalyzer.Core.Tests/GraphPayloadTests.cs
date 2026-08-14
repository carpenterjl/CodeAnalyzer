using System.Text.Json;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// Guards the projection from a graph fragment onto the wire format the renderer reads.
/// The wire names are part of the contract with wwwroot/graph.js, so they are asserted
/// literally: renaming a property here without renaming it there breaks the view silently.
/// </summary>
public class GraphPayloadTests
{
    private static GraphNode Node(
        long id,
        string name,
        SymbolKind kind = SymbolKind.Function,
        int callers = 0,
        int callees = 0,
        string? value = null) => new()
        {
            Id = id,
            Name = name,
            Kind = kind,
            RelativePath = "drivers/uart.c",
            Line = 10,
            Value = value,
            CallerCount = callers,
            CalleeCount = callees,
        };

    private static GraphEdge Edge(
        long source,
        long target,
        ReferenceKind kind = ReferenceKind.Call,
        EdgeConfidence confidence = EdgeConfidence.Unique,
        int candidates = 1) => new()
        {
            SourceId = source,
            TargetId = target,
            Kind = kind,
            Confidence = confidence,
            Line = 12,
            CandidateCount = candidates,
        };

    [Fact]
    public void FocusNodeIsMarkedAndOthersAreNot()
    {
        var payload = GraphPayloadBuilder.Build(new GraphFragment
        {
            FocusId = 1,
            Nodes = [Node(1, "uart_init"), Node(2, "uart_configure")],
        });

        Assert.True(payload.Nodes.Single(n => n.Id == "1").IsFocus);
        Assert.False(payload.Nodes.Single(n => n.Id == "2").IsFocus);
        Assert.Equal("1", payload.FocusId);
    }

    [Fact]
    public void NodesCarryTheStoredTotalsRatherThanFragmentRelativeCounts()
    {
        // The page subtracts what it draws, so the payload has to hand over the totals
        // untouched. Pre-subtracting here is what used to make expand buttons vanish
        // while neighbours were still hidden.
        var payload = GraphPayloadBuilder.Build(new GraphFragment
        {
            FocusId = 1,
            Nodes = [Node(1, "uart_init", callers: 7, callees: 4), Node(2, "main")],
            Edges = [Edge(2, 1)],
        });

        var focus = payload.Nodes.Single(n => n.Id == "1");
        Assert.Equal(7, focus.TotalCallers);
        Assert.Equal(4, focus.TotalCallees);
    }

    [Fact]
    public void ConfidenceIsCarriedAsBothATokenAndAnHonestLabel()
    {
        var payload = GraphPayloadBuilder.Build(new GraphFragment
        {
            FocusId = 1,
            Nodes = [Node(1, "a"), Node(2, "b"), Node(3, "c")],
            Edges =
            [
                Edge(1, 2, confidence: EdgeConfidence.Ambiguous, candidates: 4),
                Edge(1, 3, confidence: EdgeConfidence.Weak, candidates: 2),
            ],
        });

        var ambiguous = payload.Edges.Single(e => e.Target == "2");
        Assert.Equal("ambiguous", ambiguous.Confidence);
        Assert.Equal("one of several name matches", ambiguous.ConfidenceLabel);
        Assert.Equal(4, ambiguous.Candidates);

        var weak = payload.Edges.Single(e => e.Target == "3");
        Assert.Equal("weak", weak.Confidence);
        Assert.Equal("cross-language name match", weak.ConfidenceLabel);
    }

    [Fact]
    public void TwoSymbolsLinkedTwiceKeepDistinctEdgeIds()
    {
        // A call and a type use between the same pair are different facts, and the
        // renderer keys elements by id, so they must not collide.
        var payload = GraphPayloadBuilder.Build(new GraphFragment
        {
            FocusId = 1,
            Nodes = [Node(1, "a"), Node(2, "b")],
            Edges = [Edge(1, 2, ReferenceKind.Call), Edge(1, 2, ReferenceKind.TypeUse)],
        });

        Assert.Equal(2, payload.Edges.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void KindsMapOntoTheVisualFamiliesTheStylesheetDefines()
    {
        var payload = GraphPayloadBuilder.Build(new GraphFragment
        {
            Nodes =
            [
                Node(1, "f", SymbolKind.Function),
                Node(2, "S", SymbolKind.Struct),
                Node(3, "K", SymbolKind.Constant),
                Node(4, "M", SymbolKind.Macro),
                Node(5, "mod", SymbolKind.Module),
                Node(6, "v", SymbolKind.Variable),
            ],
        });

        Assert.Equal(
            ["function", "type", "constant", "macro", "module", "variable"],
            payload.Nodes.Select(n => n.Group));
    }

    [Fact]
    public void TruncationSurvivesTheProjection()
    {
        var payload = GraphPayloadBuilder.Build(new GraphFragment
        {
            FocusId = 1,
            Nodes = [Node(1, "a")],
            WasTruncated = true,
        });

        Assert.True(payload.Truncated);
    }

    [Fact]
    public void SerialisedNamesMatchWhatTheGraphPageReads()
    {
        var payload = GraphPayloadBuilder.Build(new GraphFragment
        {
            FocusId = 1,
            Nodes = [Node(1, "UART_BAUD", SymbolKind.Macro, callers: 3, value: "115200")],
            Edges = [],
        });

        var json = JsonSerializer.Serialize(payload, GraphPayloadBuilder.JsonOptions);
        using var document = JsonDocument.Parse(json);

        var node = document.RootElement.GetProperty("nodes")[0];
        Assert.Equal("UART_BAUD", node.GetProperty("name").GetString());
        Assert.Equal("macro", node.GetProperty("kind").GetString());
        Assert.Equal("macro", node.GetProperty("group").GetString());
        Assert.Equal("115200", node.GetProperty("value").GetString());
        Assert.Equal(3, node.GetProperty("totalCallers").GetInt32());
        Assert.True(node.GetProperty("isFocus").GetBoolean());

        // Ids cross as strings: JavaScript numbers cannot hold the full 64-bit range.
        Assert.Equal(JsonValueKind.String, node.GetProperty("id").ValueKind);
    }

    [Fact]
    public void NullValuesAreOmittedRatherThanSentAsNull()
    {
        var payload = GraphPayloadBuilder.Build(new GraphFragment
        {
            Nodes = [Node(1, "plain")],
        });

        var json = JsonSerializer.Serialize(payload, GraphPayloadBuilder.JsonOptions);
        Assert.DoesNotContain("\"value\"", json);
        Assert.DoesNotContain("\"signature\"", json);
    }
}
