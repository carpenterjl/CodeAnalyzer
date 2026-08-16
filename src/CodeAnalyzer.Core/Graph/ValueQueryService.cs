using System.Text;
using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Graph;

/// <summary>One definition whose literal denotes the same value as another's.</summary>
public sealed record ValueMatch(
    long SymbolId,
    string Name,
    SymbolKind Kind,
    string? ContainerName,
    string? Verbatim,
    long? Number,
    string? Text,
    string Language,
    string RelativePath,
    int Line)
{
    /// <summary>
    /// Why this row is here, in the vocabulary <see cref="ValueFacts"/> owns: the literal
    /// as written, the shared value where the two differ in form, and the relation claimed.
    /// </summary>
    public string EqualityNote { get; } = ValueFacts.EqualityNote(Verbatim, Number, Text);

    /// <summary>What this symbol is, for a search row: <c>value 0xA5 (= 165)</c>.</summary>
    public string Descriptor { get; } = ValueFacts.Descriptor(Verbatim, Number, Text);
}

/// <summary>
/// Definitions sharing one value, with the honesty flag that says the list was cut.
/// </summary>
public sealed record ValueMatchSet
{
    public required IReadOnlyList<ValueMatch> Matches { get; init; }

    /// <summary>The value both sides share, in its plain form: <c>165</c> or <c>"COM3"</c>.</summary>
    public required string Canonical { get; init; }

    /// <summary>More definitions carry this value than the limit allowed through.</summary>
    public bool Truncated { get; init; }

    /// <summary>How many were shown, when <see cref="Truncated"/> is set.</summary>
    public int Limit { get; init; }

    /// <summary>Languages represented, the selected symbol's own excluded.</summary>
    public IReadOnlyList<string> OtherLanguages { get; init; } = [];
}

/// <summary>One value and every definition that carries it, for the constants view.</summary>
public sealed record ValueGroup(
    string Canonical,
    long? Number,
    string? Text,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> TopDirectories,
    IReadOnlyList<ValueMatch> Members,
    int TotalCount);

/// <summary>
/// Reads the index by what a literal <em>denotes</em> rather than by what it is called.
/// <para>
/// This is the query behind the question a protocol raises and the call graph cannot
/// answer: a command byte written <c>0xA5</c> in the C# that sends it, <c>165</c> in the C
/// that receives it and <c>8'hA5</c> in the RTL that decodes it is one agreement spelled
/// three ways, and no reference connects the three.
/// </para>
/// <para>
/// The claim stays narrow on purpose. Two symbols appear together because their literals
/// denote the same number or the same characters — a shared value is evidence, not proof of
/// a relationship, and the wording in <see cref="ValueFacts"/> says exactly that.
/// </para>
/// </summary>
public sealed class ValueQueryService(SqliteConnection connection)
{
    /// <summary>
    /// Definitions per answer. A round number like 0 or 1 is carried by hundreds of
    /// symbols, and past a screenful the list stops being readable — so it is cut, and
    /// the cut is stated rather than hidden.
    /// </summary>
    public int MaxMatches { get; init; } = 50;

    /// <summary>Groups the constants view reads at once.</summary>
    public int MaxGroups { get; init; } = 200;

    /// <summary>Definitions listed inside one constants-view group.</summary>
    public int MaxGroupMembers { get; init; } = 12;

    /// <summary>
    /// Columns every match is read from. One list, because three queries hydrate the same
    /// record and a fourth column order would be a silent field swap.
    /// </summary>
    private const string MatchColumns = """
        SELECT s.id, s.name, s.kind, c.name, s.value, s.value_num, s.value_str,
               s.language, f.rel_path, s.start_line
        FROM symbol s
        JOIN file f ON f.id = s.file_id
        LEFT JOIN symbol c ON c.id = s.container_id
        """;

    /// <summary>
    /// Every other definition whose literal denotes what this symbol's literal denotes,
    /// or null when this symbol carries no value the parser could certify.
    /// <para>
    /// Cross-language matches come first: they are the ones no other view can find, and
    /// the reason this query exists.
    /// </para>
    /// </summary>
    public ValueMatchSet? GetSameValue(long symbolId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long? number;
        string? text;
        string language;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value_num, value_str, language FROM symbol WHERE id = $id";
            command.Parameters.AddWithValue("$id", symbolId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            number = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            text = reader.IsDBNull(1) ? null : reader.GetString(1);
            language = reader.GetString(2);
        }

        if (number is null && text is null)
        {
            return null;
        }

        var matches = ReadMatches(number, text, symbolId, language, MaxMatches + 1, cancellationToken);
        var truncated = matches.Count > MaxMatches;
        if (truncated)
        {
            matches.RemoveRange(MaxMatches, matches.Count - MaxMatches);
        }

        if (matches.Count == 0)
        {
            return null;
        }

        return new ValueMatchSet
        {
            Matches = matches,
            Canonical = ValueFacts.Canonical(number, text) ?? string.Empty,
            Truncated = truncated,
            Limit = MaxMatches,
            OtherLanguages = matches
                .Select(m => m.Language)
                .Where(l => l != language)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
        };
    }

    /// <summary>
    /// Every definition carrying a value typed into the search box. Returns null when the
    /// text is not a literal this parser can read — the caller says so rather than falling
    /// back to a name search the user did not ask for.
    /// </summary>
    public ValueMatchSet? FindByValue(
        string query,
        int limit,
        IReadOnlySet<SymbolKind>? kinds = null,
        CancellationToken cancellationToken = default)
    {
        var value = LiteralValueParser.ParseQuery(query);
        if (!value.HasValue)
        {
            return null;
        }

        var matches = ReadMatches(value.Number, value.Text, null, null, limit + 1, cancellationToken, kinds);
        var truncated = matches.Count > limit;
        if (truncated)
        {
            matches.RemoveRange(limit, matches.Count - limit);
        }

        return new ValueMatchSet
        {
            Matches = matches,
            Canonical = ValueFacts.Canonical(value.Number, value.Text) ?? string.Empty,
            Truncated = truncated,
            Limit = limit,
            OtherLanguages = matches
                .Select(m => m.Language)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
        };
    }

    /// <summary>
    /// Values carried by definitions in more than one language (or, when
    /// <paramref name="acrossDirectories"/> is set, in more than one top-level directory).
    /// <para>
    /// Ordered by how many languages share the value, then by how few definitions carry it:
    /// a command byte written in three languages by five symbols is a stronger signal than
    /// <c>0</c> written by nine hundred, and this puts it first without hiding either.
    /// </para>
    /// </summary>
    /// <param name="acrossDirectories">
    /// Span top-level directories rather than languages — the same question for a workspace
    /// whose halves are written in one language.
    /// </param>
    /// <param name="includeTrivial">
    /// Keep 0 and 1. They are values like any other and are excluded only when the caller
    /// says so, never silently.
    /// </param>
    public IReadOnlyList<ValueGroup> GetSharedValues(
        bool acrossDirectories = false,
        bool includeTrivial = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var spanColumn = acrossDirectories ? "f.top_dir" : "s.language";
        var trivialFilter = includeTrivial
            ? string.Empty
            : "AND (s.value_num IS NULL OR s.value_num NOT IN (0, 1))";

        var groups = new List<(long? Number, string? Text, int Total, string Languages, string Directories)>();

        using (var command = connection.CreateCommand())
        {
            // One aggregation over the partial indexes. The GROUP BY key is the stored
            // value itself, so two spellings of one number are already the same group by
            // the time SQLite sees them.
            //
            // The language and directory lists are aggregated here rather than derived
            // from the members hydrated below: those are a capped sample, and a group
            // whose sample happens to be all C would otherwise claim to span one language
            // while sitting under a "two languages" heading.
            command.CommandText = $"""
                SELECT s.value_num, s.value_str, COUNT(*) AS total,
                       COUNT(DISTINCT {spanColumn}) AS spans,
                       GROUP_CONCAT(DISTINCT s.language) AS languages,
                       GROUP_CONCAT(DISTINCT f.top_dir) AS directories
                FROM symbol s
                JOIN file f ON f.id = s.file_id
                WHERE s.is_definition = 1
                  AND (s.value_num IS NOT NULL OR s.value_str IS NOT NULL)
                  {trivialFilter}
                GROUP BY s.value_num, s.value_str
                HAVING spans >= 2
                ORDER BY spans DESC, total ASC, s.value_num, s.value_str
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", MaxGroups);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                groups.Add((
                    reader.IsDBNull(0) ? null : reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5)));
            }
        }

        var result = new List<ValueGroup>(groups.Count);

        foreach (var (number, text, total, languages, directories) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var members = ReadMatches(
                number, text, null, null, MaxGroupMembers, cancellationToken, oneLanguageAtATime: true);

            result.Add(new ValueGroup(
                ValueFacts.Canonical(number, text) ?? string.Empty,
                number,
                text,
                Split(languages, StringComparer.Ordinal),
                Split(directories, StringComparer.OrdinalIgnoreCase).Select(d => d).ToList(),
                members,
                total));
        }

        return result;
    }

    /// <summary>Unpacks a GROUP_CONCAT list, which SQLite joins with a bare comma.</summary>
    private static List<string> Split(string concatenated, StringComparer order) =>
        concatenated.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(order)
            .Order(order)
            .ToList();

    /// <summary>
    /// Definitions carrying one value. <paramref name="excludeSymbolId"/> leaves the symbol
    /// being asked about out of its own answer; <paramref name="ownLanguage"/> sorts the
    /// languages it cannot reach on its own to the top.
    /// </summary>
    private List<ValueMatch> ReadMatches(
        long? number,
        string? text,
        long? excludeSymbolId,
        string? ownLanguage,
        int limit,
        CancellationToken cancellationToken,
        IReadOnlySet<SymbolKind>? kinds = null,
        bool oneLanguageAtATime = false)
    {
        var matches = new List<ValueMatch>();

        using var command = connection.CreateCommand();

        var where = new StringBuilder();

        // One of the two partial indexes drives the seek; the other column is NULL for
        // every row that could match, so it needs no predicate of its own.
        if (number is not null)
        {
            where.Append("WHERE s.value_num = $number");
            command.Parameters.AddWithValue("$number", number.Value);
        }
        else
        {
            where.Append("WHERE s.value_str = $text");
            command.Parameters.AddWithValue("$text", text);
        }

        where.Append(" AND s.is_definition = 1");

        if (excludeSymbolId is not null)
        {
            where.Append(" AND s.id <> $exclude");
            command.Parameters.AddWithValue("$exclude", excludeSymbolId.Value);
        }

        // The kind chips above the results mean the same thing here as they do for a name
        // search. Applied in SQL rather than to the returned page, so the truncation flag
        // still counts what the user asked for.
        if (kinds is { Count: > 0 })
        {
            where.Append(" AND s.kind IN (").AppendJoin(',', kinds.Select(k => (int)k)).Append(')');
        }

        // Cross-language first, then a stable path/line order so two runs of the same
        // query print the same list.
        var order = ownLanguage is null
            ? "ORDER BY s.language, f.rel_path, s.start_line, s.id"
            : "ORDER BY (s.language = $ownLanguage), s.language, f.rel_path, s.start_line, s.id";

        if (ownLanguage is not null)
        {
            command.Parameters.AddWithValue("$ownLanguage", ownLanguage);
        }

        if (oneLanguageAtATime)
        {
            // A capped list of a value carried by two hundred vendor macros and one C#
            // constant would be all macros — and the C# one is the entire reason the
            // group is on screen. Taking each language's first definition before any
            // language's second puts the crossing in the sample.
            command.CommandText = $"""
                SELECT id, name, kind, container_name, value, value_num, value_str,
                       language, rel_path, start_line
                FROM (
                    SELECT s.id, s.name, s.kind, c.name AS container_name, s.value,
                           s.value_num, s.value_str, s.language, f.rel_path, s.start_line,
                           ROW_NUMBER() OVER (
                               PARTITION BY s.language
                               ORDER BY f.rel_path, s.start_line, s.id) AS rank_in_language
                    FROM symbol s
                    JOIN file f ON f.id = s.file_id
                    LEFT JOIN symbol c ON c.id = s.container_id
                    {where}
                )
                ORDER BY rank_in_language, language, rel_path, start_line, id
                LIMIT $limit
                """;
        }
        else
        {
            command.CommandText = $"""
                {MatchColumns}
                {where}
                {order}
                LIMIT $limit
                """;
        }

        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            matches.Add(new ValueMatch(
                reader.GetInt64(0),
                reader.GetString(1),
                (SymbolKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9)));
        }

        return matches;
    }

    private static string TopDirectory(string relativePath)
    {
        var slash = relativePath.IndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }
}
