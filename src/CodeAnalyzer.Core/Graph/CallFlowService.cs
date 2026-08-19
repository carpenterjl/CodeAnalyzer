using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// Builds the depth-first call trace the flow view and the <c>flow</c> verb draw.
/// <para>
/// Traversal is per-node seeks on <c>ix_ref_from</c> in C# with an explicit stack — the
/// same posture as <see cref="GraphQueryService.GetNeighbourhood"/>, and for the same
/// reason: a recursive CTE can touch the whole edge table before returning anything.
/// Everything is capped, and every cap that fires is stated on the result.
/// </para>
/// <para>
/// The call-site filter is <c>ref.fate IS NOT NULL</c>: the analyzer stamps a fate on
/// exactly the references that are call sites (calls, instantiations, C# constructions)
/// and on nothing else, so the column doubles as the "is a call" predicate.
/// </para>
/// <para>
/// Callers must hold the connection's <c>DatabaseGate</c>; this class, like the other
/// query services, does not lock for itself.
/// </para>
/// </summary>
public sealed class CallFlowService(SqliteConnection connection)
{
    /// <summary>Expansion depth when the caller does not choose one.</summary>
    public int DefaultDepth { get; init; } = 3;

    /// <summary>Hard ceiling on the requested depth.</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>Whole-flow step budget, across every level.</summary>
    public int MaxSteps { get; init; } = 400;

    /// <summary>Listed call sites per expanded body; the rest are counted, not dropped silently.</summary>
    public int MaxChildrenPerNode { get; init; } = 64;

    /// <summary>Candidates carried per ambiguous step, beyond the followed one.</summary>
    public int MaxCandidatesPerStep { get; init; } = 5;

    /// <summary>
    /// Traces every call <paramref name="rootId"/> makes, in source order, transitively.
    /// </summary>
    /// <param name="rootId">The symbol to trace from.</param>
    /// <param name="depth">Levels to expand below the root; clamped to [1, MaxDepth].</param>
    /// <param name="io">
    /// When given (with catalog and marks), steps that cross an I/O boundary are stamped —
    /// one pass over the expanded callers, so every surface answers identically.
    /// </param>
    /// <param name="catalog">The I/O catalog to match against.</param>
    /// <param name="marks">User I/O marks.</param>
    /// <param name="pins">
    /// Per-reference candidate choices (ref id → target id). A pinned step follows its
    /// pin instead of the strongest candidate; the pin is a session concern, never stored.
    /// </param>
    /// <param name="activeAncestors">
    /// Symbol ids already on the caller's stack when this trace roots a subtree of a
    /// larger flow — what keeps a deepened branch from re-expanding its own ancestors.
    /// </param>
    public CallFlow GetCallFlow(
        long rootId,
        int? depth = null,
        IoBoundaryService? io = null,
        IReadOnlyList<IoCatalogEntry>? catalog = null,
        IReadOnlyList<IoMark>? marks = null,
        IReadOnlyDictionary<long, long>? pins = null,
        IReadOnlyCollection<long>? activeAncestors = null,
        CancellationToken cancellationToken = default)
    {
        var levels = Math.Clamp(depth ?? DefaultDepth, 1, MaxDepth);

        var root = LoadRoot(rootId);
        if (root is null)
        {
            return new CallFlow { RootId = rootId, RootExists = false };
        }

        var state = new TraverseState(pins, cancellationToken) { Budget = MaxSteps };
        var active = new HashSet<long>(activeAncestors ?? []) { rootId };

        var (steps, rootCut, rootTotal) = Expand(rootId, levels, prefix: "", active, state);

        if (io is not null && state.ExpandedCallers.Count > 0)
        {
            var sites = io.GetSitesForCallers(
                state.ExpandedCallers, catalog ?? [], marks ?? [], cancellationToken);
            if (sites.Count > 0)
            {
                var byRef = new Dictionary<long, IoBoundarySite>();
                foreach (var site in sites)
                {
                    byRef.TryAdd(site.RefId, site);
                }

                steps = Stamp(steps, byRef);
            }
        }

        return new CallFlow
        {
            RootId = rootId,
            RootExists = true,
            RootName = root.Value.Name,
            RootKind = root.Value.Kind,
            RootPath = root.Value.RelativePath,
            RootLine = root.Value.Line,
            Steps = steps,
            TotalSteps = state.Created,
            DepthUsed = levels,
            RootTruncated = rootCut,
            RootCallSites = rootTotal,
            WasTruncated = state.Truncated,
        };
    }

    private sealed class TraverseState(
        IReadOnlyDictionary<long, long>? pins, CancellationToken cancellationToken)
    {
        public IReadOnlyDictionary<long, long>? Pins { get; } = pins;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int Budget;
        public int Created;
        public bool Truncated;

        /// <summary>Target → ordinal of its one full drawing, for collapse references.</summary>
        public Dictionary<long, string> ExpandedAt { get; } = [];

        /// <summary>Every symbol whose body was listed — the I/O pass drives from these.</summary>
        public HashSet<long> ExpandedCallers { get; } = [];
    }

    /// <summary>
    /// Lists one body's call sites and expands each, depth-first. Returns the steps, plus
    /// whether the listing itself was cut and what the body actually holds when it was.
    /// </summary>
    private (List<CallFlowStep> Steps, bool Cut, int Total) Expand(
        long symbolId, int levels, string prefix, HashSet<long> active, TraverseState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        state.ExpandedCallers.Add(symbolId);

        var (groups, total) = LoadCallSites(symbolId);
        var steps = new List<CallFlowStep>();
        var cut = groups.Count < total;

        foreach (var group in groups)
        {
            if (state.Budget <= 0)
            {
                cut = true;
                break;
            }

            state.Budget--;
            state.Created++;

            var ordinal = prefix.Length == 0
                ? (steps.Count + 1).ToString()
                : $"{prefix}.{steps.Count + 1}";

            steps.Add(BuildStep(group, ordinal, levels, active, state));
        }

        if (cut)
        {
            state.Truncated = true;
        }

        return (steps, cut, cut ? total : 0);
    }

    private CallFlowStep BuildStep(
        RefGroup group, string ordinal, int levels, HashSet<long> active, TraverseState state)
    {
        var chosen = Choose(group, state.Pins);

        var isCycle = chosen is not null && active.Contains(chosen.TargetId);
        string? collapsedAt = null;
        List<CallFlowStep> children = [];
        var childCut = false;
        var childTotal = 0;

        if (chosen is not null && !isCycle)
        {
            if (state.ExpandedAt.TryGetValue(chosen.TargetId, out var firstOrdinal))
            {
                collapsedAt = firstOrdinal;
            }
            else if (levels <= 1 || state.Budget <= 0)
            {
                childTotal = CountCallSites(chosen.TargetId);
                childCut = childTotal > 0;
                if (childCut)
                {
                    state.Truncated = true;
                }
            }
            else
            {
                active.Add(chosen.TargetId);
                (children, childCut, childTotal) =
                    Expand(chosen.TargetId, levels - 1, ordinal, active, state);
                active.Remove(chosen.TargetId);

                // Only a full drawing registers: a later occurrence must not collapse to
                // a subtree that was itself cut by depth or budget.
                if (!childCut)
                {
                    state.ExpandedAt[chosen.TargetId] = ordinal;
                }
            }
        }

        var others = new List<FlowCandidate>();
        foreach (var candidate in group.Candidates)
        {
            if (candidate == chosen || others.Count >= MaxCandidatesPerStep)
            {
                continue;
            }

            others.Add(new FlowCandidate(
                candidate.TargetId, candidate.Name, candidate.Kind,
                candidate.RelativePath, candidate.Line, candidate.Confidence));
        }

        return new CallFlowStep
        {
            RefId = group.RefId,
            Ordinal = ordinal,
            Name = group.Name,
            Kind = group.Kind,
            ArgumentText = group.ArgumentText,
            ReceiverText = group.ReceiverText,
            Line = group.Line,
            Col = group.Col,
            Fate = group.Fate,
            FateName = group.FateName,
            TargetId = chosen?.TargetId,
            TargetName = chosen?.Name,
            TargetKind = chosen?.Kind ?? SymbolKind.Unknown,
            TargetPath = chosen?.RelativePath,
            TargetLine = chosen?.Line ?? 0,
            Confidence = chosen?.Confidence ?? EdgeConfidence.Unique,
            OtherCandidates = others,
            IsUnresolved = chosen is null,
            IsCycle = isCycle,
            CollapsedAt = collapsedAt,
            ChildrenTruncated = childCut,
            CallSitesInBody = childTotal,
            Children = children,
        };
    }

    /// <summary>The pinned candidate when one is pinned and still exists; else the strongest.</summary>
    private static CandidateRow? Choose(RefGroup group, IReadOnlyDictionary<long, long>? pins)
    {
        if (group.Candidates.Count == 0)
        {
            return null;
        }

        if (pins is not null && pins.TryGetValue(group.RefId, out var pinnedId))
        {
            foreach (var candidate in group.Candidates)
            {
                if (candidate.TargetId == pinnedId)
                {
                    return candidate;
                }
            }
        }

        return group.Candidates[0];
    }

    private sealed record CandidateRow(
        long TargetId, EdgeConfidence Confidence, string Name, SymbolKind Kind,
        int Line, string RelativePath);

    private sealed class RefGroup
    {
        public long RefId;
        public required string Name;
        public ReferenceKind Kind;
        public string? ArgumentText;
        public string? ReceiverText;
        public int Line;
        public int Col;
        public ResultFate? Fate;
        public string? FateName;
        public List<CandidateRow> Candidates { get; } = [];
    }

    /// <summary>
    /// One body's call sites in source order — never by ref id, which is tree-sitter match
    /// order — with every resolution candidate riding along, strongest first.
    /// </summary>
    private (List<RefGroup> Groups, int Total) LoadCallSites(long symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.name, r.kind, r.arg_text, r.receiver_text, r.line, r.col,
                   r.fate, r.fate_name,
                   e.target_symbol_id, e.confidence, s.name, s.kind, s.start_line, f.rel_path
            FROM ref r
            LEFT JOIN edge e ON e.ref_id = r.id
            LEFT JOIN symbol s ON s.id = e.target_symbol_id
            LEFT JOIN file f ON f.id = s.file_id
            WHERE r.from_symbol_id = $symbolId AND r.fate IS NOT NULL
            ORDER BY r.line, r.col, r.id, e.confidence, e.target_symbol_id
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);

        var groups = new List<RefGroup>();
        RefGroup? current = null;
        var total = 0;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var refId = reader.GetInt64(0);
            if (current is null || current.RefId != refId)
            {
                total++;
                if (groups.Count >= MaxChildrenPerNode)
                {
                    current = null;
                    continue;
                }

                current = new RefGroup
                {
                    RefId = refId,
                    Name = reader.GetString(1),
                    Kind = (ReferenceKind)reader.GetInt32(2),
                    ArgumentText = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ReceiverText = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Line = reader.GetInt32(5),
                    Col = reader.GetInt32(6),
                    Fate = reader.IsDBNull(7) ? null : (ResultFate)reader.GetInt32(7),
                    FateName = reader.IsDBNull(8) ? null : reader.GetString(8),
                };
                groups.Add(current);
            }

            if (current is not null && !reader.IsDBNull(9))
            {
                current.Candidates.Add(new CandidateRow(
                    reader.GetInt64(9),
                    (EdgeConfidence)reader.GetInt32(10),
                    reader.IsDBNull(11) ? current.Name : reader.GetString(11),
                    reader.IsDBNull(12) ? SymbolKind.Unknown : (SymbolKind)reader.GetInt32(12),
                    reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                    reader.IsDBNull(14) ? string.Empty : reader.GetString(14)));
            }
        }

        return (groups, total);
    }

    private int CountCallSites(long symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM ref WHERE from_symbol_id = $symbolId AND fate IS NOT NULL";
        command.Parameters.AddWithValue("$symbolId", symbolId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private (string Name, SymbolKind Kind, string RelativePath, int Line)? LoadRoot(long symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.name, s.kind, f.rel_path, s.start_line
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            WHERE s.id = $symbolId AND s.is_definition = 1
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), (SymbolKind)reader.GetInt32(1),
            reader.GetString(2), reader.GetInt32(3));
    }

    private static List<CallFlowStep> Stamp(
        List<CallFlowStep> steps, Dictionary<long, IoBoundarySite> byRef)
    {
        var stamped = new List<CallFlowStep>(steps.Count);
        foreach (var step in steps)
        {
            var next = step;
            if (byRef.TryGetValue(step.RefId, out var site))
            {
                next = next with
                {
                    IsIoBoundary = true,
                    IoDirection = site.Direction,
                    IoFamily = site.Family,
                };
            }

            if (next.Children.Count > 0)
            {
                next = next with { Children = Stamp([.. next.Children], byRef) };
            }

            stamped.Add(next);
        }

        return stamped;
    }
}
