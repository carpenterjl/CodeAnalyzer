using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

/// <summary>What a treemap tile stands for at the level being shown.</summary>
public enum TreemapTileType
{
    Directory,
    File,

    /// <summary>A top-level symbol, shown once the drill reaches a single file.</summary>
    Symbol,
}

/// <summary>
/// One tile. <paramref name="Value"/> is what sizes it: indexed symbols for a directory or
/// file, source lines for a symbol.
/// </summary>
/// <param name="OutgoingLinks">
/// Resolved references from inside this tile that land outside it. Together with
/// <paramref name="InternalLinks"/> this is the tile's colour: how much of what it does
/// reaches beyond itself.
/// </param>
public sealed record TreemapTile(
    string Name,
    string Path,
    TreemapTileType Type,
    long Value,
    int Files,
    int Symbols,
    long Bytes,
    int InternalLinks,
    int OutgoingLinks,
    long? SymbolId = null,
    SymbolKind? Kind = null);

/// <summary>One level of the drill-down. The whole tree is never materialised.</summary>
public sealed record TreemapLevel
{
    /// <summary>Workspace-relative path of the level, empty at the root.</summary>
    public required string Path { get; init; }

    public TreemapTileType ChildType { get; init; } = TreemapTileType.Directory;

    public IReadOnlyList<TreemapTile> Tiles { get; init; } = [];

    /// <summary>True when the tile cap dropped the smallest entries.</summary>
    public bool Truncated { get; init; }
}

/// <summary>Which set of facts the dependency wheel is drawn from.</summary>
public enum WheelSource
{
    /// <summary>Resolved <c>#include</c> and <c>import</c> lines, from <c>file_dep</c>.</summary>
    Includes,

    /// <summary>Resolved symbol references, from the edge table.</summary>
    References,
}

/// <summary>
/// One arc of the wheel: a top-level workspace directory.
/// </summary>
/// <param name="Unresolved">
/// Dependencies written in this group that name something not indexed here. Reported as a
/// number rather than drawn as a ribbon, because there is no second end to draw it to.
/// </param>
public sealed record WheelGroup(string Name, int Files, int Links, int Unresolved);

/// <summary>A ribbon: how many links run from one group to another.</summary>
public sealed record WheelLink(string Source, string Target, int Count);

public sealed record DependencyWheel
{
    public required WheelSource Source { get; init; }
    public IReadOnlyList<WheelGroup> Groups { get; init; } = [];
    public IReadOnlyList<WheelLink> Links { get; init; } = [];

    /// <summary>Groups left out by the arc cap, so the picture never silently shrinks.</summary>
    public int OmittedGroups { get; init; }
}
