using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Cli.Output;

/// <summary>
/// The CLI's short symbol-kind tokens (<c>fn</c>, <c>const</c>, <c>iface</c>) and the
/// parser for <c>--kinds</c> filters.
/// <para>
/// Filters accept three spellings: a family name from <see cref="SymbolKindGroups"/>
/// ("function" covers functions and methods, exactly the graph's colour families), a full
/// kind label from <see cref="KindLabels"/>, or the short token printed in results. An
/// unknown name is an error, not an empty filter — a typo silently matching nothing would
/// read as "no results".
/// </para>
/// </summary>
internal static class KindTokens
{
    public static string For(SymbolKind kind) => kind switch
    {
        SymbolKind.Function => "fn",
        SymbolKind.Method => "method",
        SymbolKind.Class => "class",
        SymbolKind.Struct => "struct",
        SymbolKind.Union => "union",
        SymbolKind.Enum => "enum",
        SymbolKind.EnumMember => "enum-val",
        SymbolKind.Field => "field",
        SymbolKind.Variable => "var",
        SymbolKind.Constant => "const",
        SymbolKind.Macro => "macro",
        SymbolKind.Typedef => "typedef",
        SymbolKind.Namespace => "ns",
        SymbolKind.Interface => "iface",
        SymbolKind.Property => "prop",
        SymbolKind.Parameter => "param",
        SymbolKind.Module => "module",
        SymbolKind.Port => "port",
        SymbolKind.MarkupElement => "elem",
        SymbolKind.ResourceKey => "res",
        _ => "sym",
    };

    /// <summary>
    /// Parses a comma-separated kind filter. Returns null with <paramref name="error"/> set
    /// when any item is unrecognised.
    /// </summary>
    public static IReadOnlySet<SymbolKind>? Parse(string text, out string? error)
    {
        error = null;
        var kinds = new HashSet<SymbolKind>();

        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = raw.ToLowerInvariant();

            if (SymbolKindGroups.All.Contains(item))
            {
                kinds.UnionWith(SymbolKindGroups.KindsIn([item]));
                continue;
            }

            var matched = Enum.GetValues<SymbolKind>().Where(k =>
                string.Equals(k.ToString(), item, StringComparison.OrdinalIgnoreCase)
                || KindLabels.For(k) == item
                || For(k) == item).ToList();

            if (matched.Count == 0)
            {
                error = $"unknown kind '{raw}' — use a family ({string.Join("/", SymbolKindGroups.All)}) "
                    + "or a kind token (fn, method, class, struct, const, macro, var, iface, …)";
                return null;
            }

            kinds.UnionWith(matched);
        }

        return kinds.Count == 0 ? null : kinds;
    }
}
