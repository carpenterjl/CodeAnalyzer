using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

public enum GraphDirection
{
    /// <summary>Symbols that reference the focus.</summary>
    Callers,

    /// <summary>Symbols the focus references.</summary>
    Callees,

    Both,
}

/// <summary>A node in the rendered graph.</summary>
public sealed record GraphNode
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public required string RelativePath { get; init; }
    public required int Line { get; init; }
    public string? Signature { get; init; }

    /// <summary>Literal value for constants, shown as a sub-label.</summary>
    public string? Value { get; init; }

    /// <summary>
    /// The parameter list as written, drawn on the node so two overloads of one method are
    /// distinguishable without a click. Null when the declaration has no parameter list.
    /// </summary>
    public string? ParameterText { get; init; }

    /// <summary>Declared type, stated in the descriptor for symbols that take no parameters.</summary>
    public string? TypeText { get; init; }

    /// <summary>
    /// How many definitions share this name in its own scope, and which of them this is.
    /// Both 1 when the name is not overloaded, which is how the descriptor knows to say
    /// nothing about overloading.
    /// </summary>
    public int OverloadCount { get; init; } = 1;

    public int OverloadOrdinal { get; init; } = 1;

    /// <summary>Verbatim modifier keywords ("public sealed override"), where captured.</summary>
    public string? Modifiers { get; init; }

    /// <summary>
    /// Name of the containing symbol, so a node can say which class owns it without a
    /// click — two files' `Send` methods are indistinguishable by name alone.
    /// </summary>
    public string? ContainerName { get; init; }

    /// <summary>Incoming edge count, so the UI can badge nodes with unexpanded neighbours.</summary>
    public int CallerCount { get; init; }

    public int CalleeCount { get; init; }
}

/// <summary>An edge, carrying the honesty signal for how it was resolved.</summary>
public sealed record GraphEdge
{
    public required long SourceId { get; init; }
    public required long TargetId { get; init; }
    public required ReferenceKind Kind { get; init; }
    public required EdgeConfidence Confidence { get; init; }

    /// <summary>Where the reference appears, so the UI can jump to the call site.</summary>
    public int Line { get; init; }

    /// <summary>How many candidate definitions the name matched.</summary>
    public int CandidateCount { get; init; } = 1;

    /// <summary>
    /// How many distinct references were merged into this edge. One drawn edge stands for
    /// every call site between the pair, and the count is what keeps that honest.
    /// </summary>
    public int CallSiteCount { get; init; } = 1;
}

/// <summary>A bounded slice of the graph. The whole graph is never materialised.</summary>
public sealed record GraphFragment
{
    public long? FocusId { get; init; }
    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];

    /// <summary>
    /// I/O boundary stubs for the drawn nodes. Attached by the shell after the fragment is
    /// built, because matching needs the catalog and the user's marks, which the graph
    /// query deliberately knows nothing about.
    /// </summary>
    public IReadOnlyList<IoStub> IoStubs { get; init; } = [];

    /// <summary>True when the node cap truncated the result, so the UI can say so.</summary>
    public bool WasTruncated { get; init; }
}

/// <summary>Everything the detail pane shows about one symbol. All values are stored facts.</summary>
public sealed record SymbolDetail
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

    /// <summary>The parameter list as written, or null where the declaration has none.</summary>
    public string? ParameterText { get; init; }

    /// <summary>Verbatim modifier keywords in source order, where the pack captured any.</summary>
    public string? Modifiers { get; init; }

    public string ScopePath { get; init; } = string.Empty;

    /// <summary>
    /// Every definition sharing this one's name in its own scope, this one included, in
    /// source order. Empty when the name is not overloaded — one definition is not a set,
    /// and a pane listing a symbol's sole self would say nothing.
    /// </summary>
    public IReadOnlyList<OverloadSibling> Overloads { get; init; } = [];

    /// <summary>Size of the overload set, 1 when the name is not overloaded.</summary>
    public int OverloadCount => Overloads.Count == 0 ? 1 : Overloads.Count;

    /// <summary>This definition's 1-based position in the set.</summary>
    public int OverloadOrdinal
    {
        get
        {
            var index = -1;
            for (var i = 0; i < Overloads.Count; i++)
            {
                if (Overloads[i].IsCurrent)
                {
                    index = i;
                    break;
                }
            }

            return index < 0 ? 1 : index + 1;
        }
    }

    /// <summary>Struct fields, class methods, or Verilog module ports.</summary>
    public IReadOnlyList<SymbolMember> Members { get; init; } = [];

    public IReadOnlyList<RelatedSymbol> Callers { get; init; } = [];
    public IReadOnlyList<RelatedSymbol> Callees { get; init; } = [];

    /// <summary>References from this symbol that matched no definition, e.g. libc calls.</summary>
    public IReadOnlyList<UnresolvedReference> UnresolvedReferences { get; init; } = [];
}

public sealed record SymbolMember(
    long Id,
    string Name,
    SymbolKind Kind,
    string? TypeText,
    string? Value,
    int Line,
    string? Modifiers = null);

/// <summary>
/// One member of an overload set: what it takes, where it is, and whether it is the
/// symbol currently being shown.
/// </summary>
public sealed record OverloadSibling(
    long Id,
    string? Signature,
    string? ParameterText,
    int Line,
    bool IsCurrent);

/// <summary>One call site behind a merged graph edge: where, and what was passed.</summary>
public sealed record EdgeCallSite(int Line, string? ArgumentText, EdgeConfidence Confidence);

public sealed record RelatedSymbol(
    long Id,
    string Name,
    SymbolKind Kind,
    string RelativePath,
    int Line,
    ReferenceKind ReferenceKind,
    EdgeConfidence Confidence);

public sealed record UnresolvedReference(string Name, ReferenceKind Kind, int Line);
