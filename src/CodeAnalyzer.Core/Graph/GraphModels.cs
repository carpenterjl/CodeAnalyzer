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

    /// <summary>
    /// The comment written immediately above the declaration, punctuation stripped and lines
    /// joined, or null where there is none. On the fact sheet unconditionally, because a
    /// fact sheet's job is to save opening the file and what the author wrote about a symbol
    /// is the part of it no signature can carry.
    /// </summary>
    public string? DocComment { get; init; }

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

    /// <summary>
    /// How many distinct callers and callees exist, as against how many are listed. Equal
    /// to the list length whenever nothing was cut.
    /// <para>
    /// Carried because the cap was announced without a size. A field report on another
    /// codebase read "… list capped at 100 per direction" and could not tell 101 from 1,010
    /// without leaving the tool for grep — and "how many" was the question that report's
    /// predecessor had got wrong.
    /// </para>
    /// <para>
    /// It also fixes a way the cap could bite in silence. The LIMIT applies to rows, and
    /// rows are then collapsed to one entry per caller and reference kind, so a symbol
    /// whose callers reference it repeatedly could lose entries while the list came back
    /// shorter than the cap — and the only test for truncation was list length against the
    /// cap. Counting the distinct entries in SQL asks the question the list cannot answer
    /// about itself.
    /// </para>
    /// </summary>
    public int CallerTotal { get; init; }

    /// <inheritdoc cref="CallerTotal"/>
    public int CalleeTotal { get; init; }

    /// <summary>
    /// How many places call or reference this symbol's <em>members</em>, as distinct from
    /// this symbol itself.
    /// <para>
    /// A container's caller count is not the sum of its members', and the difference is
    /// load-bearing rather than pedantic: <c>PrimitiveFactory</c> is a static class nobody
    /// names, whose two factory methods the application calls, and <c>callers: 0</c> on the
    /// class read as "this is dead". A field report acted on exactly that shape three times
    /// in one session — correctly, on three genuinely unreachable features — which is what
    /// made the fourth reading so expensive. The count stays what it is; this number is what
    /// lets the sheet say so out loud.
    /// </para>
    /// </summary>
    public int MemberCallerTotal { get; init; }

    /// <summary>References from this symbol that matched no definition, e.g. libc calls.</summary>
    public IReadOnlyList<UnresolvedReference> UnresolvedReferences { get; init; } = [];

    /// <summary>
    /// What this type's declaration says it derives from, one entry per name in the base
    /// list as written — resolved to a workspace type where an inherit edge exists, verbatim
    /// where the base lives outside the workspace. Both belong on the fact sheet: the base
    /// list is a stored fact either way, and hiding the unresolved half would misreport
    /// <c>class Foo : IDisposable</c> as a type that derives from nothing.
    /// </summary>
    public IReadOnlyList<BaseTypeFact> BaseTypes { get; init; } = [];

    /// <summary>Workspace types whose inherit reference resolved to this one.</summary>
    public IReadOnlyList<RelatedSymbol> DerivedTypes { get; init; } = [];
}

/// <summary>One name in a type's base list; located when it resolved, verbatim when not.</summary>
public sealed record BaseTypeFact(
    string Name,
    long? TargetId,
    string? TargetPath,
    int? TargetLine);

public sealed record SymbolMember(
    long Id,
    string Name,
    SymbolKind Kind,
    string? TypeText,
    string? Value,
    int Line,
    string? Modifiers = null,
    string? ParameterText = null);

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

/// <summary>
/// One call site behind a merged graph edge: where, what was passed, and — when the call
/// was written against a receiver — the receiver verbatim. The receiver is what lets a
/// reader audit the confidence marker: <c>orchestrator.IndexAsync(…)</c> wears the reason
/// its edge is a name match rather than an exact one.
/// <para>
/// <see cref="Name"/> is the name as <em>written</em> at the site, which is not always the
/// matched symbol's: a XAML <c>x:Class</c> reference is written short and resolves to a
/// qualified declaration. Carrying it here rather than letting each consumer substitute the
/// symbol's name keeps every rendering of a site the source text and nothing else — and
/// gives the receiver's dot something to attach to, which a reference with no arguments
/// otherwise lacks.
/// </para>
/// </summary>
public sealed record EdgeCallSite(
    int Line,
    string? ArgumentText,
    EdgeConfidence Confidence,
    string? ReceiverText = null,
    string? Name = null)
{
    /// <summary>
    /// The site rebuilt as the source wrote it, for the renderings that show one line of
    /// text. <paramref name="fallbackName"/> covers rows stored before the name travelled
    /// with the site.
    /// <para>
    /// Markup extensions are the exception, and the reason this lives in one place rather
    /// than in each formatter: for a call, <see cref="ArgumentText"/> is the argument list
    /// and the name goes in front of it, but M19.3 stores a whole <c>{StaticResource
    /// SearchBox}</c> verbatim in that field, because the extension <em>is</em> the
    /// reference rather than an argument to one. Prefixing the name there says
    /// <c>SearchBox{StaticResource SearchBox}</c>.
    /// </para>
    /// </summary>
    public string SourceText(ReferenceKind kind, string? fallbackName = null)
    {
        if (kind is ReferenceKind.Binding or ReferenceKind.Resource)
        {
            return ArgumentText ?? Name ?? fallbackName ?? string.Empty;
        }

        var receiver = ReceiverText is { Length: > 0 } r ? r + "." : string.Empty;
        return receiver + (Name ?? fallbackName) + ArgumentText;
    }
}

/// <param name="Line">
/// A line in <paramref name="RelativePath"/> — which is the invariant, and which is why the
/// two directions read different columns to satisfy it. On a caller the pair is the call
/// site — the file is the caller's and the line is where inside it the reference is written,
/// the FIRST such line when there are several (a quarter of caller rows have more than one).
/// On a callee the file is the target's, so the pair is the target's declaration; where the
/// call was written belongs to the symbol being asked about, and comes back with
/// <c>include_sites</c>. Reading the reference's line for both is what made a callee row
/// point at a line the file it named did not have.
/// </param>
public sealed record RelatedSymbol(
    long Id,
    string Name,
    SymbolKind Kind,
    string RelativePath,
    int Line,
    ReferenceKind ReferenceKind,
    EdgeConfidence Confidence);

public sealed record UnresolvedReference(string Name, ReferenceKind Kind, int Line);
