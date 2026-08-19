using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// One resolution candidate a flow step did not follow, kept so a surface can offer the
/// choice instead of hiding the doubt.
/// </summary>
public sealed record FlowCandidate(
    long Id,
    string Name,
    SymbolKind Kind,
    string RelativePath,
    int Line,
    EdgeConfidence Confidence);

/// <summary>
/// One call site in a flow, in the order the caller's body writes it.
/// <para>
/// A step is a <em>site</em>, not a function: a function called twice appears twice, which
/// is what makes the trace read as "and then". The second occurrence of a target whose
/// subtree is already drawn collapses to a reference (<see cref="CollapsedAt"/>) rather
/// than repeating the drawing, and recursion (<see cref="IsCycle"/>) is marked and never
/// re-expanded.
/// </para>
/// </summary>
public sealed record CallFlowStep
{
    public required long RefId { get; init; }

    /// <summary>
    /// Dotted position in the trace ("2.1" = first call inside the second step). Computed
    /// here rather than by renderers so a collapse or cycle can cite the step it refers to.
    /// </summary>
    public required string Ordinal { get; init; }

    /// <summary>The called name as written at the site.</summary>
    public required string Name { get; init; }

    public ReferenceKind Kind { get; init; }

    /// <summary>Verbatim argument list, as stored (parse-time truncation applies).</summary>
    public string? ArgumentText { get; init; }

    public string? ReceiverText { get; init; }

    /// <summary>Call-site position, in the caller's file.</summary>
    public int Line { get; init; }

    public int Col { get; init; }

    public ResultFate? Fate { get; init; }

    public string? FateName { get; init; }

    /// <summary>The followed candidate's definition, null when nothing resolved.</summary>
    public long? TargetId { get; init; }

    public string? TargetName { get; init; }

    public SymbolKind TargetKind { get; init; }

    /// <summary>The target's declaring file — a different file than the call site's, usually.</summary>
    public string? TargetPath { get; init; }

    /// <summary>The target's declaration line, in the target's file.</summary>
    public int TargetLine { get; init; }

    public EdgeConfidence Confidence { get; init; }

    /// <summary>
    /// The candidates not followed, capped. Empty for a unique resolution. All of them are
    /// kept in the index; a surface offering a pick re-traces with the pin.
    /// </summary>
    public IReadOnlyList<FlowCandidate> OtherCandidates { get; init; } = [];

    /// <summary>No edge row at all — distinct from ambiguous, and an explicit leaf.</summary>
    public bool IsUnresolved { get; init; }

    /// <summary>The target is on the active call stack; expanding it would never end.</summary>
    public bool IsCycle { get; init; }

    /// <summary>
    /// Ordinal of the step where this target's subtree is fully drawn; this occurrence is
    /// a reference to it. Null when this is the drawing.
    /// </summary>
    public string? CollapsedAt { get; init; }

    /// <summary>The call crosses an I/O boundary — a terminal in the picture.</summary>
    public bool IsIoBoundary { get; init; }

    public IoDirection IoDirection { get; init; }

    /// <summary>Catalog family (".NET Console"); null for a user mark.</summary>
    public string? IoFamily { get; init; }

    /// <summary>
    /// Expansion below this step was cut — by depth, by the per-node listing cap, or by
    /// the whole-flow budget. Never silent: <see cref="CallSitesInBody"/> says what the
    /// target's body actually holds.
    /// </summary>
    public bool ChildrenTruncated { get; init; }

    /// <summary>
    /// Total call sites in the target's body when expansion was cut; 0 when the step is
    /// fully expanded (or has nothing to expand).
    /// </summary>
    public int CallSitesInBody { get; init; }

    public IReadOnlyList<CallFlowStep> Children { get; init; } = [];
}

/// <summary>
/// A depth-first trace of every call a symbol makes, in source order, transitively.
/// <para>
/// Source order is the honest limit stated up front: the index holds no branch or loop
/// facts, so this is the order the calls are written, not a claim about the order they
/// run. Both arms of an if appear, one after the other.
/// </para>
/// </summary>
public sealed record CallFlow
{
    public required long RootId { get; init; }

    /// <summary>False when the id names no definition — the answer is about the question.</summary>
    public bool RootExists { get; init; }

    public string? RootName { get; init; }

    public SymbolKind RootKind { get; init; }

    public string? RootPath { get; init; }

    public int RootLine { get; init; }

    public IReadOnlyList<CallFlowStep> Steps { get; init; } = [];

    /// <summary>Steps actually built, across the whole tree.</summary>
    public int TotalSteps { get; init; }

    /// <summary>The expansion depth used, after clamping.</summary>
    public int DepthUsed { get; init; }

    /// <summary>
    /// Root-level truncation: the root's own listing was cut by the per-node cap or the
    /// budget. Per-step cuts ride on the steps themselves.
    /// </summary>
    public bool RootTruncated { get; init; }

    /// <summary>Total call sites in the root's body when <see cref="RootTruncated"/>.</summary>
    public int RootCallSites { get; init; }

    /// <summary>Any cap fired anywhere in the flow. A surface must word this, never drop it.</summary>
    public bool WasTruncated { get; init; }
}
