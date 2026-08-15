using TreeSitter;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Pins the behaviour of the TreeSitter.DotNet binding that the analyzer depends on.
/// These are contract tests: if a package upgrade changes any of these, the analyzer
/// breaks, and this file says exactly how.
/// </summary>
public class TreeSitterApiContractTests
{
    private const string CSource = """
        #define BUFFER_SIZE 256

        struct Point {
            int x;
            int y;
        };

        static int add(int a, int b) {
            return a + b;
        }

        int main(void) {
            int total = add(1, 2);
            return total;
        }
        """;

    [Theory]
    [InlineData("C")]
    [InlineData("CPP")]
    [InlineData("C#")]
    [InlineData("Python")]
    [InlineData("HTML")]
    [InlineData("Verilog")]
    public void BuiltInGrammars_LoadByName(string id)
    {
        using var language = new Language(id);
        Assert.True(language.AbiVersion > 0);
    }

    [Fact]
    public void Parse_ProducesTreeWithoutErrors()
    {
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse(CSource);

        Assert.NotNull(tree);
        Assert.False(tree!.RootNode.HasError);
    }

    [Fact]
    public void NodeIndices_AreCharOffsetsIntoTheSourceString()
    {
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse(CSource)!;

        var query = language.CreateQuery("(function_definition declarator: (function_declarator declarator: (identifier) @name))");
        using var cursor = query.Execute(tree.RootNode);

        var first = cursor.Captures.First();

        // Text must equal the substring the indices point at; that is what lets the
        // analyzer slice signatures and values straight out of the source.
        Assert.Equal(first.Node.Text, CSource.Substring(first.Node.StartIndex, first.Node.EndIndex - first.Node.StartIndex));
    }

    [Fact]
    public void PointRow_IsZeroBased()
    {
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse(CSource)!;

        // The #define sits on the first line of the source.
        var query = language.CreateQuery("(preproc_def name: (identifier) @name)");
        using var cursor = query.Execute(tree.RootNode);

        var capture = cursor.Captures.First();
        Assert.Equal("BUFFER_SIZE", capture.Node.Text);
        Assert.Equal(0, capture.Node.StartPosition.Row);
    }

    [Fact]
    public void Captures_ExposeNameAndNode()
    {
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse(CSource)!;

        var query = language.CreateQuery("""
            (function_definition declarator: (function_declarator declarator: (identifier) @fn))
            (struct_specifier name: (type_identifier) @struct)
            """);
        using var cursor = query.Execute(tree.RootNode);

        var byName = cursor.Captures
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Node.Text).ToList());

        Assert.Equal(new[] { "add", "main" }, byName["fn"]);
        Assert.Equal(new[] { "Point" }, byName["struct"]);
    }

    [Fact]
    public void Matches_GroupCapturesFromOnePattern()
    {
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse(CSource)!;

        // One pattern with two captures: the analyzer relies on matches keeping them together
        // so a name and its value stay associated.
        var query = language.CreateQuery("(preproc_def name: (identifier) @name value: (preproc_arg) @value)");
        using var cursor = query.Execute(tree.RootNode);

        var match = Assert.Single(cursor.Matches);
        Assert.Equal("BUFFER_SIZE", match.Captures.First(c => c.Name == "name").Node.Text);
        Assert.Equal("256", match.Captures.First(c => c.Name == "value").Node.Text.Trim());
    }

    [Fact]
    public void Predicates_FilterMatchesRatherThanBeingReportedToTheCaller()
    {
        // Several packs narrow a broad pattern with #eq? or #match? — C# const fields,
        // Python SCREAMING_CASE constants, HTML id attributes. All of that depends on the
        // binding applying predicates itself; if it ever only reported them, those packs
        // would silently start matching everything.
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse("""
            static int keep_this(void) { return 1; }
            int drop_this(void) { return 2; }
            """)!;

        var query = language.CreateQuery("""
            (function_definition
              (storage_class_specifier) @storage
              declarator: (function_declarator declarator: (identifier) @name)
              (#eq? @storage "static"))
            """);

        using var cursor = query.Execute(tree.RootNode);
        var names = cursor.Matches.Select(m => m.Captures.First(c => c.Name == "name").Node.Text).ToList();

        Assert.Equal(new[] { "keep_this" }, names);
    }

    [Fact]
    public void MultipleModifiers_ArriveAsOneMatchEachWithTheFullCaptureSet()
    {
        // A declaration with N modifiers produces N matches, each carrying the whole
        // pattern's captures — no single match sees every modifier. The analyzer's
        // per-offset modifier accumulation is built on exactly this behaviour, and the
        // predicate operand (@storage here, @modifier in the packs) must be delivered in
        // Captures for it to have anything to accumulate.
        using var language = new Language("C#");
        using var parser = new Parser(language);
        using var tree = parser.Parse("class C { private static readonly int _x = 1; }")!;

        var query = language.CreateQuery("""
            (field_declaration
              (modifier) @modifier
              (variable_declaration
                (variable_declarator name: (identifier) @name)))
            """);

        using var cursor = query.Execute(tree.RootNode);
        var matches = cursor.Matches
            .Select(m => (
                Modifier: m.Captures.First(c => c.Name == "modifier").Node.Text,
                Name: m.Captures.First(c => c.Name == "name").Node.Text))
            .ToList();

        Assert.Equal(3, matches.Count);
        Assert.Equal(new[] { "private", "static", "readonly" }, matches.Select(m => m.Modifier));
        Assert.All(matches, m => Assert.Equal("_x", m.Name));
    }

    [Fact]
    public void MatchPredicate_AppliesARegularExpression()
    {
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse("int LOUD = 1;\nint quiet = 2;\n")!;

        var query = language.CreateQuery("""
            (init_declarator declarator: (identifier) @name (#match? @name "^[A-Z_]+$"))
            """);

        using var cursor = query.Execute(tree.RootNode);
        Assert.Equal(new[] { "LOUD" }, cursor.Captures.Select(c => c.Node.Text));
    }

    [Fact]
    public void HasError_IsSetOnTheSubtreeContainingTheError()
    {
        // The analyzer refuses declarations whose node contains a parse error, so this has
        // to be a property of the subtree and not just of the root.
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse("int good(void) { return 1; }\nint broken(void) { return")!;

        Assert.True(tree.RootNode.HasError);

        var query = language.CreateQuery("(function_definition) @fn");
        using var cursor = query.Execute(tree.RootNode);

        var good = cursor.Captures.First(c => c.Node.Text.Contains("good")).Node;
        Assert.False(good.HasError);
    }

    [Fact]
    public void GetChildForField_ReadsNamedFields()
    {
        using var language = new Language("C");
        using var parser = new Parser(language);
        using var tree = parser.Parse(CSource)!;

        var query = language.CreateQuery("(call_expression) @call");
        using var cursor = query.Execute(tree.RootNode);

        var call = cursor.Captures.First().Node;

        var function = call.GetChildForField("function");
        Assert.NotNull(function);
        Assert.Equal("add", function!.Text);

        var arguments = call.GetChildForField("arguments");
        Assert.NotNull(arguments);
        Assert.Equal(2, arguments!.NamedChildren.Count);
    }

    [Fact]
    public void MalformedSource_StillYieldsPartialTree()
    {
        using var language = new Language("C");
        using var parser = new Parser(language);

        // Truncated function body: tree-sitter is error tolerant, and the pipeline
        // depends on still getting usable symbols out of a broken file.
        using var tree = parser.Parse("int good(void) { return 1; }\nint broken(void) { return")!;

        Assert.True(tree.RootNode.HasError);

        var query = language.CreateQuery("(function_definition declarator: (function_declarator declarator: (identifier) @fn))");
        using var cursor = query.Execute(tree.RootNode);

        Assert.Contains("good", cursor.Captures.Select(c => c.Node.Text));
    }

    [Fact]
    public void MatchLimit_BoundsInProgressMatchesAndReportsExceeding()
    {
        // The analyzer caps in-progress matches to bound native memory on pathological
        // files, and reports the file's symbol list as possibly incomplete when the cap
        // bit. The semantics this pins, established empirically: the limit is on
        // *concurrently in-progress* matches, not on the total returned. A pattern whose
        // captures complete at the node it starts on never accumulates state — even 64
        // levels of nesting stay under a limit of 1 (first Execute). Only a pattern that
        // must stay open across its subtree (here: capturing the closing paren, which
        // arrives after all the nested content) holds one match per level, trips the
        // limit, and visibly drops matches (second and third). That is exactly the shape
        // of a pathological machine-generated file, which is why the cap is a safe memory
        // bound and not a silent truncation of healthy files.
        using var language = new Language("C");
        using var parser = new Parser(language);

        var nested = "int x = " + new string('(', 64) + "1" + new string(')', 64) + ";\n";
        using var tree = parser.Parse(nested)!;

        var nodeQuery = language.CreateQuery("(parenthesized_expression) @p");
        var pairQuery = language.CreateQuery("(parenthesized_expression \")\" @close) @p");

        using (var singleStep = nodeQuery.Execute(tree.RootNode, new QueryOptions { MatchLimit = 1 }))
        {
            Assert.Equal(64, singleStep.Matches.Count());
            Assert.False(singleStep.IsMatchLimitExceeded);
        }

        using (var unlimited = pairQuery.Execute(tree.RootNode))
        {
            Assert.Equal(64, unlimited.Matches.Count());
            Assert.False(unlimited.IsMatchLimitExceeded);
        }

        using (var limited = pairQuery.Execute(tree.RootNode, new QueryOptions { MatchLimit = 1 }))
        {
            // Drain the cursor first: the flag describes the last execution.
            var kept = limited.Matches.Count();
            Assert.True(limited.IsMatchLimitExceeded);
            Assert.True(kept < 64);
        }
    }

    [Fact]
    public void ParsersAreIndependentAcrossThreads()
    {
        // Each worker owns a Parser; a shared Language is only read. This asserts that
        // arrangement is actually safe, since the pipeline runs one parser per core.
        using var language = new Language("C");

        var results = new string[8];
        Parallel.For(0, results.Length, i =>
        {
            using var parser = new Parser(language);
            using var tree = parser.Parse($"int fn_{i}(void) {{ return {i}; }}")!;

            var query = language.CreateQuery("(function_definition declarator: (function_declarator declarator: (identifier) @fn))");
            using var cursor = query.Execute(tree.RootNode);
            results[i] = cursor.Captures.First().Node.Text;
        });

        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"fn_{i}"), results);
    }
}
