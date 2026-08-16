using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Cli.Querying;

/// <summary>One outline row: a definition at its nesting depth within the file.</summary>
internal sealed record OutlineEntry(
    long Id,
    string Name,
    SymbolKind Kind,
    string? ParameterText,
    string? TypeText,
    string? Value,
    string? Modifiers,
    int Line,
    int Depth);

internal sealed record FileOutline(
    string RelativePath,
    string Language,
    IReadOnlyList<OutlineEntry> Entries)
{
    /// <summary>Files matching an ambiguous path argument; set instead of an outline.</summary>
    public IReadOnlyList<string>? CandidatePaths { get; init; }
}

/// <summary>
/// The definitions of one file in source order, indented by containment — a structs-and
/// -functions table of contents. Locals stay out, same rule as search and the repo map.
/// <para>Callers hold the database gate.</para>
/// </summary>
internal static class FileOutlineService
{
    public static FileOutline? Build(ReadOnlyIndexSession session, string pathText)
    {
        var normalized = pathText.Replace('\\', '/').TrimStart('/');
        var matches = FindFiles(session, normalized);

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1)
        {
            return new FileOutline(normalized, string.Empty, [])
            {
                CandidatePaths = matches.Select(m => m.RelPath).ToList(),
            };
        }

        var (fileId, relPath, language) = matches[0];

        using var command = session.Connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.kind, s.param_text, s.type_text, s.value, s.modifiers,
                   s.start_line, s.container_id,
                   CASE WHEN c.kind IN ($function, $method) THEN 1 ELSE 0 END AS is_local
            FROM symbol s
            LEFT JOIN symbol c ON c.id = s.container_id
            WHERE s.file_id = $fileId AND s.is_definition = 1
            ORDER BY s.start_offset
            """;
        command.Parameters.AddWithValue("$fileId", fileId);
        command.Parameters.AddWithValue("$function", (int)SymbolKind.Function);
        command.Parameters.AddWithValue("$method", (int)SymbolKind.Method);

        var rows = new List<(long Id, string Name, SymbolKind Kind, string? Params, string? Type,
            string? Value, string? Modifiers, int Line, long? ContainerId, bool IsLocal)>();

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    (SymbolKind)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    reader.GetInt32(9) == 1));
            }
        }

        // Depth by walking the container chain within this file. The chain is short
        // (namespace → class → member), so a per-row walk over the dictionary is fine.
        var byId = rows.ToDictionary(r => r.Id);
        var entries = new List<OutlineEntry>(rows.Count);

        foreach (var row in rows)
        {
            if (row.IsLocal)
            {
                continue;
            }

            var depth = 0;
            var containerId = row.ContainerId;
            while (containerId is not null && byId.TryGetValue(containerId.Value, out var container))
            {
                depth++;
                containerId = container.ContainerId;
            }

            entries.Add(new OutlineEntry(
                row.Id, row.Name, row.Kind, row.Params, row.Type, row.Value, row.Modifiers,
                row.Line, depth));
        }

        return new FileOutline(relPath, language, entries);
    }

    private static List<(long FileId, string RelPath, string Language)> FindFiles(
        ReadOnlyIndexSession session, string path)
    {
        using var command = session.Connection.CreateCommand();

        // Seek by final segment first (ix on base_name), then confirm the suffix — the
        // include-resolution idiom, so "uart.c" finds drivers/uart.c without a scan.
        command.CommandText = """
            SELECT id, rel_path, language
            FROM file
            WHERE base_name = $base
              AND (rel_path = $path OR substr(rel_path, -$suffixLen) = '/' || $path)
            ORDER BY rel_path
            LIMIT 20
            """;
        var baseName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        command.Parameters.AddWithValue("$base", baseName);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$suffixLen", path.Length + 1);

        var matches = new List<(long, string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            matches.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return matches;
    }
}
