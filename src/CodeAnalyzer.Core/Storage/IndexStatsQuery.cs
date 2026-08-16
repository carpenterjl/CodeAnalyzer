using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Storage;

/// <summary>A label and how many rows carry it — one tally line in a stats block.</summary>
public sealed record NamedCount(string Name, int Count);

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
            SymbolsByKind: symbolsByKind,
            TotalEdges: (int)scalars[6],
            EdgesByConfidence: edgesByConfidence,
            TotalDeps: (int)scalars[7],
            ResolvedDeps: (int)scalars[8],
            DatabaseBytes: scalars[9]);
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
