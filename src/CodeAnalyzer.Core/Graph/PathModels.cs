using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

/// <summary>A symbol appearing somewhere on a traced route.</summary>
public sealed record PathNode(
    long Id,
    string Name,
    SymbolKind Kind,
    string RelativePath,
    int Line);

/// <summary>One hop of a route, carrying how far the resolver trusted it.</summary>
public sealed record PathLink(
    long SourceId,
    long TargetId,
    ReferenceKind Kind,
    EdgeConfidence Confidence,
    int Line);

/// <summary>
/// Every shortest route from one symbol to another.
/// <para>
/// Shortest, not all: the number of distinct routes between two symbols in a real call
/// graph grows exponentially with distance, so enumerating them all would either hang or
/// have to be cut at an arbitrary point without saying so. The search is bidirectional and
/// stops at the first depth where the two sides meet, which yields every route of that
/// length. <see cref="Truncated"/> says when even that had to be cut.
/// </para>
/// </summary>
public sealed record PathTrace
{
    public required long FromId { get; init; }
    public required long ToId { get; init; }

    /// <summary>False when the endpoint is no longer in the index.</summary>
    public bool FromExists { get; init; }

    public bool ToExists { get; init; }

    public IReadOnlyList<PathNode> Nodes { get; init; } = [];
    public IReadOnlyList<PathLink> Links { get; init; } = [];

    /// <summary>Each route as an ordered run of symbol ids, starting at From and ending at To.</summary>
    public IReadOnlyList<IReadOnlyList<long>> Routes { get; init; } = [];

    /// <summary>Hops in each route. Zero when the two endpoints are the same symbol.</summary>
    public int Length { get; init; }

    /// <summary>
    /// True when the search hit its depth or node budget before it could rule a connection
    /// out. With no routes, this is the difference between "there is no path" and "we
    /// stopped looking" — and the UI must not report the second as the first.
    /// </summary>
    public bool SearchExhausted { get; init; }

    /// <summary>True when more routes exist than the cap allowed us to return.</summary>
    public bool Truncated { get; init; }
}
