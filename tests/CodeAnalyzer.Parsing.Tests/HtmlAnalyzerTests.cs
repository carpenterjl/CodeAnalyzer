using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Extraction checks for the HTML query pack.
/// </summary>
public class HtmlAnalyzerTests() : LanguagePackFixture(LanguageRegistry.Html, "index.html")
{
    private const string Source = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <title>Dashboard</title>
            <link rel="stylesheet" href="css/app.css">
            <script src="js/app.js" defer></script>
        </head>
        <body>
            <main id="content">
                <section id="device-list" class="panel wide">
                    <a href="detail.html">Detail</a>
                    <img src="img/logo.png" alt="logo">
                </section>
            </main>
            <a href="https://example.com/docs">Docs</a>
        </body>
        </html>
        """;

    [Fact]
    public void ElementsWithAnIdAreTheDeclarationsAndTheTagIsRecordedAsTheType()
    {
        var result = Analyze(Source);

        var content = Symbol(result, "content");
        Assert.Equal(SymbolKind.MarkupElement, content.Kind);
        Assert.Equal("main", content.TypeText);

        Assert.Equal("section", Symbol(result, "device-list").TypeText);
    }

    [Fact]
    public void TagNamesAndClassesAreNotDeclarations()
    {
        var result = Analyze(Source);

        // Many elements share a tag or a class, so neither names anything.
        Assert.Equal(new[] { "content", "device-list" }, result.Symbols.Select(s => s.Name));
    }

    [Fact]
    public void NestedElementsAreLinkedToTheirEnclosingElement()
    {
        var result = Analyze(Source);

        Assert.Equal(new[] { "device-list" }, MembersOf(result, "content"));
    }

    [Fact]
    public void ResourceAttributesBecomeFileDependencies()
    {
        var result = Analyze(Source);

        Assert.Equal(
            new[] { "css/app.css", "js/app.js", "detail.html", "img/logo.png", "https://example.com/docs" },
            ReferenceNames(result, ReferenceKind.Import));
    }

    /// <summary>
    /// HTML keeps its raw-text elements, and that is correct: <c>&lt;style&gt;</c> holds CSS
    /// and <c>&lt;script&gt;</c> holds JavaScript, neither of which declares anything this
    /// pack indexes. It is worth pinning because the fix for the XAML side was to point
    /// <c>.xaml</c> at a second grammar with the tag table switched off, and pointing
    /// <c>.html</c> at that same grammar is a one-line edit away.
    /// <para>
    /// This is also what a round of trying to reproduce XAML's failure here found: text
    /// that looks like markup inside a style element is a comment, it costs nothing, and no
    /// error is raised because nothing went wrong.
    /// </para>
    /// </summary>
    [Fact]
    public void MarkupShapedTextInsideAStyleElementCostsNothingAndRaisesNothing()
    {
        var result = Analyze("""
            <div id="before">x</div>
            <style>
              #styled { color: red; }
              /* <div id="commented-out">not an element</div> */
            </style>
            <div id="after">y</div>
            """);

        // The id inside the comment is not a declaration, and the two real ones both
        // survive — the second is what a swallow would have taken.
        Assert.Equal(new[] { "before", "after" }, result.Symbols.Select(s => s.Name));
        Assert.Null(result.ErrorLine);
    }

    /// <summary>
    /// And where a raw-text element really does swallow markup, the report names the
    /// element that did it. This is the case XAML got wrong for nine rounds: there the
    /// parse stopped at <c>&lt;/Style.Triggers&gt;</c>, which begins with the characters
    /// of an end tag, so the label blamed a property element that had cost nothing.
    /// </summary>
    [Fact]
    public void AnUnclosedScriptIsReportedAgainstTheScriptItself()
    {
        var result = Analyze("""
            <div id="reached">x</div>
            <script>
              function lost() { return 1; }
            <div id="swallowed">y</div>
            """);

        // The loss is real and is HTML's own rule, not a defect: everything after an
        // unclosed <script> is script text.
        Assert.Equal(new[] { "reached" }, result.Symbols.Select(s => s.Name));
        Assert.Equal("<script>", result.ErrorText);

        // What the report could never say before: the construct the parse stopped in
        // runs to the last line — everything after the error was consumed as its body.
        // "1 indexed" alone is the exact gap that hid the XAML swallow for nine rounds.
        Assert.Equal(2, result.ErrorLine);
        Assert.Equal(4, result.ErrorEndLine);

        // And the denominator that makes those two numbers mean something. 4 is only
        // alarming because the file ends at 4; the extent alone cannot say so, which is why
        // it read as a mild clause for a round.
        Assert.Equal(4, result.LineCount);
    }

    [Fact]
    public void AFileThatParsesCleanlyStillCountsItsLines()
    {
        // Written for every file, not only broken ones — a denominator that only exists
        // when something has already gone wrong is one more thing to be null at the moment
        // it is needed.
        var result = Analyze("""
            <div id="one">x</div>
            <div id="two">y</div>
            <div id="three">z</div>
            """);

        Assert.Null(result.ErrorLine);
        Assert.Equal(3, result.LineCount);
    }
}
