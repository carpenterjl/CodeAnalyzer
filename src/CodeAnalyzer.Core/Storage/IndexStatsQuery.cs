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
/// <para>
/// <see cref="External"/> splits the unresolved column one step further: how many of a
/// row's unresolved references name something no workspace definition of a compatible
/// kind carries at all — IDisposable, a runtime's Map, a NuGet namespace. Those are the
/// correct answer, not a gap, and before this column every high-unresolved row read the
/// same whether it was hiding a resolver bug or describing the workspace's honest
/// dependence on code it does not contain. A lower bound, not an exact split: the test
/// is kind-compatibility alone (see <c>ReferenceResolver.CompatibleKindSql</c>), so a
/// name that matches a compatible definition the resolver would still have refused
/// counts as possibly-internal.
/// </para>
/// </summary>
public sealed record ResolutionSplit(
    string Name, int Total, int Unique, int Ambiguous, int Unresolved, int External = 0);

/// <summary>
/// Why one group of references went unresolved, and how many did. The four rules are the
/// resolver's own, applied in the order that makes them exclusive, and they are exhaustive
/// by construction — <see cref="UnresolvedRule.Unexplained"/> exists to prove it rather
/// than to be populated, and a workspace that puts a number there has found a gap in this
/// partition, which is worth more than a tidy table.
/// <para>
/// Naming the rule is the point. "These are external" is a claim about the corpus and is
/// usually right; "the container rule refuses cross-scope locals" is a claim about the
/// code, and only the second can be wrong in a way that hides — a rule that has quietly
/// stopped working produces exactly the same observable as one doing its job, an
/// unresolved reference, and nothing outside the resolver can tell them apart.
/// </para>
/// </summary>
public enum UnresolvedRule
{
    /// <summary>No workspace definition of a compatible kind carries the name at all.</summary>
    External = 0,

    /// <summary>
    /// The receiver is a plain identifier this workspace never declares, so whatever it
    /// denotes lives outside — and no member of it can be a workspace definition either.
    /// Ranked above the hot-name gate because it is the binding constraint: a reference
    /// this rule refuses would be refused whether its name were hot or not.
    /// </summary>
    ReceiverUnknown = 1,

    /// <summary>
    /// More definitions carry the name than <c>MaxCandidatesPerReference</c> allows, and the
    /// reference wrote no receiver, so nothing narrows it to one of them.
    /// </summary>
    TooCommon = 2,

    /// <summary>
    /// A hot name written against a receiver, admitted for that reason, but the receiver's
    /// type is not known — or the type it names holds no member of this name.
    /// </summary>
    ReceiverNotTyped = 3,

    /// <summary>
    /// Every definition of the name is a local, or a member of a scope this reference is not
    /// written inside — the container rule that keeps every loop counter out of the graph.
    /// </summary>
    OutOfScope = 4,

    /// <summary>Refused by no rule above. Expected to be zero; a gap in the partition if not.</summary>
    Unexplained = 5,
}

/// <summary>One rule and the number of unresolved references it accounts for.</summary>
public sealed record RefusalCount(UnresolvedRule Rule, int Count);

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
    /// <summary>
    /// The unresolved column of the headline triple, split by the rule that refused it.
    /// Sums to <see cref="RefsUnresolved"/> less the include and import references, which
    /// this partition does not cover because the resolver never attempts them as symbols —
    /// they are settled against <c>file_dep</c> and counted there.
    /// </summary>
    IReadOnlyList<RefusalCount> UnresolvedByRule,
    /// <summary>
    /// How many references resolved <em>only</em> by a cross-language name match. Reported
    /// beside the resolution triple because it qualifies it: measured on this workspace,
    /// not one of them also carried a stronger edge, so each is the sole answer its
    /// reference has, and a reader counting resolved references is counting these too.
    /// </summary>
    int RefsOnlyCrossLanguage,
    int TotalEdges,
    IReadOnlyList<NamedCount> EdgesByConfidence,
    int TotalDeps,
    int ResolvedDeps,
    long DatabaseBytes,
    /// <summary>
    /// The subtree every count was narrowed to, forward-slashed and normalised, or null when
    /// the whole workspace was measured. Carried here so the one place that decides what a
    /// scope means — <c>.</c> is the whole workspace, not a path — is also the place the
    /// formatters read, rather than each re-deciding it.
    /// </summary>
    string? ScopePath = null);

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
    public static IndexStats Read(SqliteConnection connection, string? pathScope = null)
    {
        var scope = FileScope.For(pathScope);

        var filesByLanguage = ReadPairs(connection,
            $"SELECT language, COUNT(*) FROM file{scope.WhereFileIn("id")} "
            + "GROUP BY language ORDER BY COUNT(*) DESC, language", scope);

        var symbolsByKind = ReadPairs(connection,
            "SELECT kind, COUNT(*) FROM symbol WHERE is_definition = 1"
            + $"{scope.AndFileIn("file_id")} GROUP BY kind ORDER BY COUNT(*) DESC",
            scope, kind => ((SymbolKind)kind).ToString());

        var edgesByConfidence = ReadPairs(connection,
            $"SELECT confidence, COUNT(*) FROM edge{scope.WhereFileIn("src_file_id")} "
            + "GROUP BY confidence ORDER BY confidence",
            scope, confidence => ((EdgeConfidence)confidence).ToString());

        // Every count is scoped by the *source* side — the file a reference, symbol, edge or
        // dependency was written in — because a reference resolves against definitions
        // anywhere, so "of what this subtree holds, how much resolves" is the only question a
        // path can answer. The database size is the one physical fact a subtree cannot narrow.
        var scalars = ReadScalars(connection, $"""
            SELECT
                (SELECT COUNT(*) FROM file{scope.WhereFileIn("id")}),
                (SELECT COUNT(*) FROM file WHERE status <> 0{scope.AndFileIn("id")}),
                (SELECT COUNT(*) FROM symbol WHERE is_definition = 1{scope.AndFileIn("file_id")}),
                (SELECT COUNT(*) FROM ref{scope.WhereFileIn("file_id")}),
                (SELECT COUNT(*) FROM ref WHERE receiver_text IS NOT NULL{scope.AndFileIn("file_id")}),
                (SELECT COUNT(*) FROM ref WHERE arg_text IS NOT NULL{scope.AndFileIn("file_id")}),
                (SELECT COUNT(*) FROM edge{scope.WhereFileIn("src_file_id")}),
                (SELECT COUNT(*) FROM file_dep{scope.WhereFileIn("file_id")}),
                (SELECT COUNT(*) FROM file_dep WHERE dep_file_id IS NOT NULL{scope.AndFileIn("file_id")}),
                (SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size())
            """, scope);

        // Per-reference fan-out, computed once: how many refs landed on exactly one
        // definition, on several, and on none at all. Scoped by the edge's source file, which
        // is the reference's own file, so a whole reference is kept or dropped together.
        int unique, ambiguous;
        long ambiguousEdges;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT COALESCE(SUM(n = 1), 0),
                       COALESCE(SUM(n > 1), 0),
                       COALESCE(SUM(CASE WHEN n > 1 THEN n ELSE 0 END), 0)
                FROM (SELECT COUNT(*) AS n FROM edge{scope.WhereFileIn("src_file_id")} GROUP BY ref_id)
                """;
            scope.Bind(command);
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
                  AND d.dep_file_id IS NOT NULL{scope.AndFileIn("r.file_id")}
                """;
            scope.Bind(command);
            unique += Convert.ToInt32(command.ExecuteScalar());
        }

        var refsByKind = ReadSplits(connection,
            "r.kind", "ref r", scope, name => ((ReferenceKind)int.Parse(name)).ToString());

        var refsByLanguage = ReadSplits(connection,
            "f.language", "ref r JOIN file f ON f.id = r.file_id", scope);

        var unresolvedByRule = ReadRefusals(connection, scope);
        var onlyCrossLanguage = ReadCrossLanguageOnly(connection, scope);

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
            UnresolvedByRule: unresolvedByRule,
            RefsOnlyCrossLanguage: onlyCrossLanguage,
            SymbolsByKind: symbolsByKind,
            TotalEdges: (int)scalars[6],
            EdgesByConfidence: edgesByConfidence,
            TotalDeps: (int)scalars[7],
            ResolvedDeps: (int)scalars[8],
            DatabaseBytes: scalars[9],
            ScopePath: scope.Path);
    }

    /// <summary>
    /// Splits the unresolved references by which rule refused them, in the order that makes
    /// the cases exclusive: kind-compatibility first because it is a fact about the corpus
    /// and holds whatever the resolver does, then the unknown-receiver rule, then the
    /// hot-name gate, then the container rule.
    /// <para>
    /// Deliberately built from what this side can already see — kinds, containers, how many
    /// definitions carry the name, whether a receiver was written — and nothing else. The
    /// tempting fifth question, "would the receiver have typed?", needs the resolver's own
    /// four-rank walk, and a reporting query that re-implements resolution is a second copy
    /// that drifts. So a hot reference carrying a receiver is reported as one bucket:
    /// admitted by the gate, and refused afterwards because the receiver named no type
    /// holding that member.
    /// </para>
    /// </summary>
    private static List<RefusalCount> ReadRefusals(SqliteConnection connection, FileScope scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH hot(name) AS (
                SELECT name FROM symbol WHERE is_definition = 1
                GROUP BY name HAVING COUNT(*) > {Resolution.ReferenceResolver.DefaultMaxCandidates}
            )
            SELECT CASE
                WHEN NOT EXISTS (SELECT 1 FROM symbol s
                                 WHERE s.name = r.name AND s.is_definition = 1
                                   AND {Resolution.ReferenceResolver.CompatibleKindSql("r", "s")})
                    THEN {(int)UnresolvedRule.External}
                WHEN {Resolution.ReferenceResolver.UnknownReceiverSql("r")}
                    THEN {(int)UnresolvedRule.ReceiverUnknown}
                WHEN EXISTS (SELECT 1 FROM hot WHERE hot.name = r.name)
                    THEN CASE WHEN r.receiver_text IS NULL OR r.receiver_text = ''
                              THEN {(int)UnresolvedRule.TooCommon}
                              ELSE {(int)UnresolvedRule.ReceiverNotTyped} END
                WHEN NOT EXISTS (SELECT 1 FROM symbol s
                                 WHERE s.name = r.name AND s.is_definition = 1
                                   AND {Resolution.ReferenceResolver.CompatibleKindSql("r", "s")}
                                   AND {Resolution.ReferenceResolver.AddressableSql("r", "s")})
                    THEN {(int)UnresolvedRule.OutOfScope}
                ELSE {(int)UnresolvedRule.Unexplained}
            END AS rule, COUNT(*)
            FROM ref r
            WHERE NOT EXISTS (SELECT 1 FROM edge e WHERE e.ref_id = r.id)
              AND r.kind NOT IN ({(int)ReferenceKind.Include}, {(int)ReferenceKind.Import})
              {scope.AndFileIn("r.file_id")}
            GROUP BY rule
            ORDER BY rule
            """;
        scope.Bind(command);

        var counts = new Dictionary<UnresolvedRule, int>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                counts[(UnresolvedRule)reader.GetInt32(0)] = reader.GetInt32(1);
            }
        }

        // Every rule is listed even at zero. A partition that prints only its non-empty rows
        // reads as if the missing ones were never asked, and the whole point of the block is
        // that the four questions were all asked of every unresolved reference.
        return Enum.GetValues<UnresolvedRule>()
            .Select(rule => new RefusalCount(rule, counts.GetValueOrDefault(rule)))
            .ToList();
    }

    /// <summary>
    /// References whose only edge is a cross-language name match. Counted per reference, not
    /// per edge, because the question it answers is "how much of the resolved column rests on
    /// this" — and a reference with four Weak edges is still one reference resting on it.
    /// </summary>
    private static int ReadCrossLanguageOnly(SqliteConnection connection, FileScope scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM (
                SELECT e.ref_id FROM edge e{scope.WhereFileIn("e.src_file_id")}
                GROUP BY e.ref_id
                HAVING MIN(e.confidence) = {(int)EdgeConfidence.Weak}
            )
            """;
        scope.Bind(command);
        return Convert.ToInt32(command.ExecuteScalar());
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
        SqliteConnection connection, string groupBy, string from,
        FileScope scope, Func<string, string>? nameOf = null)
    {
        var fileScoped = $"{(int)ReferenceKind.Include}, {(int)ReferenceKind.Import}";
        using var command = connection.CreateCommand();

        // The external column asks, per unresolved reference, whether any workspace
        // definition of a kind this reference could resolve to carries its name. For an
        // include or import the file_dep miss already IS that test — a dependency that
        // names no workspace file is external by construction — so those count whole.
        command.CommandText = $"""
            SELECT {groupBy},
                   COUNT(*),
                   COALESCE(SUM(CASE WHEN r.kind IN ({fileScoped})
                                     THEN (CASE WHEN d.dep_file_id IS NOT NULL THEN 1 ELSE 0 END)
                                     ELSE (CASE WHEN e.n = 1 THEN 1 ELSE 0 END) END), 0),
                   COALESCE(SUM(CASE WHEN r.kind IN ({fileScoped})
                                     THEN 0
                                     ELSE (CASE WHEN e.n > 1 THEN 1 ELSE 0 END) END), 0),
                   COALESCE(SUM(CASE
                       WHEN r.kind IN ({fileScoped})
                           THEN (CASE WHEN d.dep_file_id IS NULL THEN 1 ELSE 0 END)
                       WHEN e.n IS NOT NULL THEN 0
                       WHEN NOT EXISTS (SELECT 1 FROM symbol s
                                        WHERE s.name = r.name AND s.is_definition = 1
                                          AND {Resolution.ReferenceResolver.CompatibleKindSql("r", "s")})
                           THEN 1
                       ELSE 0 END), 0)
            FROM {from}
            LEFT JOIN (SELECT ref_id, COUNT(*) AS n FROM edge GROUP BY ref_id) e ON e.ref_id = r.id
            LEFT JOIN file_dep d ON d.file_id = r.file_id AND d.dep_path = r.name{scope.WhereFileIn("r.file_id")}
            GROUP BY {groupBy}
            ORDER BY COUNT(*) DESC
            """;
        scope.Bind(command);

        var splits = new List<ResolutionSplit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var raw = reader.GetValue(0)?.ToString() ?? string.Empty;
            var total = reader.GetInt32(1);
            var unique = reader.GetInt32(2);
            var ambiguous = reader.GetInt32(3);
            var external = reader.GetInt32(4);
            splits.Add(new ResolutionSplit(
                nameOf is null ? raw : nameOf(raw),
                total, unique, ambiguous, total - unique - ambiguous, external));
        }

        return splits;
    }

    private static List<NamedCount> ReadPairs(
        SqliteConnection connection, string sql, FileScope scope, Func<int, string>? nameOf = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        scope.Bind(command);

        var pairs = new List<NamedCount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = nameOf is null ? reader.GetString(0) : nameOf(reader.GetInt32(0));
            pairs.Add(new NamedCount(name, reader.GetInt32(1)));
        }

        return pairs;
    }

    private static long[] ReadScalars(SqliteConnection connection, string sql, FileScope scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        scope.Bind(command);
        using var reader = command.ExecuteReader();
        reader.Read();

        var values = new long[reader.FieldCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.GetInt64(i);
        }

        return values;
    }

    /// <summary>
    /// An optional restriction of the whole report to one subtree. A scope is a
    /// forward-slashed, workspace-relative path that names either a single file or a
    /// directory; every count is narrowed to the files at or under it. Matching is by the
    /// <em>source</em> side — the file a reference, symbol, edge or dependency was written in
    /// — never by what a reference resolves to, because a reference can resolve to a
    /// definition anywhere and "how well does this subtree resolve" is the only question a
    /// path answers.
    /// <para>
    /// The predicate is the same in every query: a file id in the set of files whose
    /// <c>rel_path</c> equals the scope or begins with it plus a slash. The two bound values
    /// carry the exact name and the <c>prefix/%</c> pattern, so a directory named
    /// <c>src/App</c> never sweeps in a sibling <c>src/AppHost</c>.
    /// </para>
    /// </summary>
    private sealed class FileScope
    {
        private readonly string? _prefix;

        private FileScope(string? prefix) => _prefix = prefix;

        public bool IsWholeWorkspace => _prefix is null;

        /// <summary>The normalised scope path, or null for the whole workspace.</summary>
        public string? Path => _prefix;

        public static FileScope For(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new FileScope(null);
            }

            var normalised = raw.Replace('\\', '/').Trim().TrimEnd('/');

            // A leading "./" is noise; "." on its own is the workspace root — the whole
            // workspace, not a literal path that no rel_path (which never starts with "./")
            // could ever match. `codeanalyzer stats .` has meant "everything" every round.
            if (normalised.StartsWith("./", StringComparison.Ordinal))
            {
                normalised = normalised[2..];
            }

            return new FileScope(normalised is "" or "." ? null : normalised);
        }

        /// <summary>A leading <c>WHERE</c> restriction on a file-id column, or empty when unscoped.</summary>
        public string WhereFileIn(string column) =>
            _prefix is null ? string.Empty : $" WHERE {FileIdIn(column)}";

        /// <summary>A trailing <c>AND</c> restriction on a file-id column, or empty when unscoped.</summary>
        public string AndFileIn(string column) =>
            _prefix is null ? string.Empty : $" AND {FileIdIn(column)}";

        public void Bind(SqliteCommand command)
        {
            if (_prefix is null)
            {
                return;
            }

            command.Parameters.AddWithValue("$scope", _prefix);
            command.Parameters.AddWithValue("$scopePrefix", _prefix + "/%");
        }

        private static string FileIdIn(string column) =>
            $"{column} IN (SELECT id FROM file WHERE rel_path = $scope OR rel_path LIKE $scopePrefix)";
    }
}
