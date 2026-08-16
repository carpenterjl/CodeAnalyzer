using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// Reads bounded slices of the dependency graph.
/// <para>
/// Every query is capped. Neighbourhoods expand level by level in C# with a visited set
/// rather than through a recursive CTE, because on a large workspace an unbounded
/// traversal can touch the entire edge table before returning anything.
/// </para>
/// </summary>
public sealed class GraphQueryService(SqliteConnection connection)
{
    /// <summary>Maximum neighbours returned per direction for a single node.</summary>
    public int NeighboursPerDirection { get; init; } = 25;

    /// <summary>Maximum nodes in one fragment, across all expansion levels.</summary>
    public int MaxNodes { get; init; } = 300;

    /// <summary>
    /// Cap on the callers/callees lists in <see cref="GetDetail"/>. Exposed so surfaces
    /// that render those lists (the CLI, the markdown report) can word the cap instead
    /// of presenting a capped list as complete.
    /// </summary>
    public int RelatedLimit => NeighboursPerDirection * 4;

    /// <summary>
    /// Returns the focus symbol together with its neighbours out to <paramref name="depth"/>.
    /// </summary>
    public GraphFragment GetNeighbourhood(
        long symbolId,
        GraphDirection direction = GraphDirection.Both,
        int depth = 1,
        CancellationToken cancellationToken = default)
    {
        var nodes = new Dictionary<long, GraphNode>();
        var edges = new HashSet<(long Source, long Target, ReferenceKind Kind)>();
        var edgeList = new List<GraphEdge>();

        var focus = LoadNode(symbolId);
        if (focus is null)
        {
            return new GraphFragment { FocusId = symbolId };
        }

        nodes[symbolId] = focus;

        var frontier = new List<long> { symbolId };
        var visited = new HashSet<long> { symbolId };
        var truncated = false;

        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var nextFrontier = new List<long>();

            foreach (var current in frontier)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (nodes.Count >= MaxNodes)
                {
                    truncated = true;
                    break;
                }

                foreach (var edge in LoadEdges(current, direction))
                {
                    if (!edges.Add((edge.SourceId, edge.TargetId, edge.Kind)))
                    {
                        continue;
                    }

                    edgeList.Add(edge);

                    var other = edge.SourceId == current ? edge.TargetId : edge.SourceId;
                    if (!nodes.ContainsKey(other))
                    {
                        if (nodes.Count >= MaxNodes)
                        {
                            truncated = true;
                            break;
                        }

                        var node = LoadNode(other);
                        if (node is not null)
                        {
                            nodes[other] = node;
                        }
                    }

                    if (visited.Add(other))
                    {
                        nextFrontier.Add(other);
                    }
                }
            }

            frontier = nextFrontier;
        }

        // Drop edges whose endpoint was cut by the node cap, so the view never
        // references a node it was not given.
        var usable = edgeList.Where(e => nodes.ContainsKey(e.SourceId) && nodes.ContainsKey(e.TargetId)).ToList();

        return new GraphFragment
        {
            FocusId = symbolId,
            Nodes = nodes.Values.ToList(),
            Edges = usable,
            WasTruncated = truncated,
        };
    }

    private List<GraphEdge> LoadEdges(long symbolId, GraphDirection direction)
    {
        var results = new List<GraphEdge>();

        if (direction is GraphDirection.Callees or GraphDirection.Both)
        {
            results.AddRange(Query(CalleeSql, symbolId, calleeDirection: true));
        }

        if (direction is GraphDirection.Callers or GraphDirection.Both)
        {
            results.AddRange(Query(CallerSql, symbolId, calleeDirection: false));
        }

        return results;
    }

    // Outgoing: references made by this symbol, joined to what they resolved to.
    // Ordered so that repeated expansion of the same node is idempotent: without it the
    // LIMIT would pick an arbitrary subset and the fragment would shift between calls.
    //
    // `t.container_id IS NOT $symbolId` drops the container-to-member edge. A function
    // referencing its own local, or a Verilog module referencing its own port, is a real
    // reference, but as a graph link it is noise: those names are already listed in full
    // under the symbol's own members, and drawing them again buries the calls that leave
    // the symbol. The rule is applied identically in both directions and in the stored
    // totals below, because a mismatch would make an expand badge offer nothing.
    private const string CalleeSql = """
        SELECT e.target_symbol_id, r.kind, MAX(e.confidence), MIN(r.line),
               MAX((SELECT COUNT(*) FROM edge e2 WHERE e2.ref_id = r.id)) AS candidates,
               COUNT(DISTINCT r.id) AS call_sites
        FROM ref r
        JOIN edge e ON e.ref_id = r.id
        JOIN symbol t ON t.id = e.target_symbol_id
        WHERE r.from_symbol_id = $symbolId
          AND e.target_symbol_id <> $symbolId
          AND t.container_id IS NOT $symbolId
        GROUP BY e.target_symbol_id, r.kind
        ORDER BY e.target_symbol_id
        LIMIT $limit
        """;

    // Incoming: references that resolved to this symbol, attributed to their caller.
    private const string CallerSql = """
        SELECT r.from_symbol_id, r.kind, MAX(e.confidence), MIN(r.line),
               MAX((SELECT COUNT(*) FROM edge e2 WHERE e2.ref_id = r.id)) AS candidates,
               COUNT(DISTINCT r.id) AS call_sites
        FROM edge e
        JOIN ref r ON r.id = e.ref_id
        WHERE e.target_symbol_id = $symbolId
          AND r.from_symbol_id IS NOT NULL
          AND r.from_symbol_id <> $symbolId
          AND r.from_symbol_id IS NOT (SELECT container_id FROM symbol WHERE id = $symbolId)
        GROUP BY r.from_symbol_id, r.kind
        ORDER BY r.from_symbol_id
        LIMIT $limit
        """;

    private List<GraphEdge> Query(string sql, long symbolId, bool calleeDirection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$limit", NeighboursPerDirection);

        var results = new List<GraphEdge>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var other = reader.GetInt64(0);

            results.Add(new GraphEdge
            {
                SourceId = calleeDirection ? symbolId : other,
                TargetId = calleeDirection ? other : symbolId,
                Kind = (ReferenceKind)reader.GetInt32(1),
                Confidence = (EdgeConfidence)reader.GetInt32(2),
                Line = reader.GetInt32(3),
                CandidateCount = reader.GetInt32(4),
                CallSiteCount = reader.GetInt32(5),
            });
        }

        return results;
    }

    private GraphNode? LoadNode(long symbolId)
    {
        using var command = connection.CreateCommand();
        // The two counts must use exactly the key that LoadEdges groups by, and repeat its
        // filters. They are subtracted from what the fragment shows to produce the "+N more"
        // badges, so any mismatch would advertise neighbours that expanding cannot produce.
        command.CommandText = $"""
            SELECT s.id, s.name, s.kind, f.rel_path, s.start_line, s.signature, s.value,
                   s.modifiers, c.name AS container_name, s.param_text, s.type_text,
                   ({OverloadSql.Count}) AS overload_count,
                   ({OverloadSql.Ordinal}) AS overload_ordinal,
                   (SELECT COUNT(*) FROM (
                        SELECT DISTINCT r.from_symbol_id, r.kind
                        FROM edge e JOIN ref r ON r.id = e.ref_id
                        WHERE e.target_symbol_id = s.id
                          AND r.from_symbol_id IS NOT NULL
                          AND r.from_symbol_id <> s.id
                          AND r.from_symbol_id IS NOT s.container_id)) AS callers,
                   (SELECT COUNT(*) FROM (
                        SELECT DISTINCT e.target_symbol_id, r.kind
                        FROM ref r JOIN edge e ON e.ref_id = r.id
                        JOIN symbol t ON t.id = e.target_symbol_id
                        WHERE r.from_symbol_id = s.id
                          AND e.target_symbol_id <> s.id
                          AND t.container_id IS NOT s.id)) AS callees
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            LEFT JOIN symbol c ON c.id = s.container_id
            WHERE s.id = $symbolId
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new GraphNode
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Kind = (SymbolKind)reader.GetInt32(2),
            RelativePath = reader.GetString(3),
            Line = reader.GetInt32(4),
            Signature = reader.IsDBNull(5) ? null : reader.GetString(5),
            Value = reader.IsDBNull(6) ? null : reader.GetString(6),
            Modifiers = reader.IsDBNull(7) ? null : reader.GetString(7),
            ContainerName = reader.IsDBNull(8) ? null : reader.GetString(8),
            ParameterText = reader.IsDBNull(9) ? null : reader.GetString(9),
            TypeText = reader.IsDBNull(10) ? null : reader.GetString(10),
            OverloadCount = reader.GetInt32(11),
            OverloadOrdinal = reader.GetInt32(12),
            CallerCount = reader.GetInt32(13),
            CalleeCount = reader.GetInt32(14),
        };
    }

    /// <summary>
    /// Finds a symbol again by where it lives, or null if nothing there matches.
    /// <para>
    /// Re-parsing a file deletes its symbol rows and inserts new ones, so every id the UI is
    /// holding for that file goes stale the moment a live update lands. Name and file survive
    /// the edit; the line does not, so it only breaks ties — the nearest one to where the
    /// symbol used to be is the one the user was looking at.
    /// </para>
    /// </summary>
    public long? FindDefinitionId(string relativePath, string name, int nearLine)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            WHERE f.rel_path = $relPath AND s.name = $name AND s.is_definition = 1
            ORDER BY ABS(s.start_line - $nearLine)
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$relPath", relativePath);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$nearLine", nearLine);

        return command.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>Loads the full fact sheet for the detail pane.</summary>
    public SymbolDetail? GetDetail(long symbolId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.kind, f.rel_path, s.language, s.start_line, s.end_line,
                   s.signature, s.value, s.type_text, s.scope_path, s.modifiers, s.param_text
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            WHERE s.id = $symbolId
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);

        SymbolDetail detail;

        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            detail = new SymbolDetail
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Kind = (SymbolKind)reader.GetInt32(2),
                RelativePath = reader.GetString(3),
                Language = reader.GetString(4),
                StartLine = reader.GetInt32(5),
                EndLine = reader.GetInt32(6),
                Signature = reader.IsDBNull(7) ? null : reader.GetString(7),
                Value = reader.IsDBNull(8) ? null : reader.GetString(8),
                TypeText = reader.IsDBNull(9) ? null : reader.GetString(9),
                ScopePath = reader.GetString(10),
                Modifiers = reader.IsDBNull(11) ? null : reader.GetString(11),
                ParameterText = reader.IsDBNull(12) ? null : reader.GetString(12),
            };
        }

        var callers = LoadRelated(symbolId, callers: true);

        return detail with
        {
            Overloads = LoadOverloads(symbolId),
            Members = LoadMembers(symbolId),
            Callers = callers,
            Callees = LoadRelated(symbolId, callers: false),
            UnresolvedReferences = LoadUnresolved(symbolId),
            BaseTypes = LoadBaseTypes(symbolId),
            // Already loaded — a derived type is a caller whose reference is an inherit.
            // Filtered rather than re-queried so the sheet and the caller list agree.
            DerivedTypes = callers.Where(c => c.ReferenceKind == ReferenceKind.Inherit).ToList(),
        };
    }

    /// <summary>
    /// The base list as written on this type's declaration: every inherit reference it
    /// makes, located when it resolved, name-only when the base is not in the workspace.
    /// The unresolved half is the reason this is not just the callee list filtered —
    /// <c>IDisposable</c> has no edge and still belongs on the fact sheet.
    /// </summary>
    private List<BaseTypeFact> LoadBaseTypes(long symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.name, s.id, f.rel_path, s.start_line
            FROM ref r
            LEFT JOIN edge e ON e.ref_id = r.id
            LEFT JOIN symbol s ON s.id = e.target_symbol_id
            LEFT JOIN file f ON f.id = s.file_id
            WHERE r.from_symbol_id = $symbolId AND r.kind = $inherit
            GROUP BY r.id
            ORDER BY r.line, r.col
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$inherit", (int)ReferenceKind.Inherit);

        var results = new List<BaseTypeFact>();
        var seen = new HashSet<string>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);

            // A partial type states its base list once per part; the fact is one fact.
            if (!seen.Add(name))
            {
                continue;
            }

            results.Add(new BaseTypeFact(
                name,
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return results;
    }

    /// <summary>
    /// The symbol's overload set, or nothing when its name is not overloaded.
    /// <para>
    /// A set of one is returned empty rather than as the symbol's own single self: the
    /// pane hides the section on an empty list, and "Overloads: this one" would be a
    /// heading over a fact nobody asked about.
    /// </para>
    /// </summary>
    private List<OverloadSibling> LoadOverloads(long symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = OverloadSql.Siblings;
        command.Parameters.AddWithValue("$symbolId", symbolId);

        var results = new List<OverloadSibling>();

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);

                results.Add(new OverloadSibling(
                    id,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    IsCurrent: id == symbolId));
            }
        }

        return results.Count > 1 ? results : [];
    }

    private List<SymbolMember> LoadMembers(long symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, type_text, value, start_line, modifiers, param_text
            FROM symbol
            WHERE container_id = $symbolId
            ORDER BY start_offset
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);

        var results = new List<SymbolMember>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SymbolMember(
                reader.GetInt64(0),
                reader.GetString(1),
                (SymbolKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }

    /// <summary>
    /// Callers or callees for the detail pane. Container-to-member links are excluded on
    /// the same rule as the graph edges, so what the pane lists and what the canvas draws
    /// cannot disagree; those names appear under Members instead.
    /// </summary>
    private List<RelatedSymbol> LoadRelated(long symbolId, bool callers)
    {
        var sql = callers
            ? """
              SELECT s.id, s.name, s.kind, f.rel_path, r.line, r.kind, e.confidence
              FROM edge e
              JOIN ref r ON r.id = e.ref_id
              JOIN symbol s ON s.id = r.from_symbol_id
              JOIN file f ON f.id = s.file_id
              WHERE e.target_symbol_id = $symbolId AND s.id <> $symbolId
                AND s.id IS NOT (SELECT container_id FROM symbol WHERE id = $symbolId)
              ORDER BY s.name
              LIMIT $limit
              """
            : """
              SELECT s.id, s.name, s.kind, f.rel_path, r.line, r.kind, e.confidence
              FROM ref r
              JOIN edge e ON e.ref_id = r.id
              JOIN symbol s ON s.id = e.target_symbol_id
              JOIN file f ON f.id = s.file_id
              WHERE r.from_symbol_id = $symbolId AND s.id <> $symbolId
                AND s.container_id IS NOT $symbolId
              ORDER BY s.name
              LIMIT $limit
              """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$limit", RelatedLimit);

        var results = new List<RelatedSymbol>();
        var seen = new HashSet<(long, int)>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var referenceKind = reader.GetInt32(5);

            // One caller may reference the target many times; list it once per kind.
            if (!seen.Add((id, referenceKind)))
            {
                continue;
            }

            results.Add(new RelatedSymbol(
                id,
                reader.GetString(1),
                (SymbolKind)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4),
                (ReferenceKind)referenceKind,
                (EdgeConfidence)reader.GetInt32(6)));
        }

        return results;
    }

    /// <summary>
    /// Every call site behind one merged graph edge, in line order.
    /// <para>
    /// A drawn edge stands for all references between the pair with one kind — the merge
    /// key the neighbour queries group by — so this is the un-merged view the edge popover
    /// shows: each site's line and the verbatim argument text recorded at parse time.
    /// </para>
    /// </summary>
    public List<EdgeCallSite> GetEdgeCallSites(
        long sourceId,
        long targetId,
        ReferenceKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.line, r.arg_text, e.confidence, r.receiver_text, r.name
            FROM ref r
            JOIN edge e ON e.ref_id = r.id
            WHERE r.from_symbol_id = $sourceId
              AND e.target_symbol_id = $targetId
              AND r.kind = $kind
            GROUP BY r.id
            ORDER BY r.line
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$limit", 50);

        var results = new List<EdgeCallSite>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EdgeCallSite(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                (EdgeConfidence)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    /// <summary>
    /// References made by this symbol that resolved to nothing. Listing them is the
    /// honest alternative to inventing a target.
    /// </summary>
    private List<UnresolvedReference> LoadUnresolved(long symbolId)
    {
        using var command = connection.CreateCommand();
        // Bindings belong here as much as calls do: an unresolved binding path is either
        // a typo or a generated member the index cannot see, and both are worth a line.
        command.CommandText = """
            SELECT DISTINCT r.name, r.kind, MIN(r.line)
            FROM ref r
            WHERE r.from_symbol_id = $symbolId
              AND r.kind IN ($call, $instantiate, $binding, $resource)
              AND NOT EXISTS (SELECT 1 FROM edge e WHERE e.ref_id = r.id)
            GROUP BY r.name, r.kind
            ORDER BY r.name
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$call", (int)ReferenceKind.Call);
        command.Parameters.AddWithValue("$instantiate", (int)ReferenceKind.Instantiate);
        command.Parameters.AddWithValue("$binding", (int)ReferenceKind.Binding);
        command.Parameters.AddWithValue("$resource", (int)ReferenceKind.Resource);
        command.Parameters.AddWithValue("$limit", 50);

        var results = new List<UnresolvedReference>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new UnresolvedReference(
                reader.GetString(0),
                (ReferenceKind)reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        return results;
    }
}
