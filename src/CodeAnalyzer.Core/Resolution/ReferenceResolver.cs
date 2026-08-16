using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Resolution;

/// <summary>
/// Turns name references into graph edges.
/// <para>
/// Resolution is purely syntactic: a reference is matched to definitions that share its
/// name, narrowed by scope tiers. Where several definitions remain, every candidate is
/// recorded and flagged ambiguous rather than one being guessed at. References that match
/// nothing get no edge at all and surface in the UI as unresolved.
/// </para>
/// <para>
/// The candidate set is built once into an indexed temp table and each reference then
/// keeps only its best tier. An earlier version ran one query per tier with the candidate
/// set expressed as a CTE; SQLite re-evaluated that CTE for every row of the join, which
/// turned a 20k-file workspace into a multi-minute stall.
/// </para>
/// </summary>
public sealed class ReferenceResolver(SqliteConnection connection)
{
    /// <summary>
    /// References with more candidates than this are left unresolved. A name like <c>i</c>
    /// or <c>value</c> can match thousands of definitions; recording them all would swamp
    /// the edge table and tell the user nothing.
    /// </summary>
    public int MaxCandidatesPerReference { get; init; } = 24;

    /// <summary>
    /// Optional per-step timing hook, used by the benchmark tool to locate slow stages.
    /// </summary>
    public Action<string, TimeSpan>? StepTimingCallback { get; init; }

    /// <summary>
    /// How far a file's include and import graph is followed when deciding that a
    /// definition was visible to a reference. Two hops covers the usual shape — a source
    /// file includes a header, which includes the header that actually declares the thing.
    /// </summary>
    public int IncludeReachDepth { get; init; } = 2;

    /// <summary>Scope tiers, lowest wins. Values are used directly in SQL.</summary>
    private const int TierSameFile = 0;
    private const int TierIncludeReachable = 1;
    private const int TierSameLanguageSameTopDirectory = 2;
    private const int TierSameLanguageWorkspace = 3;
    private const int TierAnyLanguage = 4;

    /// <summary>Rebuilds every edge in the database. Returns the number of edges created.</summary>
    public int ResolveAll(CancellationToken cancellationToken = default)
    {
        using var transaction = connection.BeginTransaction();

        Execute(transaction, "DELETE FROM edge");
        DropTempTables(transaction);

        // Before symbols, because which files a file can see is what separates a definition
        // it actually included from one that merely shares the name.
        Timed("file deps", () => ResolveFileDependencies(transaction, fullRebuild: true));

        var created = ResolveSameFile(transaction, RefScope.All, cancellationToken);
        created += ResolveAcrossFiles(transaction, RefScope.All, cancellationToken);

        DropTempTables(transaction);

        Timed("commit", transaction.Commit);
        return created;
    }

    /// <summary>
    /// Rebuilds only the edges a set of changed files could have moved. Returns the number of
    /// edges created.
    /// <para>
    /// A reference's resolution can change for exactly two reasons: the reference itself was
    /// re-parsed, or the set of definitions carrying its name moved. So the work set is every
    /// reference in a changed file, plus every reference — anywhere — whose name was defined
    /// by one of those files before or after the change. Passing the names defined
    /// <em>before</em> is what catches a caller in an untouched file whose callee just
    /// disappeared.
    /// </para>
    /// <para>
    /// This is exact only while the include graph holds still. Reach is transitive, so a file
    /// gaining an <c>#include</c> can promote a reference two hops away whose name never
    /// moved. The caller detects that by comparing dependency edges across the change and
    /// falls back to <see cref="ResolveAll"/>; <c>IncrementalResolutionTests</c> pins the two
    /// against each other.
    /// </para>
    /// </summary>
    public int ResolveIncremental(
        IReadOnlyCollection<long> dirtyFileIds,
        IReadOnlyCollection<string> dirtyNames,
        CancellationToken cancellationToken = default)
    {
        if (dirtyFileIds.Count == 0 && dirtyNames.Count == 0)
        {
            return 0;
        }

        using var transaction = connection.BeginTransaction();

        DropTempTables(transaction);
        CreateDirtySets(transaction, dirtyFileIds, dirtyNames);

        Timed("file deps", () => ResolveFileDependencies(transaction, fullRebuild: false));
        Timed("prune names", () => PruneHotDirtyNames(transaction));

        // The second half is the expensive one, and it needs the exclusion to stay honest
        // about its own size. A reference that already resolves inside its own file never
        // reaches the cross-file pass at all — ResolveSameFile settles it from that file's
        // symbols alone — so a definition moving somewhere else cannot touch it. Without the
        // exclusion, a common name like `init` or `helper` makes one saved file re-resolve
        // most of the workspace: on the 20k-file benchmark it was 40,005 edges for a
        // twelve-name file, which is the full resolve with extra steps.
        //
        // The count bound matters. Same-file resolution is skipped when a file holds more
        // candidates than the cap, and those references do fall through to the cross-file
        // pass, so "has a local definition" is not on its own enough to exclude one.
        // Two inserts rather than one UNION SELECT: each half wants a different driving
        // table, and one statement for both left the planner scanning every reference in the
        // workspace — 242 ms to produce a handful of rows.
        //
        // CROSS JOIN throughout, and everywhere else the dirty sets are read. A temp table
        // has no statistics, so SQLite guesses it is large and happily makes `ref` the outer
        // loop; CROSS JOIN is the documented way to pin the order, and here the small side is
        // known by construction. Without it this stage does not scale with the size of the
        // change, it scales with the size of the workspace, which is the whole point of an
        // incremental pass.
        Execute(transaction, "CREATE TEMP TABLE work_ref (ref_id INTEGER PRIMARY KEY)");

        Timed("work_ref/file", () => Execute(transaction, """
            INSERT OR IGNORE INTO work_ref (ref_id)
            SELECT r.id FROM dirty_file d CROSS JOIN ref r ON r.file_id = d.id
            """));

        Timed("work_ref/name", () => Execute(transaction, $"""
            INSERT OR IGNORE INTO work_ref (ref_id)
            SELECT r.id
            FROM dirty_name n
            CROSS JOIN ref r ON r.name = n.name
            WHERE (
                SELECT COUNT(*)
                FROM symbol s
                WHERE s.file_id = r.file_id AND s.name = r.name AND s.is_definition = 1
                  AND ({KindCompatibilitySql})
            ) NOT BETWEEN 1 AND {MaxCandidatesPerReference}
            """));

        // Names of the work set only, so the hot-name scan does not have to read every
        // definition in the workspace to answer a question about a handful of references.
        Execute(transaction, """
            CREATE TEMP TABLE work_name AS
            SELECT DISTINCT r.name FROM work_ref w CROSS JOIN ref r ON r.id = w.ref_id
            """);

        Execute(transaction, "CREATE INDEX temp.ix_work_name ON work_name(name)");

        // edge's primary key leads with ref_id, so this seeks rather than scans. Edges whose
        // target lived in a re-parsed file are already gone, cascaded from the symbol rows.
        Timed("clear edges", () =>
            Execute(transaction, "DELETE FROM edge WHERE ref_id IN (SELECT ref_id FROM work_ref)"));

        var created = ResolveSameFile(transaction, RefScope.Dirty, cancellationToken);
        created += ResolveAcrossFiles(transaction, RefScope.Dirty, cancellationToken);

        DropTempTables(transaction);

        Timed("commit", transaction.Commit);
        return created;
    }

    /// <summary>
    /// Drops names from the dirty set that were too common to resolve across files anyway.
    /// <para>
    /// A name with more definitions than <see cref="MaxCandidatesPerReference"/> is excluded
    /// from the cross-file pass entirely, so a reference carrying it can only ever be settled
    /// from its own file — and a change somewhere else cannot reach it. Counting only the
    /// definitions <em>outside</em> the changed files is what makes that safe to conclude
    /// about the state before the change as well as after: the count can only have moved by
    /// the definitions the changed files themselves contribute, and those are excluded here,
    /// so a name over the cap without them was over the cap both times.
    /// </para>
    /// <para>
    /// This is one indexed count per dirty name. Leaving the names in instead costs a lookup
    /// per <em>reference</em> that carries one, which on the 20k-file benchmark was 358 ms of
    /// a 640 ms update.
    /// </para>
    /// </summary>
    private void PruneHotDirtyNames(SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Counting with a LIMIT rather than counting: the question is only whether the name
        // is over the cap, and the names this is meant to catch are exactly the ones with
        // thousands of definitions to walk.
        command.CommandText = """
            DELETE FROM dirty_name
            WHERE (
                SELECT COUNT(*) FROM (
                    SELECT 1
                    FROM symbol s
                    WHERE s.name = dirty_name.name
                      AND s.is_definition = 1
                      AND s.file_id NOT IN (SELECT id FROM dirty_file)
                    LIMIT $maxCandidates + 1
                )
            ) > $maxCandidates
            """;
        command.Parameters.AddWithValue("$maxCandidates", MaxCandidatesPerReference);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Re-points the given files' unresolved include and import lines, and nothing else.
    /// <para>
    /// Run on its own before deciding how to resolve references, because re-parsing a file
    /// clears its dependency rows: without this the "did the include graph move" check would
    /// compare real targets against a set of nulls and always answer yes.
    /// </para>
    /// </summary>
    public void ResolveDependenciesFor(IReadOnlyCollection<long> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();

        DropTempTables(transaction);
        CreateDirtySets(transaction, fileIds, []);

        ResolveFileDependencies(transaction, fullRebuild: false);

        DropTempTables(transaction);
        transaction.Commit();
    }

    private static void CreateDirtySets(
        SqliteTransaction transaction,
        IReadOnlyCollection<long> dirtyFileIds,
        IReadOnlyCollection<string> dirtyNames)
    {
        Execute(transaction, "CREATE TEMP TABLE dirty_file (id INTEGER PRIMARY KEY)");
        Execute(transaction, "CREATE TEMP TABLE dirty_name (name TEXT PRIMARY KEY)");

        if (dirtyFileIds.Count > 0)
        {
            using var insertFile = transaction.Connection!.CreateCommand();
            insertFile.Transaction = transaction;
            insertFile.CommandText = "INSERT OR IGNORE INTO dirty_file (id) VALUES ($id)";
            var fileId = insertFile.CreateParameter();
            fileId.ParameterName = "$id";
            insertFile.Parameters.Add(fileId);

            foreach (var id in dirtyFileIds)
            {
                fileId.Value = id;
                insertFile.ExecuteNonQuery();
            }
        }

        if (dirtyNames.Count == 0)
        {
            return;
        }

        using var insertName = transaction.Connection!.CreateCommand();
        insertName.Transaction = transaction;
        insertName.CommandText = "INSERT OR IGNORE INTO dirty_name (name) VALUES ($name)";
        var name = insertName.CreateParameter();
        name.ParameterName = "$name";
        insertName.Parameters.Add(name);

        foreach (var value in dirtyNames)
        {
            name.Value = value;
            insertName.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Which references a pass operates on, expressed as the table its <c>r</c> alias reads.
    /// <para>
    /// The dirty form drives from the small work set and seeks <c>ref</c> by rowid, rather
    /// than scanning every reference in the workspace to test membership.
    /// </para>
    /// </summary>
    private sealed record RefScope(string Source, string HotNamePredicate)
    {
        public static readonly RefScope All = new("ref r", string.Empty);

        public static readonly RefScope Dirty = new(
            "(SELECT r2.* FROM work_ref w CROSS JOIN ref r2 ON r2.id = w.ref_id) r",
            " AND name IN (SELECT name FROM work_name)");
    }

    /// <summary>
    /// Same-file matches, resolved first because they are both the strongest signal and
    /// the cheapest to compute: seeking by (file, name) is selective even for a member
    /// name that appears in every file of the workspace.
    /// </summary>
    private int ResolveSameFile(SqliteTransaction transaction, RefScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // to_own_member uses IS, not =, deliberately: it must compute exactly what the
        // graph queries' "target.container_id IS NOT from_symbol_id" display rule reads,
        // NULL semantics included.
        //
        // References with a foreign receiver are excluded outright. This tier's whole
        // justification — a call in this file probably means the definition in this file —
        // holds for a bare foo() and for this.foo(), and does not hold for obj.foo(),
        // whose target lives wherever obj's type does. Before the receiver was stored,
        // one local candidate here meant Unique, and the graph carried false edges marked
        // exact. Those references fall through to the cross-file pass, where every
        // definition of the name competes — the local one included.
        Timed("local_candidate", () => Execute(transaction, $"""
            CREATE TEMP TABLE local_candidate AS
            SELECT r.id AS ref_id, s.id AS symbol_id, r.file_id AS file_id,
                   (s.container_id IS r.from_symbol_id) AS to_own_member,
                   {ArityMatchSql("r")} AS arity_match
            FROM {scope.Source}
            JOIN symbol s ON s.file_id = r.file_id AND s.name = r.name AND s.is_definition = 1
            WHERE r.kind NOT IN ({(int)ReferenceKind.Include}, {(int)ReferenceKind.Import})
              AND NOT {ForeignReceiverSql("r")}
              AND ({KindCompatibilitySql})
            """));

        Timed("local_candidate index", () =>
            Execute(transaction, "CREATE INDEX temp.ix_local_candidate ON local_candidate(ref_id)"));

        Timed("local_best_arity", () => Execute(transaction, """
            CREATE TEMP TABLE local_best_arity AS
            SELECT ref_id, MAX(arity_match) AS arity_match FROM local_candidate GROUP BY ref_id
            """));

        Execute(transaction, "CREATE INDEX temp.ix_local_best_arity ON local_best_arity(ref_id)");

        Timed("local_count", () => Execute(transaction, """
            CREATE TEMP TABLE local_count AS
            SELECT c.ref_id, COUNT(*) AS candidates
            FROM local_candidate c
            JOIN local_best_arity a ON a.ref_id = c.ref_id AND a.arity_match = c.arity_match
            GROUP BY c.ref_id
            """));

        Execute(transaction, "CREATE INDEX temp.ix_local_count ON local_count(ref_id)");

        return TimedInsert("local edges", transaction, $"""
            INSERT OR IGNORE INTO edge (ref_id, target_symbol_id, confidence, src_file_id, dst_file_id, to_own_member)
            SELECT c.ref_id, c.symbol_id,
                   CASE WHEN k.candidates > 1
                        THEN {(int)EdgeConfidence.Ambiguous}
                        ELSE {(int)EdgeConfidence.Unique} END,
                   c.file_id, c.file_id, c.to_own_member
            FROM local_candidate c
            JOIN local_best_arity a ON a.ref_id = c.ref_id AND a.arity_match = c.arity_match
            JOIN local_count k ON k.ref_id = c.ref_id
            WHERE k.candidates <= $maxCandidates
            """);
    }

    /// <summary>
    /// Cross-file matches for references the same-file pass could not settle.
    /// <para>
    /// Names with more definitions than the candidate cap are excluded <em>before</em> the
    /// join rather than filtered afterwards. A field name shared by every file in a large
    /// workspace would otherwise produce a candidate set quadratic in the file count —
    /// hundreds of millions of rows that the cap then discards anyway.
    /// </para>
    /// </summary>
    private int ResolveAcrossFiles(SqliteTransaction transaction, RefScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Timed("hot_name", () =>
        {
            using var hot = connection.CreateCommand();
            hot.Transaction = transaction;
            hot.CommandText = $"""
                CREATE TEMP TABLE hot_name AS
                SELECT name FROM symbol
                WHERE is_definition = 1{scope.HotNamePredicate}
                GROUP BY name
                HAVING COUNT(*) > $maxCandidates
                """;
            hot.Parameters.AddWithValue("$maxCandidates", MaxCandidatesPerReference);
            hot.ExecuteNonQuery();
        });

        Execute(transaction, "CREATE INDEX temp.ix_hot_name ON hot_name(name)");

        cancellationToken.ThrowIfCancellationRequested();

        // Narrow the reference side first. Joining `ref` straight to `symbol` gave the
        // planner no small table to drive from, so it chose an order that scanned symbols
        // per reference and turned this stage quadratic in workspace size. Materialising
        // only the still-unresolved, non-hot references keeps the driving set small.
        Timed("pending_ref", () => Execute(transaction, $"""
            CREATE TEMP TABLE pending_ref AS
            SELECT r.id, r.file_id, r.name, r.kind, r.from_symbol_id, r.arg_count,
                   r.receiver_text,
                   {ForeignReceiverSql("r")} AS foreign_receiver,
                   rf.language, rf.top_dir
            FROM {scope.Source}
            JOIN file rf ON rf.id = r.file_id
            WHERE r.kind NOT IN ({(int)ReferenceKind.Include}, {(int)ReferenceKind.Import})
              AND NOT EXISTS (SELECT 1 FROM hot_name h WHERE h.name = r.name)
              AND NOT EXISTS (SELECT 1 FROM edge e WHERE e.ref_id = r.id)
            """));

        Execute(transaction, "CREATE INDEX temp.ix_pending_ref ON pending_ref(name)");

        BuildReceiverType(transaction);
        BuildCodeBehind(transaction);
        BuildIncludeReach(transaction);

        Timed("candidate", () => Execute(transaction, $"""
            CREATE TEMP TABLE candidate AS
            SELECT
                p.id AS ref_id,
                s.id AS symbol_id,
                p.file_id AS src_file_id,
                s.file_id AS dst_file_id,
                (s.container_id IS p.from_symbol_id) AS to_own_member,
                {ArityMatchSql("p")} AS arity_match,
                CASE WHEN rt.type_name IS NOT NULL AND sc.name = rt.type_name
                     THEN 1 ELSE 0 END AS receiver_match,
                CASE
                    WHEN ir.file_id IS NOT NULL THEN {TierIncludeReachable}
                    WHEN s.language = p.language AND sf.top_dir = p.top_dir THEN {TierSameLanguageSameTopDirectory}
                    WHEN s.language = p.language THEN {TierSameLanguageWorkspace}
                    ELSE {TierAnyLanguage}
                END AS tier
            FROM pending_ref p
            CROSS JOIN symbol s ON s.name = p.name AND s.is_definition = 1
            JOIN file sf ON sf.id = s.file_id
            LEFT JOIN include_reach ir ON ir.file_id = p.file_id AND ir.reach_id = s.file_id
            LEFT JOIN receiver_type rt ON rt.ref_id = p.id
            LEFT JOIN symbol sc ON sc.id = s.container_id
            WHERE (s.file_id <> p.file_id OR p.foreign_receiver)
              AND ({PendingKindCompatibilitySql})
            """));

        Execute(transaction, "CREATE INDEX temp.ix_candidate ON candidate(ref_id, tier)");

        cancellationToken.ThrowIfCancellationRequested();

        // The receiver's declared type outranks every other signal, and is applied first.
        // Tier and arity are both proximity guesses — this file's directory, this parameter
        // count — while `orchestrator` declared `IndexOrchestrator orchestrator` says which
        // type the member is looked up on, which is the actual language rule. A preference
        // and not a filter, like arity: MAX means a reference whose receiver type matches
        // nothing keeps the whole candidate set it had before.
        Timed("best_receiver", () => Execute(transaction, """
            CREATE TEMP TABLE best_receiver AS
            SELECT ref_id, MAX(receiver_match) AS receiver_match FROM candidate GROUP BY ref_id
            """));

        Execute(transaction, "CREATE INDEX temp.ix_best_receiver ON best_receiver(ref_id)");

        // Best tier per reference, plus how many candidates tie at that tier. The count
        // is what distinguishes a unique match from an ambiguous one.
        Timed("best_tier", () => Execute(transaction, """
            CREATE TEMP TABLE best_tier AS
            SELECT c.ref_id, MIN(c.tier) AS tier
            FROM candidate c
            JOIN best_receiver v ON v.ref_id = c.ref_id AND v.receiver_match = c.receiver_match
            GROUP BY c.ref_id
            """));

        // Arity is applied within the winning tier, never across tiers. Scope outranks
        // signature: a definition the source can actually see is a better answer than a
        // distant one whose parameter count happens to line up.
        Timed("best_arity", () => Execute(transaction, """
            CREATE TEMP TABLE best_arity AS
            SELECT c.ref_id, MAX(c.arity_match) AS arity_match
            FROM candidate c
            JOIN best_receiver v ON v.ref_id = c.ref_id AND v.receiver_match = c.receiver_match
            JOIN best_tier b ON b.ref_id = c.ref_id AND b.tier = c.tier
            GROUP BY c.ref_id
            """));

        Execute(transaction, "CREATE INDEX temp.ix_best_arity ON best_arity(ref_id)");

        Timed("resolved", () => Execute(transaction, """
            CREATE TEMP TABLE resolved AS
            SELECT c.ref_id, c.tier, COUNT(*) AS candidates
            FROM candidate c
            JOIN best_receiver v ON v.ref_id = c.ref_id AND v.receiver_match = c.receiver_match
            JOIN best_tier b ON b.ref_id = c.ref_id AND b.tier = c.tier
            JOIN best_arity a ON a.ref_id = c.ref_id AND a.arity_match = c.arity_match
            GROUP BY c.ref_id, c.tier
            """));

        Execute(transaction, "CREATE INDEX temp.ix_resolved ON resolved(ref_id)");

        cancellationToken.ThrowIfCancellationRequested();

        return TimedInsert("cross-file edges", transaction, $"""
            INSERT OR IGNORE INTO edge (ref_id, target_symbol_id, confidence, src_file_id, dst_file_id, to_own_member)
            SELECT c.ref_id, c.symbol_id,
                   CASE
                       WHEN r.tier = {TierAnyLanguage} THEN {(int)EdgeConfidence.Weak}
                       WHEN r.candidates > 1 THEN {(int)EdgeConfidence.Ambiguous}
                       ELSE {(int)EdgeConfidence.Unique}
                   END,
                   c.src_file_id, c.dst_file_id, c.to_own_member
            FROM candidate c
            JOIN resolved r ON r.ref_id = c.ref_id AND r.tier = c.tier
            JOIN best_receiver v ON v.ref_id = c.ref_id AND v.receiver_match = c.receiver_match
            JOIN best_arity a ON a.ref_id = c.ref_id AND a.arity_match = c.arity_match
            WHERE r.candidates <= $maxCandidates
            """);
    }

    /// <summary>
    /// Which class each markup file compiles into, read from its own <c>x:Class</c>
    /// reference — the short segment the analyzer split out, which is the name a C# class
    /// definition carries.
    /// <para>
    /// This is what makes the handler rule safe. XAML gives no syntax that separates
    /// <c>Click="OnAccept"</c> from <c>TargetType="Button"</c>, so the pack nominates on a
    /// naming convention and would nominate wrongly if left alone; requiring the target to
    /// be a method on <em>this file's</em> code-behind throws out every wrong nomination
    /// silently, because a class has no method called <c>True</c>.
    /// </para>
    /// </summary>
    private void BuildCodeBehind(SqliteTransaction transaction) => Timed("code_behind", () =>
    {
        Execute(transaction, $"""
            CREATE TEMP TABLE code_behind AS
            SELECT r.file_id, r.name AS class_name
            FROM ref r
            WHERE r.kind = {(int)ReferenceKind.TypeUse}
              AND EXISTS (SELECT 1 FROM pending_ref p
                          WHERE p.file_id = r.file_id
                            AND p.kind = {(int)ReferenceKind.Handler})
            GROUP BY r.file_id, r.name
            """);

        Execute(transaction, "CREATE INDEX temp.ix_code_behind ON code_behind(file_id)");
    });

    /// <summary>
    /// The declared type of each pending reference's receiver, where the workspace states
    /// it plainly enough to be worth believing.
    /// <para>
    /// Two facts already in the index, joined for the first time: a reference stores the
    /// receiver it was written against (v13), and a field or property stores its verbatim
    /// declared type. Putting them together turns <c>orchestrator.IndexAsync(…)</c> from a
    /// name with several matches into a lookup on a named type — which is what the language
    /// actually does, and the reason the receiver was recorded at all.
    /// </para>
    /// <para>
    /// Three sources, ranked by how much the language guarantees rather than by how much
    /// each one wins. <c>this</c> is the type the reference sits inside, which needs no
    /// lookup at all. A declaration in the same file contributes the type it was declared
    /// with. And a receiver that <em>is</em> a type name — <c>SymbolKind.Method</c>,
    /// <c>Console.WriteLine</c> — is a static or enum member access, where the type needs no
    /// declaration to be found because the receiver already is one. That last rank is last
    /// on purpose: a local named <c>Console</c> shadows the type <c>Console</c>, and only
    /// consulting the best rank present keeps the shadowing right.
    /// </para>
    /// <para>
    /// Still narrow where it counts, because a wrong type is worse than no type: one
    /// distinct type at the winning rank or nothing, and no generic stripping or namespace
    /// trimming — <c>List&lt;Widget&gt;</c> matches no container name, so it contributes
    /// nothing, which is the correct outcome rather than a guess at <c>Widget</c>. Two types
    /// sharing a name resolve to that shared name and prefer members of either, which is a
    /// preference over a genuine ambiguity and not a claim about which one it is.
    /// </para>
    /// <para>
    /// The declaration rank is still same-file only. Widening it was this round's brief and
    /// the measurement refused: no type in this workspace is declared across two files, so a
    /// partial-class rule would have been machinery with nothing to run on. Inheritance is
    /// not widened either, for a harder reason — no base-type relation is stored anywhere,
    /// so reaching a base class's field is a query-pack change first and a resolver change
    /// second.
    /// </para>
    /// <para>
    /// One inference beyond the verbatim type, because without it this repo's own motivating
    /// case does nothing: <c>var orchestrator = new IndexOrchestrator(…)</c> declares the
    /// type as the word <c>var</c>. Where the declared type is an inferred-type keyword and
    /// the value is a constructor call, the constructed type is read out of it. That is the
    /// whole of the inference — it refuses a value that is not <c>new Something(</c>, and a
    /// generic or namespace-qualified name survives verbatim and simply matches nothing.
    /// The stored <c>type_text</c> is untouched; <c>var</c> is what the source says and the
    /// detail pane should keep saying it.
    /// </para>
    /// </summary>
    private void BuildReceiverType(SqliteTransaction transaction) => Timed("receiver_type", () =>
    {
        // substr(value, 5, instr(value, '(') - 5) is the text between "new " and the first
        // open paren. The LIKE guard is what makes the arithmetic safe: no match means no
        // row, rather than a negative length.
        const string DeclaredType = """
            CASE WHEN d.type_text IN ('var', 'auto') AND d.value LIKE 'new %(%'
                 THEN rtrim(substr(d.value, 5, instr(d.value, '(') - 5))
                 ELSE d.type_text END
            """;

        var typeKinds =
            $"{(int)SymbolKind.Class}, {(int)SymbolKind.Struct}, {(int)SymbolKind.Union}, "
            + $"{(int)SymbolKind.Enum}, {(int)SymbolKind.Typedef}, {(int)SymbolKind.Interface}";

        var variableKinds =
            $"{(int)SymbolKind.Field}, {(int)SymbolKind.Property}, "
            + $"{(int)SymbolKind.Variable}, {(int)SymbolKind.Constant}";

        // Three ways a receiver's type is knowable, in order of how much the language
        // guarantees. Rank is a precedence, not a score: only the best rank present for a
        // reference is consulted, so a shadowing local is never outvoted by the type whose
        // name it borrowed.
        Execute(transaction, $"""
            CREATE TEMP TABLE receiver_candidate AS

            -- 1. `this` is the type the reference is written inside. No lookup, no guess.
            SELECT p.id AS ref_id, 1 AS rank, container.name AS type_name
            FROM pending_ref p
            JOIN symbol source ON source.id = p.from_symbol_id
            JOIN symbol container ON container.id = source.container_id
                                 AND container.kind IN ({typeKinds})
            WHERE p.receiver_text IN ('this', 'self')

            UNION ALL

            -- 2. A declaration of that name in this file, and the type it was declared with.
            SELECT p.id, 2, {DeclaredType}
            FROM pending_ref p
            JOIN symbol d ON d.file_id = p.file_id
                         AND d.name = p.receiver_text
                         AND d.is_definition = 1
                         AND d.kind IN ({variableKinds})
            WHERE p.receiver_text IS NOT NULL
              AND {DeclaredType} <> ''

            UNION ALL

            -- 3. The receiver *is* a type name: static and enum member access, and the one
            -- shape where the type needs no declaration to be found because the receiver
            -- already is one. Ranked last because a local of the same name shadows it.
            SELECT p.id, 3, t.name
            FROM pending_ref p
            JOIN symbol t ON t.name = p.receiver_text
                         AND t.is_definition = 1
                         AND t.kind IN ({typeKinds})
            WHERE p.receiver_text IS NOT NULL
            """);

        Execute(transaction, "CREATE INDEX temp.ix_receiver_candidate ON receiver_candidate(ref_id, rank)");

        // One distinct type at the winning rank, or nothing. Two candidates disagreeing is
        // not a tie to break — it is the index failing to know, and saying so costs nothing
        // because an unestablished receiver type leaves the candidate set exactly as it was.
        Execute(transaction, """
            CREATE TEMP TABLE receiver_type AS
            SELECT c.ref_id, MIN(c.type_name) AS type_name
            FROM receiver_candidate c
            JOIN (SELECT ref_id, MIN(rank) AS rank FROM receiver_candidate GROUP BY ref_id) best
              ON best.ref_id = c.ref_id AND best.rank = c.rank
            GROUP BY c.ref_id
            HAVING COUNT(DISTINCT c.type_name) = 1
            """);

        Execute(transaction, "CREATE INDEX temp.ix_receiver_type ON receiver_type(ref_id)");
    });

    /// <summary>
    /// Which files each file can see through its own includes and imports, followed
    /// <see cref="IncludeReachDepth"/> hops.
    /// <para>
    /// This is the one tier that reflects something the source actually says, rather than
    /// where a file happens to sit, so it ranks above every directory-based guess. Built
    /// iteratively with a fixed hop budget instead of a recursive CTE: a cyclic include
    /// graph is normal in C, and the bounded form cannot be led into one.
    /// </para>
    /// </summary>
    private void BuildIncludeReach(SqliteTransaction transaction) => Timed("include_reach", () =>
    {
        Execute(transaction, """
            CREATE TEMP TABLE include_reach (
                file_id  INTEGER NOT NULL,
                reach_id INTEGER NOT NULL,
                PRIMARY KEY (file_id, reach_id)
            ) WITHOUT ROWID
            """);

        // Seeded only from files that still have an unresolved reference. Everything else
        // would be rows nothing ever looks at, and on a codebase with an umbrella header
        // that every file includes, the difference is millions of them.
        Execute(transaction, """
            INSERT OR IGNORE INTO include_reach (file_id, reach_id)
            SELECT p.file_id, d.dep_file_id
            FROM (SELECT DISTINCT file_id FROM pending_ref) p
            JOIN file_dep d ON d.file_id = p.file_id
            WHERE d.dep_file_id IS NOT NULL AND d.dep_file_id <> p.file_id
            """);

        for (var hop = 1; hop < IncludeReachDepth; hop++)
        {
            Execute(transaction, """
                INSERT OR IGNORE INTO include_reach (file_id, reach_id)
                SELECT r.file_id, d.dep_file_id
                FROM include_reach r
                JOIN file_dep d ON d.file_id = r.reach_id
                WHERE d.dep_file_id IS NOT NULL AND d.dep_file_id <> r.file_id
                """);
        }
    });

    private int TimedInsert(string label, SqliteTransaction transaction, string sql)
    {
        var created = 0;
        Timed(label, () =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$maxCandidates", MaxCandidatesPerReference);
            created = command.ExecuteNonQuery();
        });

        return created;
    }

    private static void DropTempTables(SqliteTransaction transaction)
    {
        foreach (var table in new[]
                 {
                     "local_candidate", "local_best_arity", "local_count", "hot_name",
                     "hot_base", "suffix_match", "pending_ref", "include_reach", "candidate",
                     "receiver_candidate", "receiver_type", "code_behind",
                     "best_receiver", "best_tier", "best_arity",
                     "resolved",
                     "dirty_file", "dirty_name", "work_ref", "work_name",
                 })
        {
            Execute(transaction, $"DROP TABLE IF EXISTS temp.{table}");
        }
    }

    /// <summary>
    /// Whether a candidate definition takes exactly as many parameters as the call site
    /// passes arguments. Both halves are facts read out of the source.
    /// <para>
    /// This is a <em>preference</em>, not a filter: the caller takes <c>MAX</c> of it per
    /// reference and keeps only the winners, so when nothing matches, every candidate
    /// carries 0 and the whole set survives untouched. That is what makes it safe against
    /// the many ways an arity mismatch fails to prove a non-match — C# default arguments
    /// and <c>params</c> arrays, C++ default arguments, varargs, and C's <c>f(void)</c>,
    /// whose one named parameter node is written <c>f()</c> at every call site.
    /// </para>
    /// </summary>
    private static string ArityMatchSql(string referenceAlias) =>
        $"CASE WHEN {referenceAlias}.arg_count IS NOT NULL AND s.param_count = {referenceAlias}.arg_count " +
        "THEN 1 ELSE 0 END";

    /// <summary>
    /// Whether a reference was written against a receiver other than the enclosing
    /// instance — <c>obj.foo()</c>, but not <c>foo()</c>, <c>this.foo()</c> or
    /// <c>self.foo()</c>. A self-receiver is spelled dispatch to the same scope a bare
    /// name reaches, so it keeps the same-file tier's locality claim; any other receiver
    /// defeats it — the target lives wherever the receiver's type does, and the file the
    /// call sits in says nothing about that. NULL receiver_text stays non-foreign
    /// deliberately: packs that capture no receiver assert nothing, and treating their
    /// silence as evidence would demote every edge in those languages.
    /// </summary>
    private static string ForeignReceiverSql(string referenceAlias) =>
        $"({referenceAlias}.receiver_text IS NOT NULL " +
        $"AND {referenceAlias}.receiver_text NOT IN ('this', 'self'))";

    /// <summary>
    /// Restricts which symbol kinds a reference kind may resolve to. Without this a call
    /// could bind to a struct field that happens to share the callee's name.
    /// </summary>
    private static string KindCompatibilitySql { get; } = BuildKindCompatibility("r", crossFile: false);

    /// <summary>The same rules expressed against the pre-filtered pending_ref table.</summary>
    private static string PendingKindCompatibilitySql { get; } = BuildKindCompatibility("p", crossFile: true);

    /// <param name="crossFile">
    /// True for the pass that runs after <c>code_behind</c> exists. A markup event handler
    /// names a method on the code-behind class, which by construction lives in a different
    /// file, so the same-file tier offers it no clause at all and every one of them falls
    /// through to here.
    /// </param>
    private static string BuildKindCompatibility(string referenceAlias, bool crossFile)
    {
        static string In(params SymbolKind[] kinds) =>
            string.Join(", ", kinds.Select(k => (int)k));

        var callable = In(SymbolKind.Function, SymbolKind.Method, SymbolKind.Macro, SymbolKind.Module);
        var types = In(
            SymbolKind.Class, SymbolKind.Struct, SymbolKind.Union, SymbolKind.Enum,
            SymbolKind.Typedef, SymbolKind.Interface, SymbolKind.Module);
        var inheritable = In(SymbolKind.Class, SymbolKind.Interface, SymbolKind.Struct);

        // A bare identifier use only binds to things that can be referenced across scopes:
        // macros, constants, enum members, fields, and file-scope declarations. This is
        // what keeps every loop counter named `i` out of the graph.
        //
        // SymbolKind.Parameter was here and is not any more, and the reason it went is worth
        // keeping. It was never wrong — a parameter would have been admitted to its own
        // function by the container clause below and correctly refused everywhere else — it
        // simply never once applied, because no query pack captures a parameter and none
        // ever has. The capture name is wired end to end (`["parameter"] = Parameter` in
        // CaptureNames, with a specificity rank), so switching it on is a pack change in
        // each language, a schema version, and several thousand new symbols; measured
        // against this workspace it would establish a receiver type for about 848 more
        // references. That is a milestone someone can choose on the evidence. What it is
        // not is a line in a list that has silently claimed to be doing something since it
        // was written. Whoever captures parameters puts the kind back, with a test.
        var referencable = In(
            SymbolKind.Macro, SymbolKind.Constant, SymbolKind.EnumMember,
            SymbolKind.Field, SymbolKind.Function, SymbolKind.Variable, SymbolKind.Port,
            SymbolKind.Property);

        // ...but "across scopes" has to mean something. A symbol declared inside a
        // *function* is a local: unreachable by name from anywhere else, and admitting it
        // is what would make every `i` in the workspace a candidate for every other `i`.
        // A symbol declared inside a *type* is the opposite — a class's constant, an
        // enum's member, an element's named child exist precisely to be named elsewhere,
        // and `Container.Name` is how. Restricting both alike was one rule doing two jobs:
        // it kept the loop counters out and took every type member with them, so
        // `callers SymbolKind.MarkupElement` answered "none" while six call sites read it.
        var scopes = In(
            SymbolKind.Class, SymbolKind.Struct, SymbolKind.Union, SymbolKind.Enum,
            SymbolKind.Interface, SymbolKind.Namespace, SymbolKind.Module,
            SymbolKind.MarkupElement, SymbolKind.ResourceKey);

        // A binding path names a property or field on a type the markup never states; a
        // resource key names a keyed resource. Kept apart deliberately — one rule for
        // both let a binding land on an element name and a resource on a property. No
        // container restriction on either: crossing scopes is what they are for, and the
        // Use rule's restriction would strangle every one.
        //
        // The resource side narrowed again in M20.1's sibling: a key is its own symbol
        // kind, not a markup element, because x:Key and x:Name are different namespaces
        // that happened to share a kind. While they shared one,
        // Style="{StaticResource SearchBox}" written on <TextBox x:Name="SearchBox">
        // resolved to the TextBox — a self-edge, and so invisible in every listing.
        var bindable = In(SymbolKind.Property, SymbolKind.Field);

        var handler = crossFile
            ? $"""
                OR ({referenceAlias}.kind = {(int)ReferenceKind.Handler}
                    AND s.kind = {(int)SymbolKind.Method}
                    AND EXISTS (SELECT 1 FROM code_behind cb
                                JOIN symbol owner ON owner.id = s.container_id
                                WHERE cb.file_id = {referenceAlias}.file_id
                                  AND owner.name = cb.class_name))
              """
            : string.Empty;

        return $"""
            ({referenceAlias}.kind = {(int)ReferenceKind.Call} AND s.kind IN ({callable}))
            OR ({referenceAlias}.kind = {(int)ReferenceKind.TypeUse} AND s.kind IN ({types}))
            OR ({referenceAlias}.kind = {(int)ReferenceKind.Instantiate} AND s.kind = {(int)SymbolKind.Module})
            OR ({referenceAlias}.kind = {(int)ReferenceKind.Inherit} AND s.kind IN ({inheritable}))
            OR ({referenceAlias}.kind = {(int)ReferenceKind.Binding} AND s.kind IN ({bindable}))
            OR ({referenceAlias}.kind = {(int)ReferenceKind.Resource} AND s.kind = {(int)SymbolKind.ResourceKey})
            OR ({referenceAlias}.kind = {(int)ReferenceKind.Use} AND s.kind IN ({referencable})
                AND (s.container_id IS NULL
                     OR s.container_id = {referenceAlias}.from_symbol_id
                     OR EXISTS (SELECT 1 FROM symbol c
                                WHERE c.id = s.container_id AND c.kind IN ({scopes})))){handler}
            """;
    }

    /// <summary>
    /// Points each include/import at the workspace file it names, in three passes that go
    /// from what the language actually promises to what is merely likely.
    /// <para>
    /// The order matters for correctness, not just speed. <c>#include "common.h"</c> means
    /// the <c>common.h</c> next to the including file; in a codebase with one per module,
    /// matching on the filename alone would pick an arbitrary other module's.
    /// </para>
    /// <para>
    /// Dependencies with no probe — a C# namespace, an absolute URL — are left unresolved
    /// rather than matched on their last word.
    /// </para>
    /// </summary>
    /// <param name="fullRebuild">
    /// True to re-point every dependency from scratch. False leaves already-resolved rows
    /// alone and looks only at the dirty files' unresolved ones — sound because the caller
    /// only takes the incremental path when no file was added or removed, so a row that
    /// resolved before still names the same file.
    /// </param>
    private void ResolveFileDependencies(SqliteTransaction transaction, bool fullRebuild)
    {
        var scope = fullRebuild ? string.Empty : " AND file_id IN (SELECT id FROM dirty_file)";
        var aliasScope = fullRebuild ? string.Empty : " AND d.file_id IN (SELECT id FROM dirty_file)";

        if (fullRebuild)
        {
            Execute(transaction, "UPDATE file_dep SET dep_file_id = NULL");
        }

        // 1. Relative to the including file's own directory. This is the rule C states, and
        //    it is two indexed seeks: the file's own row, then a unique rel_path.
        Execute(transaction, $"""
            UPDATE file_dep
            SET dep_file_id = (
                SELECT f.id FROM file f, file sf
                WHERE sf.id = file_dep.file_id
                  AND f.rel_path = CASE
                        WHEN sf.dir_path = '' THEN file_dep.dep_probe
                        ELSE sf.dir_path || '/' || file_dep.dep_probe
                      END
            )
            WHERE dep_file_id IS NULL AND dep_base <> ''{scope}
            """);

        // 2. Relative to the workspace root, which covers an include path rooted at the
        //    top of the tree.
        Execute(transaction, $"""
            UPDATE file_dep
            SET dep_file_id = (SELECT f.id FROM file f WHERE f.rel_path = file_dep.dep_probe)
            WHERE dep_file_id IS NULL AND dep_base <> ''{scope}
            """);

        // 3. Anywhere in the tree, but only when exactly one file matches. Several would
        //    mean we cannot tell which, and picking the shortest path is a guess.
        //    Filenames shared by more than the candidate cap are excluded first: they are
        //    hopeless anyway, and scanning them per dependency is what made an earlier
        //    version of this stage quadratic.
        Execute(transaction, $"""
            CREATE TEMP TABLE hot_base AS
            SELECT base_name FROM file
            GROUP BY base_name HAVING COUNT(*) > {MaxCandidatesPerReference}
            """);

        Execute(transaction, "CREATE INDEX temp.ix_hot_base ON hot_base(base_name)");

        // Driven from the leftovers rather than from `file`, and grouped once, so the count
        // and the answer come out of a single pass over an indexed join.
        Execute(transaction, $"""
            CREATE TEMP TABLE suffix_match AS
            SELECT d.file_id, d.dep_path, MIN(f.id) AS target_id, COUNT(*) AS matches
            FROM file_dep d
            JOIN file f ON f.base_name = d.dep_base
            WHERE d.dep_file_id IS NULL
              AND d.dep_base <> ''
              AND f.rel_path LIKE '%/' || d.dep_probe
              AND NOT EXISTS (SELECT 1 FROM hot_base h WHERE h.base_name = d.dep_base){aliasScope}
            GROUP BY d.file_id, d.dep_path
            """);

        Execute(transaction, "CREATE INDEX temp.ix_suffix_match ON suffix_match(file_id, dep_path)");

        Execute(transaction, $"""
            UPDATE file_dep
            SET dep_file_id = (
                SELECT s.target_id FROM suffix_match s
                WHERE s.file_id = file_dep.file_id
                  AND s.dep_path = file_dep.dep_path
                  AND s.matches = 1
            )
            WHERE dep_file_id IS NULL AND dep_base <> ''{scope}
            """);

        Execute(transaction, "DROP TABLE IF EXISTS temp.hot_base");
        Execute(transaction, "DROP TABLE IF EXISTS temp.suffix_match");

        // `import pkg.sub` means pkg/sub/__init__.py when there is no pkg/sub.py, which is
        // how most Python packages are laid out. Only the leftovers are looked at again.
        Execute(transaction, $"""
            UPDATE file_dep
            SET dep_file_id = (
                SELECT f.id FROM file f
                WHERE f.base_name = '__init__.py'
                  AND (f.rel_path = substr(file_dep.dep_probe, 1, length(file_dep.dep_probe) - 3) || '/__init__.py'
                       OR f.rel_path LIKE '%/' || substr(file_dep.dep_probe, 1, length(file_dep.dep_probe) - 3) || '/__init__.py')
                ORDER BY length(f.rel_path)
                LIMIT 1
            )
            WHERE dep_file_id IS NULL AND dep_probe LIKE '%.py'{scope}
            """);
    }

    private static void Execute(SqliteTransaction transaction, string sql)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Runs a step, reporting its duration when a timing callback is attached.</summary>
    private void Timed(string label, Action action)
    {
        if (StepTimingCallback is null)
        {
            action();
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        StepTimingCallback(label, stopwatch.Elapsed);
    }
}
