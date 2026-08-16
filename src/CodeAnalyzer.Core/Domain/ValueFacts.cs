using System.Globalization;

namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// How a value match is worded, in one place.
/// <para>
/// Lives in Core beside <see cref="SymbolFacts"/> and <see cref="KindLabels"/> for the same
/// reason: the detail pane, the search rows, the constants payload and the CLI all state
/// this relation, and four copies of the sentence would eventually disagree about what is
/// being claimed.
/// </para>
/// <para>
/// The claim is deliberately narrow. Two symbols appear together because their literals
/// denote the same number or the same characters — not because they mean the same thing.
/// A baud rate and a buffer size that are both 9600 are numerically equal and nothing more,
/// which is exactly what the note says.
/// </para>
/// </summary>
public static class ValueFacts
{
    /// <summary>
    /// The claim every same-value surface must repeat. Promoted to a constant in M18 when
    /// a fourth surface (the markdown report) needed it — the wording was already living
    /// in three places, worded three slightly different ways.
    /// </summary>
    public const string EvidenceSentence =
        "A shared value is evidence of an agreement, not proof of one.";

    /// <summary>Two literals denote the same integer, however each is written.</summary>
    public const string NumericallyEqual = "numerically equal";

    /// <summary>Two string literals denote the same characters.</summary>
    public const string IdenticallyWritten = "identical text";

    /// <summary>
    /// The one-line note for a matched symbol: its literal as written, the shared value
    /// where the two differ in form, and the relation being claimed.
    /// <list type="bullet">
    /// <item><c>0xA5 = 165 — numerically equal</c></item>
    /// <item><c>165 — numerically equal</c> (already the plain form)</item>
    /// <item><c>@"COM3" = "COM3" — identical text</c></item>
    /// </list>
    /// </summary>
    public static string EqualityNote(string? verbatim, long? number, string? text)
    {
        var canonical = Canonical(number, text);
        if (canonical is null)
        {
            return string.Empty;
        }

        var relation = number is not null ? NumericallyEqual : IdenticallyWritten;
        var written = Flatten(verbatim);

        return written is null || written == canonical
            ? $"{canonical} — {relation}"
            : $"{written} = {canonical} — {relation}";
    }

    /// <summary>
    /// The plain form of a value: decimal for a number, quoted for a string. This is the
    /// form the two literals share, never a re-rendering of either one's notation.
    /// </summary>
    public static string? Canonical(long? number, string? text)
    {
        if (number is { } value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return text is null ? null : $"\"{text}\"";
    }

    /// <summary>
    /// The descriptor a value-matched search row carries: <c>value 0xA5 (= 165)</c>.
    /// Reads as the declaration's own words first, with the shared value in brackets.
    /// </summary>
    public static string Descriptor(string? verbatim, long? number, string? text)
    {
        var canonical = Canonical(number, text);
        if (canonical is null)
        {
            return "value";
        }

        var written = Flatten(verbatim);

        return written is null || written == canonical
            ? $"value {canonical}"
            : $"value {written} (= {canonical})";
    }

    /// <summary>
    /// Collapses the whitespace a source slice can carry, the way
    /// <see cref="SymbolFacts"/> does: these strings are drawn on one-line rows where a
    /// newline out of the source would silently become an extra row.
    /// </summary>
    private static string? Flatten(string? verbatim)
    {
        if (string.IsNullOrWhiteSpace(verbatim))
        {
            return null;
        }

        var parts = verbatim.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
