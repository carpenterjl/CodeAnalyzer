using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Extraction checks for the JavaScript query pack.
/// <para>
/// The fixture source deliberately mirrors this repo's own page bundle rather than modern
/// module style: an IIFE-era file of <c>var</c> declarations, function expressions bound to
/// names, and an object literal that registers behaviour by key. That is the shape the pack
/// has to read to be worth anything here.
/// </para>
/// </summary>
public class JavaScriptAnalyzerTests()
    : LanguagePackFixture(LanguageRegistry.JavaScript, "graph.js")
{
    private const string Source = """
        import { cssVar } from "./util.js";

        var bridge = window.graphBridge;
        var DOUBLE_TAP_MS = 350;
        var GROUPS = ["function", "type"];

        function buildStyle(accent) {
            var stroke = 2;
            return { selector: "node", weight: stroke };
        }

        var relabelAll = function (nodes) {
            return buildStyle(nodes);
        };

        const clip = (text, max) => text.slice(0, max);

        class Renderer extends Base {
            static LIMIT = 5;

            constructor(canvas) {
                this.canvas = canvas;
            }

            draw(nodes) {
                return new Frame(nodes);
            }
        }

        window.exportView = function (format) {
            return format;
        };

        var view = {
            element: null,
            onShow: function () {
                return clip("x", 1);
            },
            onExport: (format) => format
        };
        """;

    [Fact]
    public void AFunctionIsAFunctionWhicheverWayItIsWritten()
    {
        var result = Analyze(Source);

        // The keyword form, a function expression bound to a name, and an arrow — the
        // language distinguishes them, a reader looking for "where is relabelAll" does not.
        Assert.Equal(SymbolKind.Function, Symbol(result, "buildStyle").Kind);
        Assert.Equal(SymbolKind.Function, Symbol(result, "relabelAll").Kind);
        Assert.Equal(SymbolKind.Function, Symbol(result, "clip").Kind);

        Assert.Equal("(nodes)", Symbol(result, "relabelAll").ParameterText);
        Assert.Equal("(text, max)", Symbol(result, "clip").ParameterText);
    }

    [Fact]
    public void AssigningAFunctionToAPropertyDeclaresIt()
    {
        var result = Analyze(Source);

        var exportView = Symbol(result, "exportView");
        Assert.Equal(SymbolKind.Function, exportView.Kind);
        Assert.Equal("(format)", exportView.ParameterText);
    }

    [Fact]
    public void ScreamingCaseBecomesAConstantCarryingItsLiteral()
    {
        var result = Analyze(Source);

        var tap = Symbol(result, "DOUBLE_TAP_MS");
        Assert.Equal(SymbolKind.Constant, tap.Kind);

        // The literal is what lets a page constant meet the same number in the firmware.
        Assert.Equal("350", tap.Value);
        Assert.Equal(SymbolKind.Constant, Symbol(result, "GROUPS").Kind);
    }

    [Fact]
    public void APlainDeclarationStaysAVariableAndKeepsItsInitializer()
    {
        var result = Analyze(Source);

        var bridge = Symbol(result, "bridge");
        Assert.Equal(SymbolKind.Variable, bridge.Kind);
        Assert.Equal("window.graphBridge", bridge.Value);
    }

    [Fact]
    public void AFunctionsOwnStateIsContainedByIt()
    {
        var result = Analyze(Source);

        // Containment is what marks a local as a local, which is what keeps every `var i`
        // in the bundle out of search results.
        Assert.Contains("stroke", MembersOf(result, "buildStyle"));
    }

    [Fact]
    public void ClassMembersAreMethodsAndFields()
    {
        var result = Analyze(Source);

        Assert.Equal(SymbolKind.Class, Symbol(result, "Renderer").Kind);
        Assert.Equal(SymbolKind.Method, Symbol(result, "draw").Kind);
        Assert.Equal(SymbolKind.Method, Symbol(result, "constructor").Kind);

        var limit = Symbol(result, "LIMIT");
        Assert.Equal(SymbolKind.Field, limit.Kind);
        Assert.Equal("5", limit.Value);

        var members = MembersOf(result, "Renderer");
        Assert.Contains("draw", members);
        Assert.Contains("LIMIT", members);
    }

    [Fact]
    public void AnObjectLiteralsFunctionKeysAreMethodsAndItsDataKeysAreFields()
    {
        var result = Analyze(Source);

        // This is how the page's view modules declare what they do, so it is the one shape
        // that most needed to stop being invisible.
        Assert.Equal(SymbolKind.Method, Symbol(result, "onShow").Kind);
        Assert.Equal(SymbolKind.Method, Symbol(result, "onExport").Kind);
        Assert.Equal(SymbolKind.Field, Symbol(result, "element").Kind);
    }

    [Fact]
    public void CallsInheritanceInstantiationAndImportsAreRecorded()
    {
        var result = Analyze(Source);

        var calls = ReferenceNames(result, ReferenceKind.Call);
        Assert.Contains("buildStyle", calls);
        Assert.Contains("clip", calls);

        // A method call keeps only the member name: the receiver's type is not syntax.
        Assert.Contains("slice", calls);

        Assert.Contains("Base", ReferenceNames(result, ReferenceKind.Inherit));
        Assert.Contains("Frame", ReferenceNames(result, ReferenceKind.Instantiate));
        Assert.Contains("./util.js", ReferenceNames(result, ReferenceKind.Import));
    }

    [Fact]
    public void ARequireCallIsRecordedAsAnImportOfTheFileItNames()
    {
        var result = Analyze("""
            var dep = require("./bridge.js");
            """);

        Assert.Contains("./bridge.js", ReferenceNames(result, ReferenceKind.Import));
    }
}
