using System.Text;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Search;

/// <param name="LooseMatch">
/// That the query's letters appear in this name in order and nothing better can be said
/// for it: the match did not reach <see cref="FuzzyMatcher.StrongScoreFloor"/>. Verbatim
/// matching never sets it — containing the query is itself the answer.
/// </param>
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
    int OverloadOrdinal = 1,
    bool LooseMatch = false,
    string? DocComment = null)
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

    /// <summary>
    /// The query is matched against the comment written above each declaration rather than
    /// against its name. A separate mode rather than a widening of the other two, because
    /// the two populations are not comparable: a name is a few characters chosen to be
    /// unique and a comment is prose, and one fuzzy scorer over both would rank a symbol
    /// whose comment happens to contain the query's letters in order above the symbol
    /// actually called that. Verbatim containment is what prose deserves.
    /// </summary>
    DocComment = 2,
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

    /// <summary>
    /// Carries each hit's doc comment back with it. Off by default: a comment is prose and
    /// four definitions in five have none, so switched on always it would either double the
    /// height of every result list or be printed truncated to the point of saying nothing.
    /// Searching comments turns it on by itself — a hit found by its comment that does not
    /// show the comment is asking to be taken on faith.
    /// </summary>
    public bool IncludeDocComments { get; init; }
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

    /// <summary>
    /// Reorders each run of equally-scored, equally-long hits by how much of the workspace
    /// uniquely refers to each — the same importance signal <c>repo_map</c> ranks by.
    /// <para>
    /// This is asked per tie rather than held for every symbol, and the difference is the
    /// whole cost of the tie-break. Loading it for all of them added 235 ms to every command
    /// on a 63,000-symbol workspace — more than half the wall time of a search — to settle
    /// an ordering that 15 of 51 real queries need and that never involves more than a
    /// handful of rows. The targeted query is 0.02 ms warm. Narrowing the whole-table
    /// version to names that could tie was measured and rejected: 78.5% of definitions there
    /// share a name with another, so it saves nothing.
    /// </para>
    /// </summary>
    private void BreakTiesByImportance(
        List<(long Id, int Score, int NameLength)> scored,
        int limit,
        CancellationToken cancellationToken)
    {
        static bool SameRank(
            (long Id, int Score, int NameLength) a,
            (long Id, int Score, int NameLength) b)
            => a.Score == b.Score && a.NameLength == b.NameLength;

        // Only the returned window can be affected, extended through whatever run straddles
        // its edge: a run's members can only be reordered among themselves, but when the cut
        // falls inside one, that order decides which of them is returned at all.
        var window = Math.Min(scored.Count, limit);
        while (window > 0 && window < scored.Count && SameRank(scored[window], scored[window - 1]))
        {
            window++;
        }

        var runs = new List<(int Start, int Length)>();
        for (var start = 0; start < window;)
        {
            var end = start + 1;
            while (end < window && SameRank(scored[end], scored[start]))
            {
                end++;
            }

            if (end - start > 1)
            {
                runs.Add((start, end - start));
            }

            start = end;
        }

        if (runs.Count == 0)
        {
            return;
        }

        var tied = runs
            .SelectMany(run => Enumerable.Range(run.Start, run.Length))
            .Select(i => scored[i].Id)
            .ToList();
        var referencers = LoadUniqueReferencers(tied, cancellationToken);

        var byImportance = Comparer<(long Id, int Score, int NameLength)>.Create((a, b) =>
        {
            var byReferencers = referencers.GetValueOrDefault(b.Id)
                .CompareTo(referencers.GetValueOrDefault(a.Id));
            return byReferencers != 0 ? byReferencers : a.Id.CompareTo(b.Id);
        });

        foreach (var (start, length) in runs)
        {
            scored.Sort(start, length, byImportance);
        }
    }

    /// <summary>
    /// How many distinct symbols refer to each of these, over UNIQUE edges only.
    /// <para>
    /// Ambiguous edges land on every same-name candidate, so counting them makes all such
    /// candidates read as equally important — measured on this repo, the two fields named
    /// <c>ReferenceKind</c> carried 120 and 119 smeared referencers against the enum's 28,
    /// while unique-only reads 1, 0 and 28. Same-name symbols are exactly the group a tie
    /// has to separate, so the smeared count is the one signal that cannot do it.
    /// </para>
    /// </summary>
    private Dictionary<long, int> LoadUniqueReferencers(
        List<long> ids,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<long, int>(ids.Count);

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.target_symbol_id, COUNT(DISTINCT r.from_symbol_id)
            FROM edge e
            JOIN ref r ON r.id = e.ref_id
            WHERE e.confidence = $unique
              AND e.target_symbol_id IN ({string.Join(",", ids)})
            GROUP BY e.target_symbol_id
            """;
        command.Parameters.AddWithValue("$unique", (int)EdgeConfidence.Unique);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            counts[reader.GetInt64(0)] = reader.GetInt32(1);
        }

        return counts;
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

        if (options.Match == SymbolMatchMode.DocComment)
        {
            return SearchDocComments(query, options, cancellationToken);
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

        // Id last so that one query always returns one order: List.Sort is unstable, and 15
        // of the 51 real queries ever issued against this tool hit a rank-1 tie, which it
        // was resolving by partition luck.
        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byLength = a.NameLength.CompareTo(b.NameLength);
            return byLength != 0 ? byLength : a.Id.CompareTo(b.Id);
        });

        BreakTiesByImportance(scored, options.Limit, cancellationToken);

        // Verbatim matching has no loose tier: a name that contains the query contains it.
        // Fuzzy matching does, and because every hit here was scored against the same
        // query, the loose ones are exactly the tail of this already-sorted list.
        var floor = options.Match == SymbolMatchMode.Substring
            ? int.MinValue
            : FuzzyMatcher.StrongScoreFloor(query);

        var top = scored.Take(options.Limit).ToList();
        return Hydrate(top, floor, options.IncludeDocComments, cancellationToken);
    }

    /// <summary>
    /// Finds symbols by what was written about them rather than by what they are called.
    /// <para>
    /// Straight to SQL rather than through the in-memory table, which holds names only.
    /// Holding 3.3 MB of prose in memory and fuzzy-scoring it on every keystroke would be
    /// the wrong shape twice over: comment search is a deliberate act, not a search-box
    /// default, and prose wants containment rather than subsequence matching.
    /// </para>
    /// <para>
    /// Order is stated rather than left to the scan, which is round sixteen's lesson: a hit
    /// whose NAME also contains the query comes first, because a symbol called
    /// <c>RetryPolicy</c> whose comment explains retries is more likely to be what "retry"
    /// meant than one that mentions retries in passing; then by where in the comment the
    /// match falls, an opening sentence being about the symbol more often than a closing
    /// aside; then by id, so one query always returns one order.
    /// </para>
    /// </summary>
    private List<SymbolSearchHit> SearchDocComments(
        string query,
        SymbolSearchOptions options,
        CancellationToken cancellationToken)
    {
        var kindFilter = options.Kinds is { Count: > 0 }
            ? $"AND s.kind IN ({string.Join(",", options.Kinds.Select(k => (int)k))})"
            : string.Empty;
        var localFilter = options.ExcludeFunctionLocals
            ? "AND (c.kind IS NULL OR c.kind NOT IN ($function, $method))"
            : string.Empty;

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id
            FROM symbol s
            LEFT JOIN symbol c ON c.id = s.container_id
            WHERE s.is_definition = 1
              AND s.doc_comment IS NOT NULL
              AND s.doc_comment LIKE '%' || $pattern || '%' ESCAPE '\'
              {kindFilter}
              {localFilter}
            ORDER BY CASE WHEN INSTR(LOWER(s.name), LOWER($raw)) > 0 THEN 0 ELSE 1 END,
                     INSTR(LOWER(s.doc_comment), LOWER($raw)),
                     s.id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$pattern", EscapeForLike(query));
        command.Parameters.AddWithValue("$raw", query);
        command.Parameters.AddWithValue("$function", (int)SymbolKind.Function);
        command.Parameters.AddWithValue("$method", (int)SymbolKind.Method);
        command.Parameters.AddWithValue("$limit", options.Limit);

        var ranked = new List<(long Id, int Score, int NameLength)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ranked.Add((reader.GetInt64(0), 0, 0));
            }
        }

        // int.MinValue for the floor: the loose mark says a fuzzy match was weak, and
        // containment in prose is not weak, it is containment. A hit here always shows its
        // comment, whatever the caller asked for — a match a reader cannot see is a claim.
        return Hydrate(ranked, int.MinValue, includeDocComments: true, cancellationToken);
    }

    /// <summary>
    /// The words of a query, for matching prose: camel case split apart, punctuation
    /// dropped, anything shorter than three characters discarded.
    /// <para>
    /// Three characters because the tokens this exists to drop are the <c>of</c>, <c>to</c>
    /// and <c>a</c> that appear in every English comment ever written, and because the
    /// two-letter tokens a query actually means — <c>id</c>, <c>io</c> — are worse than
    /// useless as a conjunct: requiring them of every comment excludes the right answer
    /// more often than it finds it.
    /// </para>
    /// </summary>
    public static List<string> Words(string query)
    {
        var words = new List<string>();
        var word = new StringBuilder();

        for (var i = 0; i <= query.Length; i++)
        {
            var c = i < query.Length ? query[i] : ' ';

            // A capital starts a new word after a lower-case letter (StatsCommand), and
            // also at the end of a run of capitals when a lower-case letter follows it —
            // otherwise MCPServer is one unsearchable word rather than "mcp" and "server".
            var boundary = !char.IsLetterOrDigit(c)
                || (char.IsUpper(c) && word.Length > 0
                    && (!char.IsUpper(query[i - 1])
                        || (i + 1 < query.Length && char.IsLower(query[i + 1]))));

            if (boundary && word.Length > 0)
            {
                if (word.Length >= 3)
                {
                    words.Add(word.ToString().ToLowerInvariant());
                }

                word.Clear();
            }

            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
        }

        return words;
    }

    /// <summary>
    /// The second question, asked only when the first one found nothing: which declarations
    /// have every word of the query somewhere in the comment above them.
    /// <para>
    /// Every word, not the query verbatim, and that distinction is the whole feature.
    /// Replaying the 70 distinct <c>search_symbols</c> queries this tool has ever been
    /// asked, 15 returned no strong name hit; searching comments for the query verbatim
    /// rescued <b>1</b> of them, and requiring each word separately rescued <b>10</b>.
    /// The queries that fail are things like <c>StatsCommand</c> and
    /// <c>handle property table</c> — a concept spelled as a name that the codebase does
    /// not use, which no amount of matching against names can reach, and which the author
    /// of the right symbol usually did write down in prose.
    /// </para>
    /// </summary>
    public List<SymbolSearchHit> SearchDocCommentWords(
        string query,
        SymbolSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SymbolSearchOptions();
        var words = Words(query);
        if (words.Count == 0 || _count == 0)
        {
            return [];
        }

        var kindFilter = options.Kinds is { Count: > 0 }
            ? $"AND s.kind IN ({string.Join(",", options.Kinds.Select(k => (int)k))})"
            : string.Empty;
        var localFilter = options.ExcludeFunctionLocals
            ? "AND (c.kind IS NULL OR c.kind NOT IN ($function, $method))"
            : string.Empty;
        var wordFilter = string.Concat(words.Select((_, i) =>
            $"\n  AND LOWER(s.doc_comment) LIKE '%' || ${"w" + i} || '%' ESCAPE '\\'"));

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id
            FROM symbol s
            LEFT JOIN symbol c ON c.id = s.container_id
            WHERE s.is_definition = 1
              AND s.doc_comment IS NOT NULL{wordFilter}
              {kindFilter}
              {localFilter}
            ORDER BY CASE WHEN {string.Join(" OR ", words.Select((_, i) =>
                $"INSTR(LOWER(s.name), ${"w" + i}) > 0"))} THEN 0 ELSE 1 END,
                     LENGTH(s.doc_comment),
                     s.id
            LIMIT $limit
            """;
        for (var i = 0; i < words.Count; i++)
        {
            command.Parameters.AddWithValue("$w" + i, EscapeForLike(words[i]));
        }

        command.Parameters.AddWithValue("$function", (int)SymbolKind.Function);
        command.Parameters.AddWithValue("$method", (int)SymbolKind.Method);
        command.Parameters.AddWithValue("$limit", options.Limit);

        var ranked = new List<(long Id, int Score, int NameLength)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ranked.Add((reader.GetInt64(0), 0, 0));
            }
        }

        return Hydrate(ranked, int.MinValue, includeDocComments: true, cancellationToken);
    }

    /// <summary>
    /// Neutralises the wildcards in a user's query so LIKE matches it literally. Without
    /// this, searching comments for <c>%</c> returns every commented symbol in the index.
    /// </summary>
    private static string EscapeForLike(string query) => query
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

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
        int strongScoreFloor,
        bool includeDocComments,
        CancellationToken cancellationToken)
    {
        var scoreById = ranked.ToDictionary(r => r.Id, r => r.Score);

        using var command = _connection.CreateCommand();
        // The container name is what tells two same-named members apart in the result
        // list — Device.Send vs Radio.Send — without opening either.
        //
        // The comment is selected as NULL rather than skipped when it was not asked for, so
        // one query shape serves both and the column indices below stay put.
        var docComment = includeDocComments ? "s.doc_comment" : "NULL";
        command.CommandText = $"""
            SELECT s.id, s.name, s.kind, f.rel_path, s.start_line, s.signature, c.name,
                   s.param_text, s.modifiers, s.type_text,
                   ({OverloadSql.Count}) AS overload_count,
                   ({OverloadSql.Ordinal}) AS overload_ordinal,
                   {docComment} AS doc_comment
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
                reader.GetInt32(11),
                scoreById[id] < strongScoreFloor,
                reader.IsDBNull(12) ? null : reader.GetString(12));
        }

        return ranked
            .Select(r => rows.GetValueOrDefault(r.Id))
            .Where(hit => hit is not null)
            .Select(hit => hit!)
            .ToList();
    }
}
