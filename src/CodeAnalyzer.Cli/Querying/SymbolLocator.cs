using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Search;

namespace CodeAnalyzer.Cli.Querying;

/// <summary>
/// One definition a locate attempt found, with enough context to pick it from a list.
/// <paramref name="ContainerId"/> is the declaring symbol, and it is what lets a type absorb
/// its own constructors instead of competing with them — see
/// <see cref="SymbolLocator.TypeContainingEveryOtherCandidate"/>.
/// </summary>
internal sealed record LocatedSymbol(
    long Id,
    string Name,
    SymbolKind Kind,
    string? ParameterText,
    string RelativePath,
    int Line,
    long? ContainerId = null);

/// <summary>
/// The three-way answer to "which symbol did you mean". Ambiguity is a first-class result,
/// not an error dressed as a guess: the caller lists the candidates with their ids and the
/// user (or agent) picks — the same rule the resolver applies to its own edges.
/// </summary>
internal abstract record LocateResult
{
    public sealed record Resolved(LocatedSymbol Symbol) : LocateResult;

    /// <summary><paramref name="More"/> says the candidate list itself was capped.</summary>
    public sealed record Ambiguous(IReadOnlyList<LocatedSymbol> Candidates, bool More) : LocateResult;

    public sealed record NotFound(string Message, IReadOnlyList<LocatedSymbol> Suggestions) : LocateResult;
}

/// <summary>
/// Turns a command-line symbol argument into a symbol id. Three spellings:
/// <c>#123</c> (an id a previous result printed), <c>name</c> (exact definition name), or
/// <c>path/to/file.c:name</c> (the same, confined to one file — the path may be the full
/// relative path or any suffix of it).
/// <para>Callers hold the database gate; everything here is plain connection work.</para>
/// </summary>
internal static class SymbolLocator
{
    /// <summary>Candidates listed before the list itself is capped and says so.</summary>
    public const int MaxCandidates = 20;

    private const int SuggestionCount = 5;

    public static LocateResult Locate(
        ReadOnlyIndexSession session, string symbolText, CancellationToken cancellationToken = default)
    {
        symbolText = symbolText.Trim();

        if (symbolText.StartsWith('#'))
        {
            return LocateById(session, symbolText);
        }

        string? pathFilter = null;
        var name = symbolText;

        var colon = symbolText.LastIndexOf(':');
        if (colon > 0 && colon < symbolText.Length - 1)
        {
            pathFilter = symbolText[..colon].Replace('\\', '/');
            name = symbolText[(colon + 1)..];
        }

        var candidates = QueryByName(session, name, pathFilter, containerFilter: null);

        // "Container.Member" is how search results and the GUI display contained symbols,
        // so accept it back: the last segment is the name, the one before it the container.
        if (candidates.Count == 0 && name.Contains('.'))
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1)
            {
                candidates = QueryByName(session, name[(dot + 1)..], pathFilter, name[..dot]);
            }
        }

        if (candidates.Count == 1)
        {
            return new LocateResult.Resolved(candidates[0]);
        }

        if (candidates.Count > 1)
        {
            if (TypeContainingEveryOtherCandidate(candidates) is { } owner)
            {
                return new LocateResult.Resolved(owner);
            }

            var more = candidates.Count > MaxCandidates;
            return new LocateResult.Ambiguous(candidates.Take(MaxCandidates).ToList(), more);
        }

        var where = pathFilter is null ? string.Empty : $" in {pathFilter}";

        // Suggest on the member name alone when the argument was "Container.Member".
        // The fuzzy scorer matches against bare symbol names, so handing it the dotted
        // form scores nothing and a wrong guess comes back with no help at all — which is
        // the worst answer available, since the near miss is usually a sibling member.
        var suggestFor = name;
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0 && lastDot < name.Length - 1)
        {
            suggestFor = name[(lastDot + 1)..];
        }

        return new LocateResult.NotFound(
            $"no definition named '{name}'{where} in the index",
            Suggest(session, suggestFor, cancellationToken));
    }

    /// <summary>
    /// The one ambiguity worth settling rather than reporting: a type competing with the
    /// members it declares. A C# or C++ constructor necessarily shares its type's name, so
    /// every type with a declared constructor was answering "2 definitions — pass an id"
    /// and costing a round trip to learn something the source never left in doubt.
    /// <para>
    /// The test is containment, not constructor-ness. The index stores no constructor kind —
    /// the packs record one as a method — so deciding by "its name equals its container's"
    /// would be reading meaning into a name. Containment is a stored fact, and it is the
    /// stronger claim anyway: whatever those members are, the type's own fact sheet already
    /// lists them, so resolving to the type hides nothing.
    /// </para>
    /// <para>
    /// Deliberately narrow, so it can never swallow a real ambiguity. Exactly one candidate
    /// may be a type; every other candidate must be declared by <em>that</em> type; and a
    /// capped candidate list is refused outright, because an unseen row could be a second
    /// type somewhere else in the workspace. Two same-named classes in different files stay
    /// ambiguous, which is correct — nothing about the source says which one was meant.
    /// </para>
    /// </summary>
    public static LocatedSymbol? TypeContainingEveryOtherCandidate(IReadOnlyList<LocatedSymbol> candidates)
    {
        // A capped list is not a list. There may be another type past the cut.
        if (candidates.Count < 2 || candidates.Count > MaxCandidates)
        {
            return null;
        }

        LocatedSymbol? type = null;
        foreach (var candidate in candidates)
        {
            if (!IsType(candidate.Kind))
            {
                continue;
            }

            if (type is not null)
            {
                return null;
            }

            type = candidate;
        }

        if (type is null)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Id != type.Id && candidate.ContainerId != type.Id)
            {
                return null;
            }
        }

        return type;
    }

    // The families the graph already colours by, so this cannot drift from the picture or
    // from the search filters. An interface is its own group there, and both are types here.
    private static bool IsType(SymbolKind kind) =>
        SymbolKindGroups.For(kind) is "type" or "interface";

    private static LocateResult LocateById(ReadOnlyIndexSession session, string symbolText)
    {
        if (!long.TryParse(symbolText[1..], out var id))
        {
            return new LocateResult.NotFound($"'{symbolText}' is not a symbol id", []);
        }

        using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.kind, s.param_text, f.rel_path, s.start_line, s.container_id
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            WHERE s.id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new LocateResult.Resolved(ReadSymbol(reader));
        }

        // Ids do not survive a re-index of their file, so a stale one is expected life,
        // not a mystery — say what happened and what to do.
        return new LocateResult.NotFound(
            $"symbol #{id} is no longer in the index (its file may have been re-indexed) — re-run search",
            []);
    }

    private static List<LocatedSymbol> QueryByName(
        ReadOnlyIndexSession session, string name, string? pathFilter, string? containerFilter)
    {
        using var command = session.Connection.CreateCommand();

        // The name seek does the narrowing (ix_symbol_lookup); the path and container
        // filters then only inspect that handful of rows. Suffix matching via substr
        // avoids LIKE and its escaping rules: "src/drivers/uart.c" is matched by
        // "drivers/uart.c" too.
        command.CommandText = """
            SELECT s.id, s.name, s.kind, s.param_text, f.rel_path, s.start_line, s.container_id
            FROM symbol s
            JOIN file f ON f.id = s.file_id
            WHERE s.name = $name
              AND s.is_definition = 1
              AND ($path IS NULL
                   OR f.rel_path = $path
                   OR substr(f.rel_path, -$suffixLen) = '/' || $path)
              AND ($container IS NULL
                   OR EXISTS (SELECT 1 FROM symbol c WHERE c.id = s.container_id AND c.name = $container))
            ORDER BY f.rel_path, s.start_line
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$path", (object?)pathFilter ?? DBNull.Value);
        command.Parameters.AddWithValue("$suffixLen", (pathFilter?.Length ?? 0) + 1);
        command.Parameters.AddWithValue("$container", (object?)containerFilter ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", MaxCandidates + 1);

        var results = new List<LocatedSymbol>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadSymbol(reader));
        }

        return results;
    }

    private static List<LocatedSymbol> Suggest(
        ReadOnlyIndexSession session, string name, CancellationToken cancellationToken)
    {
        List<SymbolSearchHit> hits;
        try
        {
            hits = session.Search.Search(
                name, new SymbolSearchOptions { Limit = SuggestionCount }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        return hits
            .Select(h => new LocatedSymbol(h.SymbolId, h.Name, h.Kind, h.ParameterText, h.RelativePath, h.Line))
            .ToList();
    }

    private static LocatedSymbol ReadSymbol(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        (SymbolKind)reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6));
}
