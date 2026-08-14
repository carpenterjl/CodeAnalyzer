using System.Text;

namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// The one-line description of what a symbol is: "public method · overload 2 of 3",
/// "private string field", "namespace".
/// <para>
/// Lives in Core beside <see cref="KindLabels"/> and <see cref="ModifierFacts"/>, and for
/// the same reason: the XAML detail pane, the search rows and the JSON payload all render
/// this string, and a second copy would eventually disagree with the first.
/// </para>
/// <para>
/// Every part is a stored fact. Nothing is defaulted in — a C# class with no visibility
/// keyword is internal by the language's rule, but the source did not say so, so neither
/// does this.
/// </para>
/// </summary>
public static class SymbolFacts
{
    /// <summary>
    /// Longest declared type worth putting in a descriptor. A type long enough to hit this
    /// has stopped being a useful label, and the descriptor is drawn on a graph node.
    /// </summary>
    private const int MaxTypeTextLength = 40;

    /// <summary>
    /// Describes a symbol in one line.
    /// </summary>
    /// <param name="kind">The declared kind.</param>
    /// <param name="modifiers">Verbatim modifier keywords, or null where none were captured.</param>
    /// <param name="typeText">The declared type, used only when there is no parameter list.</param>
    /// <param name="hasParameterList">
    /// Whether the declaration has a parameter list at all. A callable already shows its
    /// types in its parameters, so repeating a return type here would be noise; a variable
    /// has no parameters and its type is the fact worth stating.
    /// </param>
    /// <param name="overloadCount">
    /// How many definitions share this symbol's name in its own scope. One means the name
    /// is not overloaded and nothing about overloading is said.
    /// </param>
    /// <param name="overloadOrdinal">This symbol's 1-based position within that set.</param>
    public static string Describe(
        SymbolKind kind,
        string? modifiers,
        string? typeText,
        bool hasParameterList,
        int overloadCount = 1,
        int overloadOrdinal = 1)
    {
        var builder = new StringBuilder();

        Append(builder, Normalise(modifiers));

        if (!hasParameterList)
        {
            Append(builder, Truncate(Normalise(typeText), MaxTypeTextLength));
        }

        Append(builder, KindLabels.For(kind));

        // The separator marks a change of subject: everything before it describes the
        // declaration, this describes where it sits among its namesakes.
        if (overloadCount > 1)
        {
            builder.Append(" · overload ").Append(overloadOrdinal).Append(" of ").Append(overloadCount);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? part)
    {
        if (string.IsNullOrEmpty(part))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(part);
    }

    /// <summary>
    /// Collapses the run of whitespace a source slice can carry into single spaces. A type
    /// written across two lines is one fact, and the descriptor is drawn on a node label
    /// where a stray newline would silently become an extra row.
    /// </summary>
    private static string? Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>Cuts with a visible ellipsis, so a shortened type never reads as complete.</summary>
    private static string? Truncate(string? text, int maxLength) =>
        text is null || text.Length <= maxLength ? text : text[..maxLength] + "…";
}
