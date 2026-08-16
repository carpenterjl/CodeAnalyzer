using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Extraction checks for the Python query pack.
/// </summary>
public class PythonAnalyzerTests() : LanguagePackFixture(LanguageRegistry.Python, "radio.py")
{
    private const string Source = """
        import os
        import os.path as osp
        from collections import OrderedDict
        from .device import Device, Frame

        MAX_DEVICES = 16
        PREFIX: str = "dev-"

        class Radio(Device, Loggable):
            CHANNELS = 32

            def __init__(self, channel: int) -> None:
                self.channel = channel
                self._state = 0

            def send(self, payload: bytes) -> int:
                frame = Frame(len(payload))
                return count(MAX_DEVICES)

            @staticmethod
            def reset():
                return 0

        def count(limit: int) -> int:
            total = limit
            return total
        """;

    [Fact]
    public void ADefInsideAClassIsAMethodAndOneAtFileScopeIsAFunction()
    {
        var result = Analyze(Source);

        // The same pattern matches both; nesting is what makes the difference, and it is
        // resolved by the more specific claim winning the position.
        Assert.Equal(SymbolKind.Method, Symbol(result, "send").Kind);
        Assert.Equal(SymbolKind.Function, Symbol(result, "count").Kind);
    }

    [Fact]
    public void ADecoratedMethodIsStillAMethod()
    {
        var result = Analyze(Source);

        Assert.Equal(SymbolKind.Method, Symbol(result, "reset").Kind);
    }

    [Fact]
    public void ScreamingCaseAssignmentsBecomeConstantsWithTheirLiteral()
    {
        var result = Analyze(Source);

        var max = Symbol(result, "MAX_DEVICES");
        Assert.Equal(SymbolKind.Constant, max.Kind);
        Assert.Equal("16", max.Value);

        // The annotated form has to keep both the type and the value.
        var prefix = Symbol(result, "PREFIX");
        Assert.Equal(SymbolKind.Constant, prefix.Kind);
        Assert.Equal("str", prefix.TypeText);
        Assert.Equal("\"dev-\"", prefix.Value);
    }

    [Fact]
    public void LowerCaseAssignmentsStayVariables()
    {
        var result = Analyze(Source);

        var total = Symbol(result, "total");
        Assert.Equal(SymbolKind.Variable, total.Kind);
        Assert.Equal("limit", total.Value);
    }

    [Fact]
    public void InstanceAttributesAreRecordedAsFields()
    {
        var result = Analyze(Source);

        // `self.channel = channel` is where a Python object's members are declared, so
        // this is the only place the composition of Radio can come from.
        var channel = Symbol(result, "channel");
        Assert.Equal(SymbolKind.Field, channel.Kind);
        Assert.Equal("channel", channel.Value);

        Assert.Equal(SymbolKind.Field, Symbol(result, "_state").Kind);
    }

    [Fact]
    public void MethodsAndClassAttributesHangOffTheClass()
    {
        var result = Analyze(Source);

        Assert.Equal(
            new[] { "CHANNELS", "__init__", "send", "reset" },
            MembersOf(result, "Radio"));
    }

    [Fact]
    public void BaseClassesAreRecordedAsInheritance()
    {
        var result = Analyze(Source);

        Assert.Equal(new[] { "Device", "Loggable" }, ReferenceNames(result, ReferenceKind.Inherit));
    }

    [Fact]
    public void InheritanceIsAttributedToTheDerivedClass()
    {
        var result = Analyze(Source);

        var radio = IndexOf(result, "Radio", SymbolKind.Class);
        Assert.True(radio >= 0);

        Assert.All(
            result.References.Where(r => r.Kind == ReferenceKind.Inherit),
            r => Assert.Equal(radio, r.FromSymbolLocalIndex));
    }

    [Fact]
    public void PythonSymbolsCarryNoModifiers()
    {
        // Python has no visibility or override syntax; underscore naming is a convention,
        // and reading a convention as a fact would be inference.
        var result = Analyze(Source);

        Assert.All(result.Symbols, s => Assert.Null(s.Modifiers));
    }

    [Fact]
    public void ImportsAreRecordedExactlyAsWrittenIncludingRelativeOnes()
    {
        var result = Analyze(Source);

        Assert.Equal(
            new[] { "os", "os.path", "collections", ".device" },
            ReferenceNames(result, ReferenceKind.Import));
    }

    [Fact]
    public void NamesBroughtInByAFromImportAreReferencesToThoseSymbols()
    {
        var result = Analyze(Source);

        // `from .device import Device, Frame` is how this file reaches those definitions.
        var uses = ReferenceNames(result, ReferenceKind.Use);
        Assert.Contains("OrderedDict", uses);
        Assert.Contains("Frame", uses);
    }

    [Fact]
    public void CallsAreAttributedToTheEnclosingMethod()
    {
        var result = Analyze(Source);

        var send = IndexOf(result, "send", SymbolKind.Method);
        var call = Assert.Single(result.References, r => r is { Kind: ReferenceKind.Call, Name: "count" });

        Assert.Equal(send, call.FromSymbolLocalIndex);
        Assert.Equal(1, call.ArgumentCount);
    }

    [Fact]
    public void AnnotationsAreTypeReferences()
    {
        var result = Analyze(Source);

        Assert.Contains("bytes", ReferenceNames(result, ReferenceKind.TypeUse));
    }

    [Fact]
    public void MethodCallsThroughAnAttributeRecordTheMethodName()
    {
        var result = Analyze("""
            def poll(radio):
                radio.send(b"")
            """);

        var call = Assert.Single(result.References, r => r.Kind == ReferenceKind.Call);
        Assert.Equal("send", call.Name);
        Assert.Equal("radio", call.ReceiverText);
    }

    [Fact]
    public void ABareCallCarriesNoReceiverAndASelfCallSaysSelf()
    {
        var result = Analyze("""
            class Radio:
                def send(self, data):
                    pass
                def flush(self):
                    self.send(b"")
                    helper()
            """);

        var calls = result.References.Where(r => r.Kind == ReferenceKind.Call).ToList();
        Assert.Equal("self", Assert.Single(calls, c => c.Name == "send").ReceiverText);
        Assert.Null(Assert.Single(calls, c => c.Name == "helper").ReceiverText);
    }
}
