using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Extraction checks for the C++ query pack: the facts the UI promises to show must
/// actually come out of real source.
/// </summary>
public class CppAnalyzerTests() : LanguagePackFixture(LanguageRegistry.Cpp, "device.cpp")
{
    private const string Source = """
        #include <vector>
        #include "device.hpp"

        namespace hw {

        constexpr int kMaxDevices = 16;

        enum class State { Idle = 0, Busy = 1 };

        struct Frame {
            int length;
            char *payload;
        };

        class Device : public Base, private Loggable {
        public:
            explicit Device(int id);
            int Send(const Frame &frame);
            int Id() const { return id_; }

        private:
            int id_;
            State state_ = State::Idle;
        };

        template <typename T>
        class Buffer {
        public:
            T *data;
        };

        using DeviceList = std::vector<Device>;

        int Device::Send(const Frame &frame) {
            if (frame.length > kMaxDevices) {
                return -1;
            }
            return Log(frame.length);
        }

        }  // namespace hw
        """;

    [Fact]
    public void ClassesAndTheirMembersAreLinked()
    {
        var result = Analyze(Source);

        Assert.True(IndexOf(result, "Device", SymbolKind.Class) >= 0, "the class itself");

        // Data members and member functions alike hang off the class, the constructor
        // among them.
        Assert.Equal(new[] { "Device", "Send", "Id", "id_", "state_" }, MembersOf(result, "Device"));

        // The constructor is a member function, not a free function that happens to share
        // the class's name.
        Assert.Equal(SymbolKind.Method, result.Symbols.Single(s =>
            s.Name == "Device" && s.Kind != SymbolKind.Class).Kind);
    }

    [Fact]
    public void AMemberDeclaredInTheClassIsNotADefinitionButTheOutOfLineBodyIs()
    {
        var result = Analyze(Source);

        var declarations = result.Symbols.Where(s => s.Name == "Send").ToList();
        Assert.Equal(2, declarations.Count);

        // Exactly one of the two is the definition, so a call to Send resolves to the body
        // rather than becoming an ambiguous pair.
        var definition = Assert.Single(declarations, s => s.IsDefinition);
        Assert.Equal(SymbolKind.Method, definition.Kind);
        Assert.Contains("Device::Send", Source[definition.Span.StartOffset..definition.Span.EndOffset]);
    }

    [Fact]
    public void AnInlineMemberBodyIsItsOwnDefinition()
    {
        var result = Analyze(Source);

        var id = Symbol(result, "Id");
        Assert.Equal(SymbolKind.Method, id.Kind);
        Assert.True(id.IsDefinition, "a member function with a body in the class is the definition");
    }

    [Fact]
    public void ConstexprValuesAreConstantsCarryingTheirLiteral()
    {
        var result = Analyze(Source);

        // Two patterns match this declaration; the constant is the more specific claim.
        var max = Symbol(result, "kMaxDevices");
        Assert.Equal(SymbolKind.Constant, max.Kind);
        Assert.Equal("16", max.Value);
    }

    [Fact]
    public void FieldInitialisersSurviveTheBareFieldPattern()
    {
        var result = Analyze(Source);

        // A field matches both with and without its default; the one that read the value
        // has to win, or the detail pane loses the fact.
        var state = Symbol(result, "state_");
        Assert.Equal(SymbolKind.Field, state.Kind);
        Assert.Equal("State", state.TypeText);
        Assert.Equal("State::Idle", state.Value);
    }

    [Fact]
    public void EnumClassMembersKeepTheirValues()
    {
        var result = Analyze(Source);

        Assert.Equal(SymbolKind.Enum, Symbol(result, "State").Kind);
        Assert.Equal("0", Symbol(result, "Idle").Value);
        Assert.Equal("1", Symbol(result, "Busy").Value);
    }

    [Fact]
    public void NamespacesAndTemplatesAndAliasesAreRecorded()
    {
        var result = Analyze(Source);

        Assert.Equal(SymbolKind.Namespace, Symbol(result, "hw").Kind);

        // A template class is wrapped in template_declaration; the class must still surface.
        Assert.Equal(SymbolKind.Class, Symbol(result, "Buffer").Kind);

        var alias = Symbol(result, "DeviceList");
        Assert.Equal(SymbolKind.Typedef, alias.Kind);
        Assert.Equal("std::vector<Device>", alias.TypeText);
    }

    [Fact]
    public void BaseClassesAreRecordedAsInheritance()
    {
        var result = Analyze(Source);

        Assert.Equal(new[] { "Base", "Loggable" }, ReferenceNames(result, ReferenceKind.Inherit));
    }

    [Fact]
    public void InheritanceIsAttributedToTheDerivedClass()
    {
        var result = Analyze(Source);

        var device = IndexOf(result, "Device", SymbolKind.Class);
        Assert.True(device >= 0);

        Assert.All(
            result.References.Where(r => r.Kind == ReferenceKind.Inherit),
            r => Assert.Equal(device, r.FromSymbolLocalIndex));
    }

    [Fact]
    public void VirtualAndOverrideSpecifiersAreCapturedAsModifiers()
    {
        var result = Analyze("""
            class Base {
            public:
                virtual int Run(int mode);
                virtual void Stop() { }
            };

            class Impl : public Base {
                int Run(int mode) override;
                void Halt() final { }
            };
            """);

        Assert.Equal("virtual", result.Symbols.Single(s => s.Name == "Run" && s.Modifiers == "virtual").Modifiers);
        Assert.Equal("override", result.Symbols.Single(s => s.Name == "Run" && s.Modifiers == "override").Modifiers);
        Assert.Equal("virtual", Symbol(result, "Stop").Modifiers);
        Assert.Equal("final", Symbol(result, "Halt").Modifiers);

        // Per-member visibility is deliberately NOT captured: `public:` is a section
        // label, and attributing it to each member below would be inference.
        Assert.All(result.Symbols, s => Assert.DoesNotContain("public", s.Modifiers ?? ""));
    }

    [Fact]
    public void IncludesAreRecordedWithDelimitersStripped()
    {
        var result = Analyze(Source);

        Assert.Equal(new[] { "vector", "device.hpp" }, ReferenceNames(result, ReferenceKind.Include));
    }

    [Fact]
    public void CallsAreAttributedToTheEnclosingMemberFunction()
    {
        var result = Analyze(Source);

        // The body lives in the out-of-line definition, not the in-class declaration.
        var symbols = result.Symbols.ToList();
        var send = symbols.FindIndex(s => s is { Name: "Send", IsDefinition: true });

        var call = Assert.Single(result.References, r => r is { Kind: ReferenceKind.Call, Name: "Log" });

        Assert.Equal(send, call.FromSymbolLocalIndex);
    }

    [Fact]
    public void AQualifiedNameReachesBothHalves()
    {
        var result = Analyze("""
            enum class State { Idle };
            int pick() { return (int)State::Idle; }
            """);

        // State::Idle is a use of the enumerator and a type reference to the enum, which is
        // what puts both on the graph.
        Assert.Contains(result.References, r => r is { Kind: ReferenceKind.Use, Name: "Idle" });
        Assert.Contains(result.References, r => r is { Kind: ReferenceKind.TypeUse, Name: "State" });
    }

    [Fact]
    public void ConstructingATypeIsATypeReferenceNotACallToAFunction()
    {
        var result = Analyze("""
            struct Frame { int n; };
            Frame *make() { return new Frame(); }
            """);

        // `new Frame()` names a type; there is no function called Frame to call, so
        // recording it as a call would point the graph at nothing.
        var construction = Assert.Single(
            result.References,
            r => r.Name == "Frame" && r.Position.Line == 2 && r.Position.Column > 20);

        Assert.Equal(ReferenceKind.TypeUse, construction.Kind);
        Assert.DoesNotContain(result.References, r => r is { Kind: ReferenceKind.Call, Name: "Frame" });
    }
}
