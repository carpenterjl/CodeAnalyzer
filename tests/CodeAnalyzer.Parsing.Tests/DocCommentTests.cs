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

    [Fact]
    public void AMethodsParametersDoNotEachTakeTheMethodsComment()
    {
        // Parameters begin on the line the method begins on, so walking up from each of
        // them reaches the same block. Round seventeen shipped without this check and
        // 45.3% of every comment stored on JGraph was a copy — most of them parameters.
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                /// Splits a leading axes handle off an argument list.
                public void PeelAxes(int first, int second) { }
            }
            """, CancellationToken.None);

        Assert.Equal("Splits a leading axes handle off an argument list.", DocOf(result, "PeelAxes"));
        Assert.Null(DocOf(result, "first"));
        Assert.Null(DocOf(result, "second"));
    }

    [Fact]
    public void ARecordsPositionalMembersDoNotEachTakeTheRecordsComment()
    {
        var result = _csharp.Analyze("t.cs", """
            /// One cell of a spreadsheet fixture.
            record XCell(int Kind, double Num, string Str);
            """, CancellationToken.None);

        Assert.Equal("One cell of a spreadsheet fixture.", DocOf(result, "XCell"));
        Assert.Null(DocOf(result, "Kind"));
        Assert.Null(DocOf(result, "Num"));
        Assert.Null(DocOf(result, "Str"));
    }

    [Fact]
    public void FieldsDeclaredTogetherOnOneLineAllKeepTheirSharedComment()
    {
        // The other side of the same rule, and the reason it is written as "an ANCESTOR on
        // this line" rather than "anything else on this line": these three are siblings,
        // the comment is about all three, and dropping two of them would lose real text.
        var result = _csharp.Analyze("t.cs", """
            class Projection
            {
                // Rotation rows of the view matrix: screen-right, screen-up, depth.
                private readonly double _ux, _uy, _uz;
            }
            """, CancellationToken.None);

        const string shared = "Rotation rows of the view matrix: screen-right, screen-up, depth.";
        Assert.Equal(shared, DocOf(result, "_ux"));
        Assert.Equal(shared, DocOf(result, "_uy"));
        Assert.Equal(shared, DocOf(result, "_uz"));
    }

    [Fact]
    public void ANestedDeclarationOnTheSameLineDoesNotTakeTheOuterOnesComment()
    {
        // The general form, without relying on C# parameters: whatever begins inside a
        // declaration that begins on the same line is that declaration's syntax.
        var result = _csharp.Analyze("t.cs", """
            class Api
            {
                /// Every retry policy the transport knows.
                enum Policy { Once, Twice }
            }
            """, CancellationToken.None);

        Assert.Equal("Every retry policy the transport knows.", DocOf(result, "Policy"));
        Assert.Null(DocOf(result, "Once"));
        Assert.Null(DocOf(result, "Twice"));
    }
}
