using System.Text.Json;
using CodeAnalyzer.Core.Export;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// Pins the parse of the page's export JSON — the M8 document shape, finally a type.
/// The page omits what a node does not have, so absent members must default quietly,
/// and members this type has not learned of yet must never fail the parse.
/// </summary>
public class ExportedGraphDocumentTests
{
    [Fact]
    public void ParsesTheFullDocumentShape()
    {
        var document = ExportedGraphDocument.Parse("""
            {
              "nodes": [
                {
                  "id": "412", "name": "uart_init", "kind": "function", "group": "function",
                  "path": "drivers/uart.c", "line": 9, "signature": "int uart_init(void)",
                  "params": "void", "modifiers": "static", "container": "Uart",
                  "isFocus": true
                },
                {
                  "id": "io:412:HAL_UART_Transmit:2", "name": "HAL_UART_Transmit",
                  "ioBoundary": { "direction": "output", "source": "catalog: STM32 HAL" },
                  "argText": "(&huart1, frame, 8, 100)"
                }
              ],
              "edges": [
                {
                  "source": "412", "target": "8", "kind": "call", "confidence": "ambiguous",
                  "line": 14, "candidates": 3, "callSites": 2
                },
                { "source": "412", "target": "io:412:HAL_UART_Transmit:2" }
              ]
            }
            """);

        var focus = document.Nodes[0];
        Assert.Equal("uart_init", focus.Name);
        Assert.Equal("function", focus.Group);
        Assert.True(focus.IsFocus);
        Assert.Null(focus.IoBoundary);

        var stub = document.Nodes[1];
        Assert.NotNull(stub.IoBoundary);
        Assert.Equal("output", stub.IoBoundary!.Direction);
        Assert.Null(stub.Path);

        var call = document.Edges[0];
        Assert.Equal("ambiguous", call.Confidence);
        Assert.Equal(3, call.Candidates);

        var ioLink = document.Edges[1];
        Assert.Null(ioLink.Kind);
        Assert.Null(ioLink.Confidence);
    }

    [Fact]
    public void AMemberThisTypeHasNotLearnedOfDoesNotFailTheParse()
    {
        var document = ExportedGraphDocument.Parse("""
            { "nodes": [{ "id": "1", "name": "a", "futureField": [1, 2] }], "edges": [] }
            """);

        Assert.Single(document.Nodes);
    }

    [Fact]
    public void MalformedJsonThrowsRatherThanReturningAnEmptyDocument()
    {
        Assert.ThrowsAny<JsonException>(() => ExportedGraphDocument.Parse("{ not json"));
    }
}
