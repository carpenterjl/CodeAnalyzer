using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// Reads what a symbol is built from: members, instantiations and type relations.
/// <para>
/// This is the query behind the composition inspector, which answers a different question
/// from the call graph. A struct's fields, a Verilog module's ports and a class's methods
/// are not edges anybody calls; they are the shape of the thing, and they read far better
/// as a list of cards than as a ring of nodes.
/// </para>
/// </summary>
public sealed class CompositionQueryService(SqliteConnection connection)
{
    /// <summary>Cap on each relation list. Members are not capped: they are the point.</summary>
    public int MaxRelations { get; init; } = 100;

    public CompositionView? GetComposition(long symbolId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CompositionView view;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.id, s.name, s.kind, f.rel_path, s.language, s.start_line, s.end_line,
                       s.signature, s.value, s.type_text, s.scope_path,
                       s.container_id, c.name, s.modifiers
                FROM symbol s
                JOIN file f ON f.id = s.file_id
                LEFT JOIN symbol c ON c.id = s.container_id
                WHERE s.id = $symbolId
                """;
            command.Parameters.AddWithValue("$symbolId", symbolId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            view = new CompositionView
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Kind = (SymbolKind)reader.GetInt32(2),
                RelativePath = reader.GetString(3),
                Language = reader.GetString(4),
                StartLine = reader.GetInt32(5),
                EndLine = reader.GetInt32(6),
                Signature = reader.IsDBNull(7) ? null : reader.GetString(7),
                Value = reader.IsDBNull(8) ? null : reader.GetString(8),
                TypeText = reader.IsDBNull(9) ? null : reader.GetString(9),
                ScopePath = reader.GetString(10),
                ContainerId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                ContainerName = reader.IsDBNull(12) ? null : reader.GetString(12),
                Modifiers = reader.IsDBNull(13) ? null : reader.GetString(13),
            };
        }

        cancellationToken.ThrowIfCancellationRequested();

        return view with
        {
            Members = LoadMembers(symbolId),
            Instantiates = LoadOutgoing(symbolId, ReferenceKind.Instantiate),
            InstantiatedBy = LoadIncoming(symbolId, ReferenceKind.Instantiate),
            BaseTypes = LoadOutgoing(symbolId, ReferenceKind.Inherit),
            DerivedTypes = LoadIncoming(symbolId, ReferenceKind.Inherit),
        };
    }

    /// <summary>
    /// Direct members in source order, each with the two counts that make a card worth
    /// reading on its own: how many members it has, and how many places reference it.
    /// </summary>
    private List<CompositionMember> LoadMembers(long symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.name, m.kind, m.type_text, m.value, m.start_line,
                   (SELECT COUNT(*) FROM symbol c WHERE c.container_id = m.id),
                   (SELECT COUNT(*) FROM edge e WHERE e.target_symbol_id = m.id),
                   m.modifiers
            FROM symbol m
            WHERE m.container_id = $symbolId
            ORDER BY m.start_offset
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);

        var results = new List<CompositionMember>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CompositionMember(
                reader.GetInt64(0),
                reader.GetString(1),
                (SymbolKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return results;
    }

    /// <summary>
    /// Relations of one kind made by this symbol <em>or by its direct members</em>.
    /// <para>
    /// The wider scope is what makes the view useful: a Verilog module usually carries its
    /// instantiations itself, but a C++ class constructs things from inside its methods,
    /// and "this class builds a Frame" belongs on the class card either way.
    /// </para>
    /// <para>
    /// Unresolved references are kept, with a null target. An ambiguous one contributes a
    /// row per candidate, which is the same admission the graph makes with a dashed edge.
    /// </para>
    /// </summary>
    private List<CompositionLink> LoadOutgoing(long symbolId, ReferenceKind kind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.target_symbol_id, r.name, t.kind, f.rel_path, r.line, r.kind, e.confidence
            FROM ref r
            LEFT JOIN edge e ON e.ref_id = r.id
            LEFT JOIN symbol t ON t.id = e.target_symbol_id
            LEFT JOIN file f ON f.id = t.file_id
            WHERE r.kind = $kind
              AND (r.from_symbol_id = $symbolId
                   OR r.from_symbol_id IN (SELECT id FROM symbol WHERE container_id = $symbolId))
              AND (e.target_symbol_id IS NULL OR e.target_symbol_id <> $symbolId)
            ORDER BY r.line, r.name
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$limit", MaxRelations);

        return ReadLinks(command, targetIdColumn: 0, nameColumn: 1);
    }

    private List<CompositionLink> LoadIncoming(long symbolId, ReferenceKind kind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.kind, f.rel_path, r.line, r.kind, e.confidence
            FROM edge e
            JOIN ref r ON r.id = e.ref_id
            JOIN symbol s ON s.id = r.from_symbol_id
            JOIN file f ON f.id = s.file_id
            WHERE e.target_symbol_id = $symbolId
              AND r.kind = $kind
              AND s.id <> $symbolId
            ORDER BY s.name
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$limit", MaxRelations);

        return ReadLinks(command, targetIdColumn: 0, nameColumn: 1);
    }

    private static List<CompositionLink> ReadLinks(SqliteCommand command, int targetIdColumn, int nameColumn)
    {
        var results = new List<CompositionLink>();
        var seen = new HashSet<(long?, string, int)>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long? targetId = reader.IsDBNull(targetIdColumn) ? null : reader.GetInt64(targetIdColumn);
            var name = reader.GetString(nameColumn);
            var line = reader.GetInt32(4);

            // The same instantiation written twice on one line is one fact.
            if (!seen.Add((targetId, name, line)))
            {
                continue;
            }

            results.Add(new CompositionLink(
                targetId,
                name,
                reader.IsDBNull(2) ? null : (SymbolKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                line,
                (ReferenceKind)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : (EdgeConfidence)reader.GetInt32(6)));
        }

        return results;
    }
}
