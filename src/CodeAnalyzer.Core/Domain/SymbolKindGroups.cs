namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// The families a user can filter search results by.
/// <para>
/// Deliberately the same vocabulary the graph already colours by
/// (<see cref="KindLabels.GroupFor"/>), with one addition: an interface is its own group.
/// It draws with its own colour and shape on the canvas, so folding it back into "type"
/// here would make the filter disagree with the picture.
/// </para>
/// <para>
/// Nothing here is a new fact — every group is a partition of the stored
/// <see cref="SymbolKind"/> values, computed from the same rule the renderer uses, so the
/// two cannot drift apart.
/// </para>
/// </summary>
public static class SymbolKindGroups
{
    /// <summary>Display order, matching the graph legend left to right.</summary>
    public static readonly IReadOnlyList<string> All =
        ["function", "type", "interface", "constant", "macro", "module", "variable"];

    private static readonly Dictionary<string, IReadOnlySet<SymbolKind>> KindsByGroup =
        Enum.GetValues<SymbolKind>()
            .GroupBy(For)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<SymbolKind>)g.ToHashSet());

    /// <summary>Which group a kind belongs to.</summary>
    public static string For(SymbolKind kind) =>
        kind == SymbolKind.Interface ? "interface" : KindLabels.GroupFor(kind);

    /// <summary>
    /// Every kind covered by the named groups. An unknown group name contributes nothing
    /// rather than throwing — a stale name in a saved preference must not break search.
    /// </summary>
    public static IReadOnlySet<SymbolKind> KindsIn(IEnumerable<string> groups)
    {
        var kinds = new HashSet<SymbolKind>();
        foreach (var group in groups)
        {
            if (KindsByGroup.TryGetValue(group, out var members))
            {
                kinds.UnionWith(members);
            }
        }

        return kinds;
    }
}
