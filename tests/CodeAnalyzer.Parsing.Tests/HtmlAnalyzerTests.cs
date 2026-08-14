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
}
