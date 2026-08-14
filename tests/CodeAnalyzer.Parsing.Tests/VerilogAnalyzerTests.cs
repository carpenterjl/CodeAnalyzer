using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Extraction checks for the Verilog / SystemVerilog query pack.
/// </summary>
public class VerilogAnalyzerTests() : LanguagePackFixture(LanguageRegistry.Verilog, "uart_tx.sv")
{
    private const string Source = """
        `include "defs.svh"
        `define WIDTH 8

        package pkg_types;
            parameter int MAX_BURST = 16;
            typedef enum logic [1:0] { IDLE, BUSY } state_e;
            typedef struct packed {
                logic [7:0] data;
                logic       valid;
            } frame_t;
        endpackage

        module uart_tx #(
            parameter int DATA_WIDTH = 8,
            parameter int DEPTH = 16
        ) (
            input  logic                  clk,
            input  logic [DATA_WIDTH-1:0] tx_data,
            output logic                  tx_busy
        );

            localparam int COUNT_MAX = DEPTH - 1;

            logic [3:0] counter;
            state_e     state;

            fifo #(.DEPTH(DEPTH)) u_fifo (
                .clk(clk)
            );

            function automatic int next_count(input int c);
                return c + 1;
            endfunction

            always_ff @(posedge clk) begin
                counter <= next_count(counter);
            end

        endmodule
        """;

    [Fact]
    public void ModulesPackagesAndTypedefsAreExtracted()
    {
        var result = Analyze(Source);

        Assert.Equal(SymbolKind.Module, Symbol(result, "uart_tx").Kind);
        Assert.Equal(SymbolKind.Namespace, Symbol(result, "pkg_types").Kind);
        Assert.Equal(SymbolKind.Typedef, Symbol(result, "state_e").Kind);
        Assert.Equal(SymbolKind.Typedef, Symbol(result, "frame_t").Kind);
    }

    [Fact]
    public void PortsCarryTheirDirectionAndWidth()
    {
        var result = Analyze(Source);

        var data = Symbol(result, "tx_data");
        Assert.Equal(SymbolKind.Port, data.Kind);

        // Direction and packed width together are what a caller of this module needs.
        Assert.Equal("input  logic [DATA_WIDTH-1:0]", data.TypeText);
        Assert.StartsWith("output", Symbol(result, "tx_busy").TypeText);
    }

    [Fact]
    public void ParametersAndLocalparamsAreConstantsWithTheirValues()
    {
        var result = Analyze(Source);

        Assert.Equal(SymbolKind.Constant, Symbol(result, "DATA_WIDTH").Kind);
        Assert.Equal("8", Symbol(result, "DATA_WIDTH").Value);
        Assert.Equal("16", Symbol(result, "MAX_BURST").Value);

        var local = Symbol(result, "COUNT_MAX");
        Assert.Equal(SymbolKind.Constant, local.Kind);
        Assert.Equal("DEPTH - 1", local.Value);
    }

    [Fact]
    public void MacroDefinitionsKeepTheirReplacementText()
    {
        var result = Analyze(Source);

        var width = Symbol(result, "WIDTH");
        Assert.Equal(SymbolKind.Macro, width.Kind);
        Assert.Equal("8", width.Value);
    }

    [Fact]
    public void PackedStructMembersAndEnumNamesAreExtracted()
    {
        var result = Analyze(Source);

        Assert.Equal(SymbolKind.Field, Symbol(result, "data").Kind);
        Assert.Equal("logic [7:0]", Symbol(result, "data").TypeText);
        Assert.Equal(SymbolKind.EnumMember, Symbol(result, "IDLE").Kind);
    }

    [Fact]
    public void SignalsAndInstancesLiveInsideTheirModule()
    {
        var result = Analyze(Source);

        var module = IndexOf(result, "uart_tx", SymbolKind.Module);
        var members = MembersOf(result, "uart_tx");

        Assert.True(module >= 0);
        Assert.Contains("counter", members);
        Assert.Contains("state", members);
        Assert.Contains("u_fifo", members);
        Assert.Contains("next_count", members);
    }

    [Fact]
    public void InstantiationIsRecordedAsAnInstantiateReference()
    {
        var result = Analyze(Source);

        var instantiation = Assert.Single(result.References, r => r.Kind == ReferenceKind.Instantiate);
        Assert.Equal("fifo", instantiation.Name);

        // Module-level constructs belong to the module, which is what makes an
        // instantiation hierarchy possible.
        Assert.Equal(IndexOf(result, "uart_tx", SymbolKind.Module), instantiation.FromSymbolLocalIndex);
    }

    [Fact]
    public void IncludesAreRecordedWithQuotesStripped()
    {
        var result = Analyze(Source);

        Assert.Equal(new[] { "defs.svh" }, ReferenceNames(result, ReferenceKind.Include));
    }

    [Fact]
    public void FunctionCallsInsideAnAlwaysBlockBelongToTheModule()
    {
        var result = Analyze(Source);

        var call = Assert.Single(result.References, r => r is { Kind: ReferenceKind.Call, Name: "next_count" });

        // An always block is not a symbol, so the innermost enclosing one is the module.
        Assert.Equal(IndexOf(result, "uart_tx", SymbolKind.Module), call.FromSymbolLocalIndex);
    }

    [Fact]
    public void ABareSubroutineCallStatementIsDroppedRatherThanInventingAVariable()
    {
        // Known limitation of the bundled grammar: it reads `load(1);` as a statement as a
        // declaration followed by an error. Nothing can be recovered from that, but the
        // analyzer must not turn it into a variable named after the task.
        var result = Analyze("""
            module m;
              logic bus;
              always @(*) begin
                load(1);
              end
              task load(input int v);
                bus <= v;
              endtask
            endmodule
            """);

        Assert.Equal(FileStatus.ParseError, result.Status);

        var load = Assert.Single(result.Symbols, s => s.Name == "load");
        Assert.Equal(SymbolKind.Function, load.Kind);

        // The task itself parsed fine and is still there; only the call site is lost.
        Assert.DoesNotContain(result.Symbols, s => s is { Name: "load", Kind: SymbolKind.Variable });
    }
}
