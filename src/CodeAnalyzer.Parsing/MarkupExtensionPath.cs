namespace CodeAnalyzer.Parsing;

/// <summary>
/// Reads the one referencing name out of a markup extension attribute value —
/// <c>{Binding SearchQuery, Mode=TwoWay}</c> refers to <c>SearchQuery</c>,
/// <c>{StaticResource PanelBrush}</c> to <c>PanelBrush</c>.
/// <para>
/// This exists because the grammar cannot: an attribute value is one token to
/// tree-sitter, so the query pack can only say "this value is a markup extension" and
/// hand the string here — the same division of labour as <c>LiteralValueParser</c>,
/// which reads number notation the packs cannot.
/// </para>
/// <para>
/// Deliberately narrow. Four extension words are read — <c>Binding</c> and
/// <c>TemplateBinding</c> (the path), <c>StaticResource</c> and <c>DynamicResource</c>
/// (the key) — and one name comes back: the first path segment, which is the thing the
/// data context type must actually carry. Everything else — <c>x:Static</c>,
/// <c>RelativeSource</c>, converters, attached-property paths in parentheses — returns
/// null and stays what it was, a verbatim value making no claim. A wrong guess here
/// becomes a wrong edge in the graph, so anything this parser is not sure of, it
/// refuses.
/// </para>
/// </summary>
public static class MarkupExtensionPath
{
    /// <summary>
    /// What an extension refers to: the name, where it sits within the raw value, and
    /// which world it resolves into — <see cref="IsResource"/> separates a resource key
    /// (which names a markup element) from a binding path (which names a property on a
    /// type the markup never states). One flag rather than one result type per extension
    /// word, because that distinction is the only one resolution acts on.
    /// </summary>
    public readonly record struct ExtractedName(string Name, int Offset, bool IsResource);

    /// <summary>
    /// The referenced name and its character offset within <paramref name="value"/>,
    /// or null when the value is not an extension this parser reads or names no path.
    /// </summary>
    public static ExtractedName? Extract(string value)
    {
        // "{}" is XAML's escape: the rest of the value is a literal string.
        if (value.Length < 3 || value[0] != '{' || value.StartsWith("{}", StringComparison.Ordinal)
            || value[^1] != '}')
        {
            return null;
        }

        var inner = value.AsSpan(1, value.Length - 2);
        var innerStart = 1;

        // XAML tolerates whitespace after the opening brace.
        var lead = CountLeadingWhitespace(inner);
        inner = inner[lead..];
        innerStart += lead;

        // The extension word ends at the first whitespace (or the brace we removed).
        var wordEnd = 0;
        while (wordEnd < inner.Length && !char.IsWhiteSpace(inner[wordEnd]))
        {
            wordEnd++;
        }

        var word = inner[..wordEnd];
        var wantsPath = word.SequenceEqual("Binding") || word.SequenceEqual("TemplateBinding");
        var wantsKey = word.SequenceEqual("StaticResource") || word.SequenceEqual("DynamicResource");
        if (!wantsPath && !wantsKey)
        {
            return null;
        }

        // Arguments, split at top-level commas: a nested {RelativeSource …} must not
        // leak its content into a neighbouring argument.
        var depth = 0;
        var argStart = wordEnd;
        for (var i = wordEnd; i <= inner.Length; i++)
        {
            var atEnd = i == inner.Length;
            if (!atEnd && inner[i] == '{')
            {
                depth++;
                continue;
            }

            if (!atEnd && inner[i] == '}')
            {
                depth--;
                continue;
            }

            if (!atEnd && (inner[i] != ',' || depth > 0))
            {
                continue;
            }

            var argument = inner[argStart..i];
            var trimmedStart = argStart + CountLeadingWhitespace(argument);
            var trimmed = argument.Trim();

            if (TryReadName(trimmed, wantsPath, out var name, out var withinArgument))
            {
                return new ExtractedName(name, innerStart + trimmedStart + withinArgument, wantsKey);
            }

            argStart = i + 1;
        }

        return null;
    }

    /// <summary>
    /// One argument: a positional value is the path or key; <c>Path=…</c> is the path
    /// spelled as a named argument. Any other named argument is not a name source.
    /// </summary>
    private static bool TryReadName(
        ReadOnlySpan<char> argument, bool wantsPath, out string name, out int offset)
    {
        name = string.Empty;
        offset = 0;

        var equals = TopLevelIndexOf(argument, '=');
        if (equals >= 0)
        {
            if (!wantsPath || !argument[..equals].TrimEnd().SequenceEqual("Path"))
            {
                return false;
            }

            var afterEquals = equals + 1;
            var valueSpan = argument[afterEquals..];
            var lead = CountLeadingWhitespace(valueSpan);
            offset = afterEquals + lead;
            argument = valueSpan.Trim();
        }

        // The first path segment is the name the target type must carry. A path that
        // does not begin with a plain identifier — attached properties in parentheses,
        // an indexer, a nested extension — is refused rather than guessed at.
        var end = 0;
        while (end < argument.Length && (char.IsLetterOrDigit(argument[end]) || argument[end] == '_'))
        {
            end++;
        }

        if (end == 0 || (end < argument.Length && argument[end] is not ('.' or '[' or '/')))
        {
            return false;
        }

        name = argument[..end].ToString();
        return true;
    }

    /// <summary>
    /// The type name a binding-context attribute declares, or null when the value
    /// declares no type this parser is sure of.
    /// <para>
    /// Three spellings name a context type in this corpus's own markup and they are the
    /// three read here: <c>DataType="{x:Type vm:SearchResultItem}"</c>,
    /// <c>d:DataContext="{d:DesignInstance Type=vm:MainViewModel}"</c> (the positional
    /// spelling too), and the unbraced XML-namespace form <c>DataType="local:Item"</c>.
    /// The XML prefix is dropped because a definition carries its short name, the same
    /// rule the x:Class split follows. Everything else returns null — and null is load-
    /// bearing rather than a shrug: a runtime <c>DataContext="{Binding Detail}"</c>
    /// re-scopes its children to a type this index cannot know, so the caller treats a
    /// typeless context as a wall, not a window to the context outside it.
    /// </para>
    /// </summary>
    public static string? ContextType(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty || trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed[0] != '{')
        {
            // Unbraced: the XML-namespace DataType form, a bare (possibly prefixed) name.
            return ReadTypeName(trimmed);
        }

        if (trimmed[^1] != '}')
        {
            return null;
        }

        var inner = trimmed[1..^1].Trim();

        var wordEnd = 0;
        while (wordEnd < inner.Length && !char.IsWhiteSpace(inner[wordEnd]))
        {
            wordEnd++;
        }

        var word = inner[..wordEnd];
        var rest = inner[wordEnd..].Trim();

        if (word.SequenceEqual("x:Type"))
        {
            return ReadTypeName(rest);
        }

        if (!word.SequenceEqual("d:DesignInstance"))
        {
            return null;
        }

        // d:DesignInstance takes the type positionally or as Type=…, alongside arguments
        // like IsDesignTimeCreatable that are not name sources. Split at top-level commas,
        // same as Extract.
        var depth = 0;
        var argStart = 0;
        for (var i = 0; i <= rest.Length; i++)
        {
            var atEnd = i == rest.Length;
            if (!atEnd && rest[i] == '{')
            {
                depth++;
                continue;
            }

            if (!atEnd && rest[i] == '}')
            {
                depth--;
                continue;
            }

            if (!atEnd && (rest[i] != ',' || depth > 0))
            {
                continue;
            }

            var argument = rest[argStart..i].Trim();
            argStart = i + 1;

            var equals = TopLevelIndexOf(argument, '=');
            if (equals >= 0)
            {
                if (!argument[..equals].TrimEnd().SequenceEqual("Type"))
                {
                    continue;
                }

                argument = argument[(equals + 1)..].Trim();
            }

            if (ReadTypeName(argument) is { } name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// A (possibly XML-prefixed) type name's short segment, or null when the text is not
    /// one plain identifier — anything with dots, braces or spaces is refused, not read.
    /// </summary>
    private static string? ReadTypeName(ReadOnlySpan<char> text)
    {
        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            text = text[(colon + 1)..];
        }

        if (text.IsEmpty)
        {
            return null;
        }

        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return null;
            }
        }

        return char.IsDigit(text[0]) ? null : text.ToString();
    }

    private static int TopLevelIndexOf(ReadOnlySpan<char> text, char target)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0 && text[i] == target)
            {
                return i;
            }
        }

        return -1;
    }

    private static int CountLeadingWhitespace(ReadOnlySpan<char> text)
    {
        var count = 0;
        while (count < text.Length && char.IsWhiteSpace(text[count]))
        {
            count++;
        }

        return count;
    }
}
