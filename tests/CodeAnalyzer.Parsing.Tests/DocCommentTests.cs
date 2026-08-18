using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Parsing;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The comment written immediately above a declaration, captured with it.
/// <para>
/// The index could always say where a symbol is and never a word of what its author wrote
/// about it — and a comment is where a codebase keeps what a name cannot carry. Measured
/// before it was built: 20.3% of this repo's definitions have such a comment and 28.3% of
/// JGraph's, so it is neither a rarity nor the common case.
/// </para>
/// <para>
/// Adjacency is the whole rule, and these tests are mostly about what it EXCLUDES. A rule
/// that swept up any comment above a declaration would attach a file header to whichever
/// symbol happened to be written first, and then "search the comments" would return that
/// symbol for every word in the header.
/// </para>
/// </summary>
public class DocCommentTests : IDisposable
{
    private readonly TreeSitterAnalyzer _csharp =
        new(LanguageRegistry.ForName(LanguageRegistry.CSharp)!);
    private readonly TreeSitterAnalyzer _python =
        new(LanguageRegistry.ForName(LanguageRegistry.Python)!);
    private readonly TreeSitterAnalyzer _xaml =
        new(LanguageRegistry.ForName(LanguageRegistry.Xaml)!);

    public void Dispose()
    {
        _csharp.Dispose();
        _python.Dispose();
        _xaml.Dispose();
    }

    private static string? DocOf(ParseResult result, string name) =>
        result.Symbols.Single(s => s.Name == name).DocComment;

    [Fact]
    public void ACommentDirectlyAboveADeclarationBelongsToIt()
    {
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                /// <summary>
                /// Retries a request that came back 502, up to three times.
                /// </summary>
                public void Send() { }
            }
            """, CancellationToken.None);

        var doc = DocOf(result, "Send");

        // Punctuation gone, lines joined, tag-only lines dropped — the form that can be
        // matched without knowing the file writes `///` rather than `#`.
        Assert.Equal("Retries a request that came back 502, up to three times.", doc);
    }

    [Fact]
    public void ABlankLineEndsTheComment()
    {
        // The rule the user asked for, and the one that keeps this useful: a comment
        // separated from a declaration is about the section, not about the declaration.
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                // Everything below this line concerns transport.

                public void Send() { }
            }
            """, CancellationToken.None);

        Assert.Null(DocOf(result, "Send"));
    }

    [Fact]
    public void OnlyTheAdjacentRunIsTaken()
    {
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                // A heading paragraph about the whole file.

                // Sends one request.
                // Blocks until it completes.
                public void Send() { }
            }
            """, CancellationToken.None);

        Assert.Equal("Sends one request. Blocks until it completes.", DocOf(result, "Send"));
    }

    [Fact]
    public void ATrailingCommentOnTheLineAboveIsNotADoc()
    {
        // `int a; // about a` ends on the line directly above `int b`, so a rule that looked
        // only at line numbers would hand b a comment about a. The comment has to be the
        // first thing on its line.
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                public int Timeout;  // milliseconds, not seconds
                public int Retries;
            }
            """, CancellationToken.None);

        Assert.Null(DocOf(result, "Retries"));
        Assert.Null(DocOf(result, "Timeout"));
    }

    [Fact]
    public void ABlockCommentIsOneComment()
    {
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                /*
                 * Opens the port.
                 * Idempotent.
                 */
                public void Open() { }
            }
            """, CancellationToken.None);

        Assert.Equal("Opens the port. Idempotent.", DocOf(result, "Open"));
    }

    [Fact]
    public void EveryLanguageIsCoveredByTheSameRule()
    {
        // Comments are found as tree nodes rather than by matching `//` and `#` against the
        // text, so a language arrives supported rather than arrives silent. Python and XAML
        // share nothing with C# syntactically and need no code of their own.
        var python = _python.Analyze("t.py", """
            # Parses one frame off the wire.
            def read_frame():
                pass
            """, CancellationToken.None);

        Assert.Equal("Parses one frame off the wire.", DocOf(python, "read_frame"));

        var xaml = _xaml.Analyze("t.xaml", """
            <ResourceDictionary>
                <!-- The chrome behind every row. -->
                <Style x:Key="RowButton" TargetType="Button" />
            </ResourceDictionary>
            """, CancellationToken.None);

        Assert.Equal("The chrome behind every row.", DocOf(xaml, "RowButton"));
    }

    [Fact]
    public void ACommentThatIsOnlyPunctuationIsNotStored()
    {
        // A rule of dashes is a separator. Storing it would put an entry in the searchable
        // text that says nothing and matches "---".
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                // ------------------------------------------
                public void Send() { }
            }
            """, CancellationToken.None);

        Assert.Null(DocOf(result, "Send"));
    }

    [Fact]
    public void ADeclarationWithNoCommentStoresNothing()
    {
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                public void Send() { }
            }
            """, CancellationToken.None);

        Assert.Null(DocOf(result, "Send"));
        Assert.Null(DocOf(result, "Api"));
    }
}
