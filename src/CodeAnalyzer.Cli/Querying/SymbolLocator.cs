using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Search;

namespace CodeAnalyzer.Cli.Querying;

/// <summary>One definition a locate attempt found, with enough context to pick it from a list.</summary>
internal sealed record LocatedSymbol(
    long Id,
    string Name,
    SymbolKind Kind,
    string? ParameterText,
    string RelativePath,
    int Line);

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
            var more = candidates.Count > MaxCandidates;
            return new LocateResult.Ambiguous(candidates.Take(MaxCandidates).ToList(), more);
        }

        var where = pathFilter is null ? string.Empty : $" in {pathFilter}";
        return new LocateResult.NotFound(
            $"no definition named '{name}'{where} in the index",
            Suggest(session, name, cancellationToken));
    }

    private static LocateResult LocateById(ReadOnlyIndexSession session, string symbolText)
    {
        if (!long.TryParse(symbolText[1..], out var id))
        {
            return new LocateResult.NotFound($"'{symbolText}' is not a symbol id", []);
        }

        using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.kind, s.param_text, f.rel_path, s.start_line
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
            SELECT s.id, s.name, s.kind, s.param_text, f.rel_path, s.start_line
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
        reader.GetInt32(5));
}
