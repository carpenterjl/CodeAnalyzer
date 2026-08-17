using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Extraction checks for the C# query pack.
/// </summary>
public class CSharpAnalyzerTests() : LanguagePackFixture(LanguageRegistry.CSharp, "Device.cs")
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using Alias = System.Text.StringBuilder;

        namespace Hardware.Drivers;

        public interface IDevice
        {
            int Send(byte[] payload);
        }

        public enum State
        {
            Idle = 0,
            Busy = 1,
        }

        public struct Point
        {
            public int X;
        }

        public class Device : Base, IDevice
        {
            public const int MaxDevices = 16;
            private static readonly string Prefix = "dev-";
            private readonly int _id;

            public Device(int id)
            {
                _id = id;
            }

            public string Name { get; init; } = "device";

            public int Send(byte[] payload)
            {
                var frame = new Frame(payload.Length);
                return Count(MaxDevices);
            }

            private static int Count(int limit) => limit;

            public delegate void Callback(int code);
        }
        """;

    [Fact]
    public void TypeDeclarationsKeepTheirDistinctKinds()
    {
        var result = Analyze(Source);

        Assert.Equal(SymbolKind.Namespace, Symbol(result, "Hardware.Drivers").Kind);
        Assert.Equal(SymbolKind.Interface, Symbol(result, "IDevice").Kind);
        Assert.Equal(SymbolKind.Enum, Symbol(result, "State").Kind);
        Assert.Equal(SymbolKind.Struct, Symbol(result, "Point").Kind);
        Assert.True(IndexOf(result, "Device", SymbolKind.Class) >= 0, "the class itself");
        Assert.Equal(SymbolKind.Typedef, Symbol(result, "Callback").Kind);

        // The constructor shares the class's name and must not be mistaken for it.
        Assert.Equal(SymbolKind.Method, result.Symbols.Single(s =>
            s.Name == "Device" && s.Kind != SymbolKind.Class).Kind);
    }

    [Fact]
    public void ClassMembersAreLinkedToTheirClass()
    {
        var result = Analyze(Source);

        Assert.Equal(
            new[] { "MaxDevices", "Prefix", "_id", "Device", "Name", "Send", "Count", "Callback" },
            MembersOf(result, "Device"));
    }

    [Fact]
    public void ConstFieldsBecomeConstantsCarryingTheirLiteral()
    {
        var result = Analyze(Source);

        // Three patterns match this field. The const one is the most specific claim, and
        // the literal is what the graph shows on the node.
        var max = Symbol(result, "MaxDevices");
        Assert.Equal(SymbolKind.Constant, max.Kind);
        Assert.Equal("16", max.Value);

        // A field that is merely readonly stays a field, but still keeps its initializer.
        var prefix = Symbol(result, "Prefix");
        Assert.Equal(SymbolKind.Field, prefix.Kind);
        Assert.Equal("\"dev-\"", prefix.Value);
    }

    [Fact]
    public void AnInterfaceMemberIsNotADefinitionButTheImplementationIs()
    {
        var result = Analyze(Source);

        var declarations = result.Symbols.Where(s => s.Name == "Send").ToList();
        Assert.Equal(2, declarations.Count);

        // Without this, a call to Send would resolve to two candidates and be reported
        // ambiguous even though only one of them has a body.
        var definition = Assert.Single(declarations, s => s.IsDefinition);
        Assert.Equal(SymbolKind.Method, definition.Kind);

        // The one with the body is the class's, not the interface's.
        Assert.Equal(
            IndexOf(result, "Device", SymbolKind.Class),
            definition.ContainerLocalIndex);
    }

    [Fact]
    public void ExpressionBodiedMembersCountAsDefinitions()
    {
        var result = Analyze(Source);

        var count = Symbol(result, "Count");
        Assert.Equal(SymbolKind.Method, count.Kind);
        Assert.True(count.IsDefinition, "`=> limit` is a body");
    }

    [Fact]
    public void PropertiesKeepTheirTypeAndInitialiser()
    {
        var result = Analyze(Source);

        var name = Symbol(result, "Name");
        Assert.Equal(SymbolKind.Property, name.Kind);
        Assert.Equal("string", name.TypeText);
        Assert.Equal("\"device\"", name.Value);
    }

    [Fact]
    public void ACollectionExpressionInitialiserDoesNotEraseTheDeclaration()
    {
        // The declaration around `= []` always survived — that was measured before the
        // grammar was replaced and it never was the problem. What changed in M28.3 is that
        // the file is no longer *reported* as imperfect for containing one, so a reader is
        // no longer invited to doubt counts drawn from it.
        var result = Analyze("""
            public class Holder
            {
                public IReadOnlyList<int> Values { get; init; } = [];

                public int[] Take() { return []; }
            }
            """);

        Assert.Equal(FileStatus.Ok, result.Status);

        var values = Symbol(result, "Values");
        Assert.Equal(SymbolKind.Property, values.Kind);
        Assert.Equal("IReadOnlyList<int>", values.TypeText);

        Assert.Equal(SymbolKind.Method, Symbol(result, "Take").Kind);
    }

    [Fact]
    public void PositionalRecordMembersAreExtracted()
    {
        var result = Analyze("public record struct Frame(int Length, byte[] Payload);");

        Assert.Equal(new[] { "Length", "Payload" }, MembersOf(result, "Frame"));
        Assert.Equal("int", Symbol(result, "Length").TypeText);
    }

    [Fact]
    public void BaseTypesAreRecordedAsInheritance()
    {
        var result = Analyze(Source);

        Assert.Equal(new[] { "Base", "IDevice" }, ReferenceNames(result, ReferenceKind.Inherit));
    }

    [Fact]
    public void InheritanceIsAttributedToTheDeclaringType()
    {
        var result = Analyze(Source);

        // A base list sits in the class declaration, not in any method, so under the
        // caller kinds it would have no owner — and the composition inspector could
        // never answer "what does Device inherit".
        var device = IndexOf(result, "Device", SymbolKind.Class);
        Assert.True(device >= 0);

        Assert.All(
            result.References.Where(r => r.Kind == ReferenceKind.Inherit),
            r => Assert.Equal(device, r.FromSymbolLocalIndex));
    }

    [Fact]
    public void ModifiersAreCapturedVerbatimInSourceOrder()
    {
        var result = Analyze(Source);

        var device = result.Symbols.Single(s => s is { Name: "Device", Kind: SymbolKind.Class });
        Assert.Equal("public", device.Modifiers);
        Assert.Equal("public", Symbol(result, "IDevice").Modifiers);
        Assert.Equal("public const", Symbol(result, "MaxDevices").Modifiers);
        Assert.Equal("private static readonly", Symbol(result, "Prefix").Modifiers);
        Assert.Equal("private static", Symbol(result, "Count").Modifiers);

        // A local has no modifiers, and the pack must not invent any.
        Assert.Null(Symbol(result, "frame").Modifiers);
    }

    [Fact]
    public void AnOverrideMethodCarriesTheOverrideModifier()
    {
        var result = Analyze("""
            public class Custom : Base
            {
                public override string ToString() => "custom";
                protected internal virtual void Tick() { }
            }
            """);

        Assert.Equal("public override", Symbol(result, "ToString").Modifiers);
        Assert.Equal("protected internal virtual", Symbol(result, "Tick").Modifiers);
    }

    [Fact]
    public void CallArgumentTextIsTheVerbatimSlice()
    {
        var result = Analyze(Source);

        var call = Assert.Single(result.References, r => r is { Kind: ReferenceKind.Call, Name: "Count" });
        Assert.Equal("(MaxDevices)", call.ArgumentText);

        // References without an argument list stay null rather than empty.
        var inherit = result.References.First(r => r.Kind == ReferenceKind.Inherit);
        Assert.Null(inherit.ArgumentText);
    }

    [Fact]
    public void LongArgumentListsAreTruncatedWithAVisibleMarker()
    {
        var longLiteral = new string('x', 400);
        var result = Analyze($$"""
            class C
            {
                void M() { Log("{{longLiteral}}"); }
            }
            """);

        var call = Assert.Single(result.References, r => r is { Kind: ReferenceKind.Call, Name: "Log" });
        Assert.NotNull(call.ArgumentText);
        Assert.Equal(201, call.ArgumentText!.Length);
        Assert.EndsWith("…", call.ArgumentText);
    }

    [Fact]
    public void UsingsAreImportsAndAnAliasIsADeclarationRatherThanAnImport()
    {
        var result = Analyze(Source);

        var imports = ReferenceNames(result, ReferenceKind.Import);
        Assert.Contains("System", imports);
        Assert.Contains("System.Collections.Generic", imports);
        Assert.Contains("System.Text.StringBuilder", imports);

        // `using Alias = …` introduces the name Alias, so it must not also read as an
        // import of a namespace called Alias.
        Assert.DoesNotContain("Alias", imports);
        Assert.Equal(SymbolKind.Typedef, Symbol(result, "Alias").Kind);
    }

    [Fact]
    public void CallsAreAttributedToTheEnclosingMethod()
    {
        var result = Analyze(Source);

        var send = result.Symbols.ToList().FindIndex(s => s is { Name: "Send", IsDefinition: true });
        var call = Assert.Single(result.References, r => r is { Kind: ReferenceKind.Call, Name: "Count" });

        Assert.Equal(send, call.FromSymbolLocalIndex);
        Assert.Equal(1, call.ArgumentCount);
    }

    [Fact]
    public void TypePositionsAreRecordedAsTypeReferencesNotBareUses()
    {
        var result = Analyze("""
            class Holder
            {
                private Frame _frame;
                public Frame Take(Frame other) => other;
            }
            """);

        // A bare identifier use resolves to values only, so without these the graph would
        // never link a field or a parameter to the type it names.
        var typeUses = ReferenceNames(result, ReferenceKind.TypeUse);
        Assert.Equal(3, typeUses.Count(n => n == "Frame"));
    }

    [Fact]
    public void LocalsAreLinkedToTheirMethod()
    {
        var result = Analyze(Source);

        var send = result.Symbols.ToList().FindIndex(s => s is { Name: "Send", IsDefinition: true });
        var frame = Symbol(result, "frame");

        Assert.Equal(send, frame.ContainerLocalIndex);
        Assert.Equal("new Frame(payload.Length)", frame.Value);
    }

    [Fact]
    public void ParameterListsAreStoredVerbatim()
    {
        var result = Analyze(Source);

        // Stored as its own slice rather than dug back out of the signature: the graph
        // draws it on the node to tell overloads apart, and finding the parameters inside
        // "int Send(byte[] payload)" would be parsing rather than reading a fact.
        Assert.Equal("(int limit)", Symbol(result, "Count").ParameterText);

        // The constructor shares its name with the class it declares, so ask by kind.
        var constructor = Assert.Single(
            result.Symbols, s => s is { Name: "Device", Kind: SymbolKind.Method });
        Assert.Equal("(int id)", constructor.ParameterText);
    }

    [Fact]
    public void ADeclarationWithNoParameterListSaysSoWithNull()
    {
        var result = Analyze(Source);

        // Null and "()" are different facts, and the resolver's arity filter reads both:
        // a field has no parameter list at all, whereas a method taking nothing has one.
        Assert.Null(Symbol(result, "MaxDevices").ParameterText);
        Assert.Null(Symbol(result, "MaxDevices").ParameterCount);
    }

    [Fact]
    public void ALongParameterListIsCutWithAVisibleEllipsis()
    {
        var longList = string.Join(", ", Enumerable.Range(0, 20).Select(i => $"int argument{i}"));
        var result = Analyze($"class Wide {{ public void Take({longList}) {{ }} }}");

        var text = Symbol(result, "Take").ParameterText;

        Assert.NotNull(text);

        // The ellipsis is what stops a cut slice reading as the whole parameter list.
        Assert.EndsWith("…", text);
        Assert.Equal(121, text.Length);
    }

    [Fact]
    public void TheRecordKeywordRidesInTheModifiers()
    {
        var result = Analyze("""
            public sealed record Frame(int Length);
            public record struct Point(int X);
            public sealed class Plain { }
            """);

        // A record indexes as a class — the kind vocabulary has no separate record — so
        // without the keyword nothing in the index says whether `with` and value equality
        // exist here. The keyword is verbatim source, which is why it can be stated at all.
        Assert.Equal(SymbolKind.Class, Symbol(result, "Frame").Kind);
        Assert.Equal("public sealed record", Symbol(result, "Frame").Modifiers);

        // `record struct` is a value type, and the second keyword is the only thing saying so.
        Assert.Equal("public record struct", Symbol(result, "Point").Modifiers);

        // And a plain class is unchanged: nothing was invented for it.
        Assert.Equal("public sealed", Symbol(result, "Plain").Modifiers);
    }

    [Fact]
    public void CallsRecordTheirReceiverVerbatimAndOnlyWhereOneWasWritten()
    {
        var result = Analyze("""
            public class Session
            {
                private Orchestrator orchestrator;

                public void Apply()
                {
                    orchestrator.Index(1);
                    this.Flush();
                    Reset();
                }
            }
            """);

        var calls = result.References.Where(r => r.Kind == ReferenceKind.Call).ToList();
        Assert.Equal("orchestrator", Assert.Single(calls, c => c.Name == "Index").ReceiverText);
        Assert.Equal("this", Assert.Single(calls, c => c.Name == "Flush").ReceiverText);
        Assert.Null(Assert.Single(calls, c => c.Name == "Reset").ReceiverText);
    }
}
