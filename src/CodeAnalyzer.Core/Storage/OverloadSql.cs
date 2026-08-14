using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// The one definition of what makes two declarations overloads of each other.
/// <para>
/// Three read paths state it — the graph node, the detail pane and the search list — and
/// they have to agree exactly. A node saying "overload 2 of 3" while the pane listed two
/// siblings would be worse than saying nothing, so the predicate is written once here and
/// the three call sites paste in the same text, the way <c>KindLabels</c> is the one
/// vocabulary for the enums.
/// </para>
/// <para>
/// Every fragment below requires the <c>symbol</c> table to be aliased <c>s</c>.
/// </para>
/// </summary>
internal static class OverloadSql
{
    private static readonly string CallableKinds =
        $"{(int)SymbolKind.Function}, {(int)SymbolKind.Method}";

    /// <summary>
    /// Same name, same callable family, same declared scope.
    /// <para>
    /// Scope is the containing symbol where there is one, and the file otherwise. The file
    /// half is what catches C++ free-function overloads and out-of-line definitions; it
    /// deliberately does not reach across files, because C has no overloading and calling
    /// two same-named <c>static</c> helpers in different files an overload set would be an
    /// invention rather than a reading of the source.
    /// </para>
    /// </summary>
    private const string SameScope = """
        o.name = s.name
          AND o.is_definition = 1
          AND (CASE WHEN s.container_id IS NULL
                    THEN o.container_id IS NULL AND o.file_id = s.file_id
                    ELSE o.container_id = s.container_id END)
        """;

    /// <summary>
    /// Guards both counts. A symbol that is not a callable definition has no overload set,
    /// and answering 1 without touching <c>symbol</c> again keeps the subquery off every
    /// class, field and constant the graph draws.
    /// </summary>
    private static string Guard => $"s.kind IN ({CallableKinds}) AND s.is_definition = 1";

    /// <summary>
    /// How many definitions share this one's name in its own scope; 1 when the name is not
    /// overloaded. Seeks through <c>ix_symbol_lookup(name, is_definition, kind)</c>.
    /// </summary>
    public static string Count => $"""
        CASE WHEN {Guard} THEN (
            SELECT COUNT(*) FROM symbol o
            WHERE {SameScope}
              AND o.kind IN ({CallableKinds})
        ) ELSE 1 END
        """;

    /// <summary>
    /// This definition's 1-based position in that set, ordered by where it is written.
    /// The id breaks a tie so the ordinal is stable across a re-parse of an unchanged file
    /// rather than shifting under the user between two same-line declarations.
    /// </summary>
    public static string Ordinal => $"""
        CASE WHEN {Guard} THEN (
            SELECT COUNT(*) FROM symbol o
            WHERE {SameScope}
              AND o.kind IN ({CallableKinds})
              AND (o.start_line < s.start_line
                   OR (o.start_line = s.start_line AND o.id <= s.id))
        ) ELSE 1 END
        """;

    /// <summary>
    /// Every definition in the set, current one included, in the same order the ordinal
    /// counts. Driven from a chosen symbol id, so it carries its own <c>s</c>.
    /// </summary>
    public static string Siblings => $"""
        SELECT o.id, o.signature, o.param_text, o.start_line
        FROM symbol s
        JOIN symbol o ON {SameScope} AND o.kind IN ({CallableKinds})
        WHERE s.id = $symbolId AND {Guard}
        ORDER BY o.start_line, o.id
        """;
}
