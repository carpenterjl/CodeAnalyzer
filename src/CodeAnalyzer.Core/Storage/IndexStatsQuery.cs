using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Storage;

/// <summary>A label and how many rows carry it — one tally line in a stats block.</summary>
public sealed record NamedCount(string Name, int Count);

/// <summary>
/// One slice of the resolution triple: how a named subset of references — a reference
/// kind, or the references written in one language — divided into uniquely resolved,
/// ambiguous and unresolved. The whole-index triple is the sum of any complete set of
/// these, which is what makes an outlier row worth reading: a subset resolving far worse
/// than the index as a whole is a resolver gap with a name on it.
/// </summary>
public sealed record ResolutionSplit(string Name, int Total, int Unique, int Ambiguous, int Unresolved);

/// <summary>
/// Aggregate facts about one workspace's index: what is in it, and — the part no other
/// query answers — how well resolution is doing. Every count is measured from the tables
/// at read time; nothing here is cached or estimated.
/// </summary>
public sealed record IndexStats(
    int TotalFiles,
    IReadOnlyList<NamedCount> FilesByLanguage,
    int ImperfectFiles,
    int TotalSymbols,
    IReadOnlyList<NamedCount> SymbolsByKind,
    int TotalRefs,
    int RefsWithReceiver,
    int RefsWithArgs,
    int RefsResolvedUniquely,
    int RefsAmbiguous,
    int RefsUnresolved,
    double MeanCandidatesWhenAmbiguous,
    IReadOnlyList<ResolutionSplit> RefsByKind,
    IReadOnlyList<ResolutionSplit> RefsByLanguage,
    int TotalEdges,
    IReadOnlyList<NamedCount> EdgesByConfidence,
    int TotalDeps,
    int ResolvedDeps,
    long DatabaseBytes);

/// <summary>
/// The stats block, read straight off a connection. Same placement reasoning as
/// <see cref="FileErrorQuery"/>: the GUI holds a writable store, the CLI a read-only
/// session, and an aggregate that exists in two copies is an aggregate that drifts.
/// <para>
/// The resolution triple counts <em>references</em>, not edges: a reference with exactly
/// one edge resolved uniquely, one with several is ambiguous, one with none is unresolved.
/// Edge-level confidence is tallied separately — the two disagree by design, because an
/// ambiguous reference contributes every candidate to the edge table.
/// </para>
/// <para>
/// The one exception is an include or an import, which the resolver settles against a file
/// rather than a symbol: those count as resolved when their <c>file_dep</c> row found a
/// workspace file, not when they carry an edge — which they never do. See
/// <see cref="Read"/> and the splits query for why counting their edges reported the truth
/// backwards.
/// </para>
/// </summary>
public static class IndexStatsQuery
{
    public static IndexStats Read(SqliteConnection connection)
    {
        var filesByLanguage = ReadPairs(connection,
            "SELECT language, COUNT(*) FROM file GROUP BY language ORDER BY COUNT(*) DESC, language");

        var symbolsByKind = ReadPairs(connection,
            "SELECT kind, COUNT(*) FROM symbol WHERE is_definition = 1 GROUP BY kind ORDER BY COUNT(*) DESC",
            kind => ((SymbolKind)kind).ToString());

        var edgesByConfidence = ReadPairs(connection,
            "SELECT confidence, COUNT(*) FROM edge GROUP BY confidence ORDER BY confidence",
            confidence => ((EdgeConfidence)confidence).ToString());

        var scalars = ReadScalars(connection, """
            SELECT
                (SELECT COUNT(*) FROM file),
                (SELECT COUNT(*) FROM file WHERE status <> 0),
                (SELECT COUNT(*) FROM symbol WHERE is_definition = 1),
                (SELECT COUNT(*) FROM ref),
                (SELECT COUNT(*) FROM ref WHERE receiver_text IS NOT NULL),
                (SELECT COUNT(*) FROM ref WHERE arg_text IS NOT NULL),
                (SELECT COUNT(*) FROM edge),
                (SELECT COUNT(*) FROM file_dep),
                (SELECT COUNT(*) FROM file_dep WHERE dep_file_id IS NOT NULL),
                (SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size())
            """);

        // Per-reference fan-out, computed once: how many refs landed on exactly one
        // definition, on several, and on none at all.
        int unique, ambiguous;
        long ambiguousEdges;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(SUM(n = 1), 0),
                       COALESCE(SUM(n > 1), 0),
                       COALESCE(SUM(CASE WHEN n > 1 THEN n ELSE 0 END), 0)
                FROM (SELECT COUNT(*) AS n FROM edge GROUP BY ref_id)
                """;
            using var reader = command.ExecuteReader();
            reader.Read();
            unique = reader.GetInt32(0);
            ambiguous = reader.GetInt32(1);
            ambiguousEdges = reader.GetInt64(2);
        }

        // Include and Import carry no edge — they resolve in file_dep (see ReadSplits) — so a
        // resolved dependency has to be added to the unique count here as well, or the
        // headline triple and the by-kind rows that are meant to sum to it would disagree by
        // exactly these references. Never ambiguous: a dependency resolves to one file or none.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT COUNT(*)
                FROM ref r
                JOIN file_dep d ON d.file_id = r.file_id AND d.dep_path = r.name
                WHERE r.kind IN ({(int)ReferenceKind.Include}, {(int)ReferenceKind.Import})
                  AND d.dep_file_id IS NOT NULL
                """;
            unique += Convert.ToInt32(command.ExecuteScalar());
        }

        var refsByKind = ReadSplits(connection,
            "r.kind", "ref r", name => ((ReferenceKind)int.Parse(name)).ToString());

        var refsByLanguage = ReadSplits(connection,
            "f.language", "ref r JOIN file f ON f.id = r.file_id");

        var totalRefs = (int)scalars[3];
        return new IndexStats(
            TotalFiles: (int)scalars[0],
            FilesByLanguage: filesByLanguage,
            ImperfectFiles: (int)scalars[1],
            TotalSymbols: (int)scalars[2],
            TotalRefs: totalRefs,
            RefsWithReceiver: (int)scalars[4],
            RefsWithArgs: (int)scalars[5],
            RefsResolvedUniquely: unique,
            RefsAmbiguous: ambiguous,
            RefsUnresolved: totalRefs - unique - ambiguous,
            MeanCandidatesWhenAmbiguous: ambiguous == 0 ? 0 : (double)ambiguousEdges / ambiguous,
            RefsByKind: refsByKind,
            RefsByLanguage: refsByLanguage,
            SymbolsByKind: symbolsByKind,
            TotalEdges: (int)scalars[6],
            EdgesByConfidence: edgesByConfidence,
            TotalDeps: (int)scalars[7],
            ResolvedDeps: (int)scalars[8],
            DatabaseBytes: scalars[9]);
    }

    /// <summary>
    /// The resolution triple, grouped by any single column. The LEFT JOIN is what makes the
    /// unresolved column possible: a reference with no edge has no row in the fan-out
    /// subquery, so it survives the join with a NULL count rather than dropping out.
    /// <para>
    /// Include and Import are measured against a different table on purpose. The resolver
    /// excludes them from edge resolution outright — an <c>#include</c> or a <c>using</c>
    /// names a file or a namespace, not a symbol — and settles them in <c>file_dep</c>
    /// instead. Counting their edges (always none) reported every one of them as unresolved,
    /// which inverted the truth for C's includes: all five resolve to a workspace header, so
    /// the row that read 100% unresolved was in fact 100% resolved. Here each such reference
    /// is joined to its own <c>file_dep</c> row — the key <c>(file_id, dep_path)</c> matches
    /// the reference's <c>(file_id, name)</c> exactly — and counts as resolved when that row
    /// found a workspace file. A file dependency is unique-or-nothing by construction (the
    /// resolver takes a target only when exactly one file matches), so these kinds never
    /// contribute to the ambiguous column.
    /// </para>
    /// </summary>
    private static List<ResolutionSplit> ReadSplits(
        SqliteConnection connection, string groupBy, string from, Func<string, string>? nameOf = null)
    {
        var fileScoped = $"{(int)ReferenceKind.Include}, {(int)ReferenceKind.Import}";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {groupBy},
                   COUNT(*),
                   COALESCE(SUM(CASE WHEN r.kind IN ({fileScoped})
                                     THEN (CASE WHEN d.dep_file_id IS NOT NULL THEN 1 ELSE 0 END)
                                     ELSE (CASE WHEN e.n = 1 THEN 1 ELSE 0 END) END), 0),
                   COALESCE(SUM(CASE WHEN r.kind IN ({fileScoped})
                                     THEN 0
                                     ELSE (CASE WHEN e.n > 1 THEN 1 ELSE 0 END) END), 0)
            FROM {from}
            LEFT JOIN (SELECT ref_id, COUNT(*) AS n FROM edge GROUP BY ref_id) e ON e.ref_id = r.id
            LEFT JOIN file_dep d ON d.file_id = r.file_id AND d.dep_path = r.name
            GROUP BY {groupBy}
            ORDER BY COUNT(*) DESC
            """;

        var splits = new List<ResolutionSplit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var raw = reader.GetValue(0)?.ToString() ?? string.Empty;
            var total = reader.GetInt32(1);
            var unique = reader.GetInt32(2);
            var ambiguous = reader.GetInt32(3);
            splits.Add(new ResolutionSplit(
                nameOf is null ? raw : nameOf(raw),
                total, unique, ambiguous, total - unique - ambiguous));
        }

        return splits;
    }

    private static List<NamedCount> ReadPairs(
        SqliteConnection connection, string sql, Func<int, string>? nameOf = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var pairs = new List<NamedCount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = nameOf is null ? reader.GetString(0) : nameOf(reader.GetInt32(0));
            pairs.Add(new NamedCount(name, reader.GetInt32(1)));
        }

        return pairs;
    }

    private static long[] ReadScalars(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        reader.Read();

        var values = new long[reader.FieldCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.GetInt64(i);
        }

        return values;
    }
}
