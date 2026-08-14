using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// One member of a composite symbol: a struct field, a class method, a Verilog port,
/// an enum constant.
/// </summary>
/// <param name="MemberCount">Members of its own, so a nested struct can be drilled into.</param>
/// <param name="ReferenceCount">
/// How many resolved references point at this member. A field nothing reads is a fact
/// worth seeing, and so is one read from thirty places.
/// </param>
public sealed record CompositionMember(
    long Id,
    string Name,
    SymbolKind Kind,
    string? TypeText,
    string? Value,
    int Line,
    int MemberCount,
    int ReferenceCount,
    string? Modifiers = null);

/// <summary>
/// A relation read straight off a reference: an instantiation, a base type, a subclass.
/// <para>
/// <paramref name="TargetId"/> is null when the name resolved to nothing in the workspace.
/// The relation is still reported, because "instantiates <c>fifo</c>, which is not indexed
/// here" is a fact and dropping it would quietly shrink the picture.
/// </para>
/// </summary>
public sealed record CompositionLink(
    long? TargetId,
    string Name,
    SymbolKind? Kind,
    string? RelativePath,
    int Line,
    ReferenceKind ReferenceKind,
    EdgeConfidence? Confidence);

/// <summary>
/// What a composite symbol is made of, and how it sits among its neighbours by type.
/// Every list is read from the index; nothing is inferred.
/// </summary>
public sealed record CompositionView
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public required string RelativePath { get; init; }
    public required string Language { get; init; }
    public required int StartLine { get; init; }
    public int EndLine { get; init; }
    public string? Signature { get; init; }
    public string? Value { get; init; }
    public string? TypeText { get; init; }

    /// <summary>Verbatim modifier keywords in source order, where the pack captured any.</summary>
    public string? Modifiers { get; init; }

    public string ScopePath { get; init; } = string.Empty;

    /// <summary>The symbol that contains this one, when it is itself a member.</summary>
    public long? ContainerId { get; init; }

    public string? ContainerName { get; init; }

    public IReadOnlyList<CompositionMember> Members { get; init; } = [];

    /// <summary>Modules instantiated, or types constructed, by this symbol or its members.</summary>
    public IReadOnlyList<CompositionLink> Instantiates { get; init; } = [];

    public IReadOnlyList<CompositionLink> InstantiatedBy { get; init; } = [];

    /// <summary>Base classes and implemented interfaces.</summary>
    public IReadOnlyList<CompositionLink> BaseTypes { get; init; } = [];

    public IReadOnlyList<CompositionLink> DerivedTypes { get; init; } = [];
}
