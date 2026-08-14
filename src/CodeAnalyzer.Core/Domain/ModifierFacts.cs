namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// Token lookups over a symbol's stored modifier string.
/// <para>
/// The stored value is the verbatim keywords in source order; these helpers only ask
/// whether a known token appears in it. Like <see cref="KindLabels"/>, this lives in Core
/// because both the XAML views and the JSON payload builders consume it — a second copy
/// would eventually disagree.
/// </para>
/// </summary>
public static class ModifierFacts
{
    /// <summary>
    /// Visibility tokens, longest first so "protected internal" wins over "protected".
    /// C# is the only indexed language that spells per-declaration visibility.
    /// </summary>
    private static readonly string[] VisibilityTokens =
    [
        "protected internal",
        "private protected",
        "public",
        "internal",
        "protected",
        "private",
    ];

    /// <summary>
    /// The declaration's visibility keyword exactly as written, or null when the
    /// modifier list carries none. No default is invented: an unmodified C# class is
    /// internal by the language's rule, but the source does not say so, so neither do we.
    /// </summary>
    public static string? VisibilityToken(string? modifiers)
    {
        if (string.IsNullOrEmpty(modifiers))
        {
            return null;
        }

        foreach (var token in VisibilityTokens)
        {
            if (HasToken(modifiers, token))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>Whether the modifier list contains this exact token ("override", "static").</summary>
    public static bool HasToken(string? modifiers, string token)
    {
        if (string.IsNullOrEmpty(modifiers))
        {
            return false;
        }

        // Whole-token match: "internal" must not match inside "protected internal"'s
        // neighbour by accident of substring — tokens are space-separated words.
        var index = modifiers.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var endsAt = index + token.Length;
            var startsClean = index == 0 || modifiers[index - 1] == ' ';
            var endsClean = endsAt == modifiers.Length || modifiers[endsAt] == ' ';

            if (startsClean && endsClean)
            {
                return true;
            }

            index = modifiers.IndexOf(token, endsAt, StringComparison.Ordinal);
        }

        return false;
    }
}
