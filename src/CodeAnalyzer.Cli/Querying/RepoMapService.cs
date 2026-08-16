using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Cli.Querying;

/// <summary>One ranked symbol on the map: what it is and how many distinct callers reference it.</summary>
internal sealed record RepoMapEntry(
    long Id,
    string Name,
    string? ContainerName,
    SymbolKind Kind,
    string? ParameterText,
    string? Modifiers,
    string RelativePath,
    int Line,
    int FanIn);

/// <summary>
/// The ranked skeleton, plus what a zero-edge workspace falls back to
/// (<see cref="FilesBySymbolCount"/>) so an unranked map says so instead of saying nothing.
/// </summary>
internal sealed record RepoMap(
    IReadOnlyList<RepoMapEntry> Entries,
    IReadOnlyList<(string RelativePath, int Symbols)> FilesBySymbolCount)
{
    /// <summary>True when the ranking fetch hit its cap — symbols beyond it exist unranked.</summary>
    public bool FetchCapHit { get; init; }
}

/// <summary>
/// A codebase overview an agent can prime its context with: definitions ranked by
/// <c>COUNT(DISTINCT caller)</c> over real resolved edges, grouped by file. The count is a
/// stored fact, not a heuristic score — the map's header says exactly what the ranking is.
/// <para>Callers hold the database gate.</para>
/// </summary>
internal static class RepoMapService
{
    /// <summary>Symbols fetched for ranking; the char budget usually cuts far earlier.</summary>
    private const int FetchCap = 2000;

    /// <summary>Ids per hydration query, the <c>PathQueryService</c> chunk idiom.</summary>
    private const int Chunk = 400;

    public static RepoMap Build(ReadOnlyIndexSession session, CancellationToken cancellationToken = default)
    {
        var fanIn = ReadFanIn(session, cancellationToken);

        if (fanIn.Count == 0)
        {
            return new RepoMap([], ReadFilesBySymbolCount(session));
        }

        var entries = new List<RepoMapEntry>(fanIn.Count);

        foreach (var chunk in fanIn.Keys.Chunk(Chunk))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var command = session.Connection.CreateCommand();
            command.CommandText = $"""
                SELECT s.id, s.name, c.name, s.kind, s.param_text, s.modifiers, f.rel_path, s.start_line
                FROM symbol s
                JOIN file f ON f.id = s.file_id
                LEFT JOIN symbol c ON c.id = s.container_id
                WHERE s.id IN ({string.Join(',', chunk)})
                  AND s.is_definition = 1
                  AND s.kind IN ({RankedKinds})
                  AND (c.id IS NULL OR c.kind NOT IN ({LocalContainerKinds}))
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                entries.Add(new RepoMapEntry(
                    id,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    (SymbolKind)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt32(7),
                    fanIn[id]));
            }
        }

        // Deterministic order: fan-in desc, then name, then id — two runs of the same
        // index must print the same map.
        entries.Sort((a, b) =>
        {
            var byFanIn = b.FanIn.CompareTo(a.FanIn);
            if (byFanIn != 0)
            {
                return byFanIn;
            }

            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : a.Id.CompareTo(b.Id);
        });

        return new RepoMap(entries, []) { FetchCapHit = fanIn.Count == FetchCap };
    }

    /// <summary>
    /// Definitions worth a line on a map. Fields, variables, parameters and ports are
    /// deliberately absent — they are detail the outline command shows per file.
    /// </summary>
    private static readonly string RankedKinds = string.Join(',',
        (int)SymbolKind.Function, (int)SymbolKind.Method,
        (int)SymbolKind.Class, (int)SymbolKind.Struct, (int)SymbolKind.Union,
        (int)SymbolKind.Enum, (int)SymbolKind.Interface, (int)SymbolKind.Typedef,
        (int)SymbolKind.Constant, (int)SymbolKind.Macro,
        (int)SymbolKind.Namespace, (int)SymbolKind.Module);

    /// <summary>Same rule as the search table: declared inside a callable = a local.</summary>
    private static readonly string LocalContainerKinds = string.Join(',',
        (int)SymbolKind.Function, (int)SymbolKind.Method);

    private static Dictionary<long, int> ReadFanIn(
        ReadOnlyIndexSession session, CancellationToken cancellationToken)
    {
        using var command = session.Connection.CreateCommand();

        // One aggregation over the edge table — never a correlated subquery per symbol.
        // Self-references and container-to-member edges are excluded for the same reason
        // the graph excludes them: a symbol's own internals are not its callers.
        command.CommandText = """
            SELECT e.target_symbol_id, COUNT(DISTINCT r.from_symbol_id) AS fan_in
            FROM edge e
            JOIN ref r ON r.id = e.ref_id
            WHERE r.from_symbol_id IS NOT NULL
              AND r.from_symbol_id <> e.target_symbol_id
              AND e.to_own_member = 0
            GROUP BY e.target_symbol_id
            ORDER BY fan_in DESC
            LIMIT $cap
            """;
        command.Parameters.AddWithValue("$cap", FetchCap);

        var fanIn = new Dictionary<long, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            fanIn[reader.GetInt64(0)] = reader.GetInt32(1);
        }

        return fanIn;
    }

    private static List<(string, int)> ReadFilesBySymbolCount(ReadOnlyIndexSession session)
    {
        using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT f.rel_path, COUNT(*) AS symbols
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            WHERE s.is_definition = 1
            GROUP BY f.id
            ORDER BY symbols DESC, f.rel_path
            LIMIT 100
            """;

        var files = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            files.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return files;
    }
}
