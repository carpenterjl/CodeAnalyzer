using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Search;

public sealed record SymbolSearchHit(
    long SymbolId,
    string Name,
    SymbolKind Kind,
    string RelativePath,
    int Line,
    string? Signature,
    int Score,
    string? ContainerName = null,
    string? ParameterText = null,
    string? Modifiers = null,
    string? TypeText = null,
    int OverloadCount = 1,
    int OverloadOrdinal = 1)
{
    /// <summary>
    /// What this hit is, in one line. Two overloads of one method otherwise produce two
    /// rows reading exactly alike, which is the whole reason a name is worth searching for
    /// and then impossible to pick between.
    /// </summary>
    public string Descriptor { get; } = SymbolFacts.Describe(
        Kind, Modifiers, TypeText, ParameterText is not null, OverloadCount, OverloadOrdinal);
}

/// <summary>How a query is matched against a symbol name.</summary>
public enum SymbolMatchMode
{
    /// <summary>VS Code style subsequence matching: <c>uwr</c> finds <c>uart_write</c>.</summary>
    Fuzzy = 0,

    /// <summary>The name must contain the query verbatim, case-insensitively.</summary>
    Substring = 1,
}

public sealed record SymbolSearchOptions
{
    public int Limit { get; init; } = 50;

    /// <summary>
    /// Excludes symbols declared inside a function body. On by default: locals swamp
    /// results and are rarely what someone is searching for.
    /// </summary>
    public bool ExcludeFunctionLocals { get; init; } = true;

    /// <summary>Restricts results to these kinds when non-empty.</summary>
    public IReadOnlySet<SymbolKind>? Kinds { get; init; }

    /// <summary>
    /// How the query is matched. Fuzzy is the default because it is what a search box
    /// should do. Substring exists because subsequence matching turns a common word into a
    /// list of accidents — <c>export</c> fits inside
    /// <c>AnExtraIgnoredDirectoryIsNotReported</c>, letter by letter — and someone who
    /// already knows the name they want should be able to say so.
    /// </summary>
    public SymbolMatchMode Match { get; init; } = SymbolMatchMode.Fuzzy;
}

/// <summary>
/// Fuzzy symbol search over an in-memory name table.
/// <para>
/// The table holds only ids, names and kinds — a few tens of megabytes even for a
/// million symbols — and full rows are fetched from SQLite only for the handful of
/// results actually shown. Scoring runs off the UI thread and is cancelled on each
/// new keystroke.
/// </para>
/// </summary>
public sealed class SymbolSearchService
{
    private readonly SqliteConnection _connection;

    // Parallel arrays rather than objects: one allocation per column instead of one per symbol.
    private long[] _ids = [];
    private string[] _names = [];
    private SymbolKind[] _kinds = [];
    private bool[] _isLocal = [];
    private int _count;

    public SymbolSearchService(SqliteConnection connection) => _connection = connection;

    public int IndexedSymbolCount => _count;

    /// <summary>
    /// Rebuilds the in-memory table. Called after indexing completes; a full scan of
    /// three narrow columns is fast even at a million rows.
    /// </summary>
    public void Reload(CancellationToken cancellationToken = default)
    {
        var capacity = Math.Max(1024, CountDefinitions());

        var ids = new long[capacity];
        var names = new string[capacity];
        var kinds = new SymbolKind[capacity];
        var isLocal = new bool[capacity];
        var count = 0;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.kind,
                   CASE WHEN c.kind IN ($function, $method) THEN 1 ELSE 0 END AS is_local
            FROM symbol s
            LEFT JOIN symbol c ON c.id = s.container_id
            WHERE s.is_definition = 1
            """;
        command.Parameters.AddWithValue("$function", (int)SymbolKind.Function);
        command.Parameters.AddWithValue("$method", (int)SymbolKind.Method);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (count == ids.Length)
            {
                var grown = ids.Length * 2;
                Array.Resize(ref ids, grown);
                Array.Resize(ref names, grown);
                Array.Resize(ref kinds, grown);
                Array.Resize(ref isLocal, grown);
            }

            ids[count] = reader.GetInt64(0);
            names[count] = reader.GetString(1);
            kinds[count] = (SymbolKind)reader.GetInt32(2);
            isLocal[count] = reader.GetInt32(3) == 1;
            count++;
        }

        _ids = ids;
        _names = names;
        _kinds = kinds;
        _isLocal = isLocal;
        _count = count;
    }

    private int CountDefinitions()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbol WHERE is_definition = 1";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Scores every symbol against the query and hydrates the top results.
    /// Safe to call from a worker thread.
    /// </summary>
    public List<SymbolSearchHit> Search(
        string query,
        SymbolSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SymbolSearchOptions();
        query = query.Trim();

        if (query.Length == 0 || _count == 0)
        {
            return [];
        }

        // A bounded min-heap would save little here: scoring dominates, and the result
        // list is capped well below the point where sorting matters.
        var scored = new List<(long Id, int Score, int NameLength)>(Math.Min(_count, 4096));

        for (var i = 0; i < _count; i++)
        {
            if ((i & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (options.ExcludeFunctionLocals && _isLocal[i])
            {
                continue;
            }

            if (options.Kinds is { Count: > 0 } && !options.Kinds.Contains(_kinds[i]))
            {
                continue;
            }

            var score = options.Match == SymbolMatchMode.Substring
                ? SubstringScore(query, _names[i])
                : FuzzyMatcher.Score(query, _names[i]);

            if (score is null)
            {
                continue;
            }

            scored.Add((_ids[i], score.Value, _names[i].Length));
        }

        if (scored.Count == 0)
        {
            return [];
        }

        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.NameLength.CompareTo(b.NameLength);
        });

        var top = scored.Take(options.Limit).ToList();
        return Hydrate(top, cancellationToken);
    }

    /// <summary>
    /// Ranks a verbatim, case-insensitive containment match: the whole name first, then a
    /// prefix, then an interior hit scored by how early it starts — the same bias as the
    /// fuzzy ranker, without its subsequence reach. Null means the query is not in there
    /// at all, which is the entire point of asking for this mode.
    /// </summary>
    private static int? SubstringScore(string query, string name)
    {
        var at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        if (name.Length == query.Length)
        {
            return 1000;
        }

        return at == 0 ? 800 : Math.Max(100, 700 - at);
    }

    /// <summary>Fetches display rows for the winning ids, preserving their ranked order.</summary>
    private List<SymbolSearchHit> Hydrate(
        List<(long Id, int Score, int NameLength)> ranked,
        CancellationToken cancellationToken)
    {
        var scoreById = ranked.ToDictionary(r => r.Id, r => r.Score);

        using var command = _connection.CreateCommand();
        // The container name is what tells two same-named members apart in the result
        // list — Device.Send vs Radio.Send — without opening either.
        command.CommandText = $"""
            SELECT s.id, s.name, s.kind, f.rel_path, s.start_line, s.signature, c.name,
                   s.param_text, s.modifiers, s.type_text,
                   ({OverloadSql.Count}) AS overload_count,
                   ({OverloadSql.Ordinal}) AS overload_ordinal
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            LEFT JOIN symbol c ON c.id = s.container_id
            WHERE s.id IN ({string.Join(",", ranked.Select(r => r.Id))})
            """;

        var rows = new Dictionary<long, SymbolSearchHit>(ranked.Count);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = reader.GetInt64(0);
            rows[id] = new SymbolSearchHit(
                id,
                reader.GetString(1),
                (SymbolKind)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                scoreById[id],
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt32(10),
                reader.GetInt32(11));
        }

        return ranked
            .Select(r => rows.GetValueOrDefault(r.Id))
            .Where(hit => hit is not null)
            .Select(hit => hit!)
            .ToList();
    }
}
