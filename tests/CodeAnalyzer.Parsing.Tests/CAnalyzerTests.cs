using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// End-to-end extraction checks for the C query pack: the facts the UI promises to show
/// must actually come out of real source.
/// </summary>
public class CAnalyzerTests : IDisposable
{
    private readonly TreeSitterAnalyzer _analyzer =
        new(LanguageRegistry.ForName(LanguageRegistry.C)!);

    public void Dispose() => _analyzer.Dispose();

    private ParseResult Analyze(string source) =>
        _analyzer.Analyze("test.c", source, CancellationToken.None);

    private const string Driver = """
        #include <stdio.h>
        #include "uart.h"

        #define BUFFER_SIZE 256
        #define MAX(a, b) ((a) > (b) ? (a) : (b))

        typedef unsigned int uint;

        enum Status {
            STATUS_OK = 0,
            STATUS_BUSY = 1
        };

        struct Ring {
            char data[BUFFER_SIZE];
            int head;
            int tail;
        };

        static int ring_count(struct Ring *r) {
            return r->head - r->tail;
        }

        int uart_write(struct Ring *r, char c) {
            if (ring_count(r) >= BUFFER_SIZE) {
                return STATUS_BUSY;
            }
            r->data[r->head] = c;
            return STATUS_OK;
        }
        """;

    [Fact]
    public void ExtractsFunctionsWithSignatureAndArity()
    {
        var result = Analyze(Driver);

        var write = Assert.Single(result.Symbols, s => s.Name == "uart_write");
        Assert.Equal(SymbolKind.Function, write.Kind);
        Assert.True(write.IsDefinition);
        Assert.Equal(2, write.ParameterCount);
        Assert.Contains("uart_write", write.Signature);

        // Lines are 1-based: uart_write is the 24th line of the fixture.
        Assert.Equal(24, write.Span.Start.Line);
    }

    [Fact]
    public void ExtractsMacroConstantsWithTheirLiteralValue()
    {
        var result = Analyze(Driver);

        var bufferSize = Assert.Single(result.Symbols, s => s.Name == "BUFFER_SIZE");
        Assert.Equal(SymbolKind.Macro, bufferSize.Kind);

        // The literal value is a fact the detail pane shows verbatim.
        Assert.Equal("256", bufferSize.Value);
    }

    [Fact]
    public void ExtractsStructMembersLinkedToTheirStruct()
    {
        var result = Analyze(Driver);
        var symbols = result.Symbols.ToList();

        var ringIndex = symbols.FindIndex(s => s is { Name: "Ring", Kind: SymbolKind.Struct });
        Assert.True(ringIndex >= 0);

        var members = symbols
            .Where(s => s.Kind == SymbolKind.Field && s.ContainerLocalIndex == ringIndex)
            .Select(s => s.Name)
            .ToList();

        Assert.Equal(new[] { "data", "head", "tail" }, members);
    }

    [Fact]
    public void ExtractsEnumMembersWithValues()
    {
        var result = Analyze(Driver);

        var ok = Assert.Single(result.Symbols, s => s.Name == "STATUS_OK");
        Assert.Equal(SymbolKind.EnumMember, ok.Kind);
        Assert.Equal("0", ok.Value);
    }

    [Fact]
    public void ExtractsTypedef()
    {
        var result = Analyze(Driver);

        var alias = Assert.Single(result.Symbols, s => s.Name == "uint");
        Assert.Equal(SymbolKind.Typedef, alias.Kind);
    }

    [Fact]
    public void RecordsCallsAttributedToTheEnclosingFunction()
    {
        var result = Analyze(Driver);
        var symbols = result.Symbols.ToList();

        var writeIndex = symbols.FindIndex(s => s is { Name: "uart_write", Kind: SymbolKind.Function });

        var call = Assert.Single(
            result.References,
            r => r.Kind == ReferenceKind.Call && r.Name == "ring_count");

        // The caller is what makes a caller/callee edge possible.
        Assert.Equal(writeIndex, call.FromSymbolLocalIndex);
        Assert.Equal(1, call.ArgumentCount);
    }

    [Fact]
    public void RecordsIncludesWithDelimitersStripped()
    {
        var result = Analyze(Driver);

        var includes = result.References
            .Where(r => r.Kind == ReferenceKind.Include)
            .Select(r => r.Name)
            .ToList();

        Assert.Equal(new[] { "stdio.h", "uart.h" }, includes);
    }

    [Fact]
    public void RecordsConstantUsesSoDependenciesOnMacrosAreVisible()
    {
        var result = Analyze(Driver);

        // BUFFER_SIZE is used inside both the struct declaration and uart_write.
        var uses = result.References.Where(r => r.Name == "BUFFER_SIZE").ToList();
        Assert.NotEmpty(uses);
    }

    [Fact]
    public void DeclaringASymbolIsNotAReferenceToIt()
    {
        var result = Analyze("int counter = 0;\n");

        // The declaration of `counter` must not also appear as a use of `counter`.
        Assert.Single(result.Symbols, s => s.Name == "counter");
        Assert.DoesNotContain(result.References, r => r.Name == "counter");
    }

    [Fact]
    public void CallSitesAreRecordedAsCallsNotBareUses()
    {
        var result = Analyze("""
            int helper(void) { return 1; }
            int caller(void) { return helper(); }
            """);

        var reference = Assert.Single(result.References, r => r.Name == "helper");

        // Two patterns match a call site; the more specific kind must win.
        Assert.Equal(ReferenceKind.Call, reference.Kind);
    }

    [Fact]
    public void PrototypesAreNotResolutionTargets()
    {
        var result = Analyze("""
            int later(int x);
            int later(int x) { return x; }
            """);

        var declarations = result.Symbols.Where(s => s.Name == "later").ToList();
        Assert.Equal(2, declarations.Count);
        Assert.Contains(declarations, s => !s.IsDefinition);
        Assert.Contains(declarations, s => s.IsDefinition);
    }

    [Fact]
    public void BrokenSourceStillYieldsSymbolsAndIsFlagged()
    {
        var result = Analyze("""
            int good(void) { return 1; }
            int broken(void) { return
            """);

        Assert.Equal(FileStatus.ParseError, result.Status);
        Assert.Contains(result.Symbols, s => s.Name == "good");
    }

    [Fact]
    public void LocalVariablesAreLinkedToTheirEnclosingFunction()
    {
        var result = Analyze("""
            int compute(void) {
                int local = 5;
                return local;
            }
            """);

        var symbols = result.Symbols.ToList();
        var computeIndex = symbols.FindIndex(s => s is { Name: "compute", Kind: SymbolKind.Function });

        var local = Assert.Single(symbols, s => s.Name == "local");

        // Container linkage is what lets search hide function-local noise.
        Assert.Equal(computeIndex, local.ContainerLocalIndex);
        Assert.Equal("5", local.Value);
    }
}
