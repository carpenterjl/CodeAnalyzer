using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// Finds how one symbol reaches another through resolved references.
/// <para>
/// The search is a level-synchronised bidirectional breadth-first walk over the edge table.
/// Bidirectional because the frontier of a call graph grows fast: meeting in the middle
/// turns a search that would visit b^d nodes into two that visit b^(d/2). Every step is
/// capped, and the result says plainly whether a cap was what stopped it.
/// </para>
/// </summary>
public sealed class PathQueryService(SqliteConnection connection)
{
    /// <summary>Longest route to consider, counted in hops across both halves.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Total symbols either side may visit before the search gives up.</summary>
    public int MaxVisited { get; init; } = 40_000;

    /// <summary>Routes returned. Shortest routes between two hubs can still be many.</summary>
    public int MaxRoutes { get; init; } = 40;

    /// <summary>Ids per adjacency query. Keeps the generated SQL a sane size.</summary>
    private const int FrontierChunk = 400;

    /// <param name="maxDepth">
    /// Overrides <see cref="MaxDepth"/> for this call. Two symbols eight hops apart are
    /// related only in the loosest sense, so a shorter budget is often the more useful
    /// question to ask.
    /// </param>
    public PathTrace FindPaths(
        long fromId, long toId, int? maxDepth = null, CancellationToken cancellationToken = default)
    {
        var depthBudget = Math.Max(1, maxDepth ?? MaxDepth);

        var fromExists = SymbolExists(fromId);
        var toExists = SymbolExists(toId);

        if (!fromExists || !toExists)
        {
            return new PathTrace { FromId = fromId, ToId = toId, FromExists = fromExists, ToExists = toExists };
        }

        if (fromId == toId)
        {
            return new PathTrace
            {
                FromId = fromId,
                ToId = toId,
                FromExists = true,
                ToExists = true,
                Nodes = LoadNodes([fromId]),
                Routes = [new[] { fromId }],
                Length = 0,
            };
        }

        // Predecessors on the forward side and successors on the backward side. Both only
        // ever record a step that moves one level further from its own origin, so the two
        // maps are acyclic and route enumeration cannot loop.
        var forwardDepth = new Dictionary<long, int> { [fromId] = 0 };
        var forwardPreds = new Dictionary<long, List<long>>();
        var backwardDepth = new Dictionary<long, int> { [toId] = 0 };
        var backwardSuccs = new Dictionary<long, List<long>>();

        var forwardFrontier = new List<long> { fromId };
        var backwardFrontier = new List<long> { toId };

        var meeting = new List<long>();
        var exhausted = false;

        while (meeting.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (forwardFrontier.Count == 0 || backwardFrontier.Count == 0)
            {
                // One side ran out of new nodes: there is genuinely no route.
                break;
            }

            var currentDepth = forwardDepth[forwardFrontier[0]] + backwardDepth[backwardFrontier[0]];
            if (currentDepth >= depthBudget)
            {
                exhausted = true;
                break;
            }

            if (forwardDepth.Count + backwardDepth.Count > MaxVisited)
            {
                exhausted = true;
                break;
            }

            // Expand whichever side is cheaper. This is the whole point of searching from
            // both ends: on a graph with one heavily-called hub, one direction can be
            // orders of magnitude wider than the other.
            if (forwardFrontier.Count <= backwardFrontier.Count)
            {
                forwardFrontier = Advance(
                    forwardFrontier, forwardDepth, forwardPreds, backwardDepth, meeting,
                    outgoing: true, cancellationToken);
            }
            else
            {
                backwardFrontier = Advance(
                    backwardFrontier, backwardDepth, backwardSuccs, forwardDepth, meeting,
                    outgoing: false, cancellationToken);
            }
        }

        if (meeting.Count == 0)
        {
            return new PathTrace
            {
                FromId = fromId,
                ToId = toId,
                FromExists = true,
                ToExists = true,
                SearchExhausted = exhausted,
            };
        }

        var routes = BuildRoutes(meeting, fromId, toId, forwardPreds, backwardSuccs, out var truncated);
        if (routes.Count == 0)
        {
            return new PathTrace
            {
                FromId = fromId,
                ToId = toId,
                FromExists = true,
                ToExists = true,
                SearchExhausted = exhausted,
            };
        }

        var used = new HashSet<long>();
        var hops = new HashSet<(long, long)>();
        foreach (var route in routes)
        {
            for (var i = 0; i < route.Count; i++)
            {
                used.Add(route[i]);
                if (i + 1 < route.Count)
                {
                    hops.Add((route[i], route[i + 1]));
                }
            }
        }

        return new PathTrace
        {
            FromId = fromId,
            ToId = toId,
            FromExists = true,
            ToExists = true,
            Nodes = LoadNodes(used),
            Links = LoadLinks(hops),
            Routes = routes,
            Length = routes[0].Count - 1,
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Expands one whole level of one side. The level is finished before the caller looks
    /// at <paramref name="meeting"/>, which is what makes the result <em>every</em> shortest
    /// route rather than the first one found.
    /// </summary>
    private List<long> Advance(
        List<long> frontier,
        Dictionary<long, int> depth,
        Dictionary<long, List<long>> parents,
        Dictionary<long, int> otherSide,
        List<long> meeting,
        bool outgoing,
        CancellationToken cancellationToken)
    {
        var nextDepth = depth[frontier[0]] + 1;
        var next = new List<long>();

        foreach (var (source, target) in Adjacent(frontier, outgoing, cancellationToken))
        {
            // "source" and "target" are always in edge direction; which of them is the step
            // forward depends on which side we are walking.
            var from = outgoing ? source : target;
            var to = outgoing ? target : source;

            if (depth.TryGetValue(to, out var existing))
            {
                if (existing == nextDepth)
                {
                    AddParent(parents, to, from);
                }

                continue;
            }

            depth[to] = nextDepth;
            AddParent(parents, to, from);
            next.Add(to);

            if (otherSide.ContainsKey(to))
            {
                meeting.Add(to);
            }
        }

        return next;
    }

    private static void AddParent(Dictionary<long, List<long>> parents, long node, long parent)
    {
        if (!parents.TryGetValue(node, out var list))
        {
            list = [];
            parents[node] = list;
        }

        if (!list.Contains(parent))
        {
            list.Add(parent);
        }
    }

    /// <summary>
    /// Every resolved edge touching the frontier, in edge direction.
    /// <para>
    /// Container-to-member edges are excluded here for the same reason the neighbourhood
    /// query excludes them: routing "main reaches the config struct because uart_init has a
    /// local called config" would be a path nobody can act on.
    /// </para>
    /// </summary>
    private IEnumerable<(long Source, long Target)> Adjacent(
        List<long> frontier, bool outgoing, CancellationToken cancellationToken)
    {
        var column = outgoing ? "r.from_symbol_id" : "e.target_symbol_id";
        var results = new List<(long, long)>();

        for (var offset = 0; offset < frontier.Count; offset += FrontierChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = frontier.Skip(offset).Take(FrontierChunk).ToList();
            var placeholders = string.Join(",", chunk.Select((_, i) => $"$p{i}"));

            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT DISTINCT r.from_symbol_id, e.target_symbol_id
                FROM ref r
                JOIN edge e ON e.ref_id = r.id
                JOIN symbol t ON t.id = e.target_symbol_id
                WHERE {column} IN ({placeholders})
                  AND r.from_symbol_id IS NOT NULL
                  AND r.from_symbol_id <> e.target_symbol_id
                  AND t.container_id IS NOT r.from_symbol_id
                """;

            for (var i = 0; i < chunk.Count; i++)
            {
                command.Parameters.AddWithValue($"$p{i}", chunk[i]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
        }

        return results;
    }

    private List<IReadOnlyList<long>> BuildRoutes(
        List<long> meeting,
        long fromId,
        long toId,
        Dictionary<long, List<long>> forwardPreds,
        Dictionary<long, List<long>> backwardSuccs,
        out bool truncated)
    {
        var routes = new List<IReadOnlyList<long>>();
        var budget = new WalkBudget();

        foreach (var node in meeting)
        {
            var heads = Walk(node, fromId, forwardPreds, reverse: true, budget);
            var tails = Walk(node, toId, backwardSuccs, reverse: false, budget);

            foreach (var head in heads)
            {
                foreach (var tail in tails)
                {
                    if (routes.Count >= MaxRoutes)
                    {
                        truncated = true;
                        return routes;
                    }

                    // head ends at the meeting node and tail starts at it.
                    var route = new List<long>(head);
                    route.AddRange(tail.Skip(1));
                    routes.Add(route);
                }
            }
        }

        truncated = budget.Truncated;
        return routes;
    }

    /// <summary>Carries "we stopped early" out of the recursion so it can be reported.</summary>
    private sealed class WalkBudget
    {
        public bool Truncated;
    }

    /// <summary>
    /// Enumerates the chains from <paramref name="node"/> back to <paramref name="origin"/>
    /// through a parent map. Each returned chain runs origin → … → node when
    /// <paramref name="reverse"/> is set, and node → … → origin otherwise.
    /// </summary>
    private List<List<long>> Walk(
        long node, long origin, Dictionary<long, List<long>> parents, bool reverse, WalkBudget budget)
    {
        if (node == origin)
        {
            return [[node]];
        }

        if (!parents.TryGetValue(node, out var previous))
        {
            return [];
        }

        var results = new List<List<long>>();
        foreach (var parent in previous)
        {
            foreach (var chain in Walk(parent, origin, parents, reverse, budget))
            {
                if (results.Count >= MaxRoutes)
                {
                    budget.Truncated = true;
                    return results;
                }

                var extended = new List<long>(chain);
                if (reverse)
                {
                    extended.Add(node);
                }
                else
                {
                    extended.Insert(0, node);
                }

                results.Add(extended);
            }
        }

        return results;
    }

    private bool SymbolExists(long symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM symbol WHERE id = $symbolId";
        command.Parameters.AddWithValue("$symbolId", symbolId);
        return command.ExecuteScalar() is not null;
    }

    private List<PathNode> LoadNodes(IReadOnlyCollection<long> ids)
    {
        var results = new List<PathNode>(ids.Count);
        if (ids.Count == 0)
        {
            return results;
        }

        var list = ids.ToList();
        var placeholders = string.Join(",", list.Select((_, i) => $"$p{i}"));

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.name, s.kind, f.rel_path, s.start_line
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            WHERE s.id IN ({placeholders})
            """;

        for (var i = 0; i < list.Count; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", list[i]);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new PathNode(
                reader.GetInt64(0),
                reader.GetString(1),
                (SymbolKind)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4)));
        }

        return results;
    }

    /// <summary>
    /// Facts for the hops actually used. Where a pair is linked more than once, the weakest
    /// confidence wins: a route is only as trustworthy as its least certain step.
    /// </summary>
    private List<PathLink> LoadLinks(IReadOnlyCollection<(long Source, long Target)> hops)
    {
        var results = new List<PathLink>(hops.Count);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.kind, MAX(e.confidence), MIN(r.line)
            FROM ref r
            JOIN edge e ON e.ref_id = r.id
            WHERE r.from_symbol_id = $source AND e.target_symbol_id = $target
            GROUP BY r.kind
            ORDER BY COUNT(*) DESC
            LIMIT 1
            """;
        var source = command.Parameters.Add("$source", SqliteType.Integer);
        var target = command.Parameters.Add("$target", SqliteType.Integer);

        foreach (var (from, to) in hops)
        {
            source.Value = from;
            target.Value = to;

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                results.Add(new PathLink(
                    from,
                    to,
                    (ReferenceKind)reader.GetInt32(0),
                    (EdgeConfidence)reader.GetInt32(1),
                    reader.GetInt32(2)));
            }
        }

        return results;
    }
}
