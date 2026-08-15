using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// Finds the call sites where data crosses into or out of the workspace: calls to catalog
/// APIs and to functions the user marked. Query-time matching over stored facts — nothing
/// is stamped into the index, so editing a mark or upgrading the catalog needs no re-index.
/// <para>
/// Matching rules, in order:
/// a catalog entry matches a <see cref="ReferenceKind.Call"/> reference by exact name or
/// by name prefix (an index-range seek, never a scan), only in the entry's languages, and
/// only when its co-occurrence requirement — the calling file references one of the
/// entry's type names, or depends on one of its includes/imports — holds. When two
/// catalog entries match one call, the longer (more specific) match wins:
/// <c>HAL_SPI_TransmitReceive</c> is inout, not a second "out" from the
/// <c>HAL_SPI_Transmit</c> prefix. Ties are kept — two satisfied gates on one generic
/// member name are genuinely both possible, and both are stated. A user mark covering a
/// called name suppresses every catalog match for the calls it covers and, unless its
/// direction is <see cref="IoDirection.None"/>, contributes its own sites.
/// </para>
/// <para>
/// Callers must hold the connection's <c>DatabaseGate</c> (via <c>WorkspaceSession.Read</c>);
/// this class, like the other query services, does not lock for itself.
/// </para>
/// </summary>
public sealed class IoBoundaryService(SqliteConnection connection)
{
    /// <summary>
    /// Every boundary site in the workspace, ordered by file and line. Treemap-class work:
    /// run on demand, off the UI thread.
    /// </summary>
    public List<IoBoundarySite> GetAllSites(
        IReadOnlyList<IoCatalogEntry> catalog,
        IReadOnlyList<IoMark> marks,
        CancellationToken cancellationToken = default)
        => Collect(catalog, marks, callerSymbolIds: null, cancellationToken);

    /// <summary>
    /// Boundary sites attributed to the given caller symbols — what the graph asks when it
    /// draws a fragment. Drives from the callers' own references, so the cost scales with
    /// the fragment, not the workspace.
    /// </summary>
    public List<IoBoundarySite> GetSitesForCallers(
        IReadOnlyCollection<long> callerSymbolIds,
        IReadOnlyList<IoCatalogEntry> catalog,
        IReadOnlyList<IoMark> marks,
        CancellationToken cancellationToken = default)
        => Collect(catalog, marks, callerSymbolIds, cancellationToken);

    /// <summary>
    /// Merges sites into one stub per (caller, API name, direction) — the same philosophy
    /// as merged edges: one drawn thing per relationship, the individual sites listed when
    /// it is asked about. Sites with no caller are dropped here: a file-scope call has no
    /// node to hang a stub off (the boundaries view still lists every site).
    /// </summary>
    public static List<IoStub> GroupIntoStubs(IEnumerable<IoBoundarySite> sites)
    {
        var stubs = new Dictionary<(long Caller, string Name, IoDirection Direction), List<IoBoundarySite>>();

        foreach (var site in sites)
        {
            if (site.CallerSymbolId is not { } caller)
            {
                continue;
            }

            var key = (caller, site.Name, site.Direction);
            if (!stubs.TryGetValue(key, out var group))
            {
                stubs[key] = group = [];
            }

            group.Add(site);
        }

        var result = new List<IoStub>(stubs.Count);
        foreach (var ((caller, name, direction), group) in stubs)
        {
            var families = group
                .Select(s => s.Family)
                .Where(f => f is not null)
                .Distinct()
                .ToList();
            var gateNotes = group
                .Select(s => s.GateNote)
                .Where(n => n is not null)
                .Distinct()
                .ToList();

            result.Add(new IoStub
            {
                CallerSymbolId = caller,
                Name = name,
                Direction = direction,
                // Suppression already removed catalog rows for marked names, so a group is
                // never mixed-origin.
                Origin = group[0].Origin,
                Family = families.Count == 0 ? null : string.Join(" / ", families),
                GateNote = gateNotes.Count == 0 ? null : string.Join("; ", gateNotes),
                ArgumentText = group[0].ArgumentText,
                RefIds = group.Select(s => s.RefId).Distinct().ToList(),
            });
        }

        return result;
    }

    /// <summary>
    /// The per-site rows behind a stub, for the detail pane: where each call is and what
    /// it passed. Read fresh by ref id rather than kept from the fragment, because the
    /// stub on screen can outlive several merges.
    /// </summary>
    public List<IoSiteDetail> GetSiteDetails(IReadOnlyCollection<long> refIds)
    {
        if (refIds.Count == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        var names = new List<string>(refIds.Count);
        var i = 0;
        foreach (var refId in refIds)
        {
            var name = $"$r{i++}";
            names.Add(name);
            command.Parameters.AddWithValue(name, refId);
        }

        command.CommandText = $"""
            SELECT r.id, r.name, f.rel_path, f.language, r.line, r.arg_text, r.from_symbol_id, s.name
            FROM ref r
            JOIN file f ON f.id = r.file_id
            LEFT JOIN symbol s ON s.id = r.from_symbol_id
            WHERE r.id IN ({string.Join(", ", names)})
            ORDER BY f.rel_path, r.line
            """;

        var sites = new List<IoSiteDetail>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sites.Add(new IoSiteDetail(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return sites;
    }

    // ---- Packet framing ---------------------------------------------------

    /// <summary>Definition kinds a declared-type name can resolve to for a frame layout.</summary>
    private static readonly string FrameTypeKinds =
        $"{(int)SymbolKind.Class}, {(int)SymbolKind.Struct}, {(int)SymbolKind.Union}, {(int)SymbolKind.Typedef}";

    private static readonly System.Text.RegularExpressions.Regex IdentifierPattern =
        new("[A-Za-z_][A-Za-z0-9_]*", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The framing chain for one boundary call site. Each link is a stored fact — the
    /// argument identifiers are captured references, their resolutions are the same tiers
    /// every edge uses, the declared type is the verbatim slice, and the struct match is a
    /// whole-token name match — and each link states its own uncertainty. A literal
    /// argument (<c>0x42</c>) produces no row here: the verbatim argument text shown with
    /// the site already is the frame.
    /// </summary>
    public List<IoFrameArgument> GetPacketFraming(long refId)
    {
        long fileId;
        long? fromSymbolId;
        int line;
        string? argText;

        using (var site = connection.CreateCommand())
        {
            site.CommandText = "SELECT file_id, from_symbol_id, line, arg_text FROM ref WHERE id = $id";
            site.Parameters.AddWithValue("$id", refId);
            using var reader = site.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(3))
            {
                return [];
            }

            fileId = reader.GetInt64(0);
            fromSymbolId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            line = reader.GetInt32(2);
            argText = reader.GetString(3);
        }

        // The argument identifiers already exist as captured references: same file, same
        // caller, same line. Whole-token containment in the stored argument text is what
        // keeps a reference from elsewhere on the line (the call name, an lvalue) out.
        var candidates = new List<(long RefId, string Name)>();
        using (var uses = connection.CreateCommand())
        {
            uses.CommandText = $"""
                SELECT id, name FROM ref
                WHERE file_id = $file AND line = $line AND kind = {(int)ReferenceKind.Use}
                  AND from_symbol_id IS $from
                ORDER BY col
                """;
            uses.Parameters.AddWithValue("$file", fileId);
            uses.Parameters.AddWithValue("$line", line);
            uses.Parameters.AddWithValue("$from", (object?)fromSymbolId ?? DBNull.Value);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            using var reader = uses.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (ContainsWholeToken(argText, name) && seen.Add(name))
                {
                    candidates.Add((reader.GetInt64(0), name));
                }
            }
        }

        var arguments = new List<IoFrameArgument>(candidates.Count);
        foreach (var (useRefId, name) in candidates)
        {
            arguments.Add(BuildFrameArgument(useRefId, name));
        }

        return arguments;
    }

    private IoFrameArgument BuildFrameArgument(long useRefId, string token)
    {
        // Where the identifier's own resolution landed: the edges the resolver stored.
        var targets = new List<string?>();
        using (var edges = connection.CreateCommand())
        {
            edges.CommandText = """
                SELECT s.type_text FROM edge e
                JOIN symbol s ON s.id = e.target_symbol_id
                WHERE e.ref_id = $ref
                ORDER BY e.confidence, s.id
                """;
            edges.Parameters.AddWithValue("$ref", useRefId);
            using var reader = edges.ExecuteReader();
            while (reader.Read())
            {
                targets.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            }
        }

        if (targets.Count == 0)
        {
            return new IoFrameArgument { Token = token, IsUnresolved = true };
        }

        var declaredType = targets[0];
        var resolutionNote = targets.Count > 1 ? $"one of {targets.Count} name matches" : null;

        if (declaredType is null)
        {
            return new IoFrameArgument
            {
                Token = token,
                ResolutionNote = resolutionNote,
            };
        }

        // The declared type is a verbatim slice ("volatile hmi_frame *"), so the struct
        // lookup tries each identifier-shaped token in it against the workspace's type
        // definitions. Decoration words like "unsigned" or "const" simply match nothing.
        string? structName = null;
        string? structNote = null;
        long structId = 0;
        foreach (System.Text.RegularExpressions.Match match in IdentifierPattern.Matches(declaredType))
        {
            using var lookup = connection.CreateCommand();
            // Several rows can share a type name (a typedef and the struct it names, or
            // one header per module). The one that carries members is the one that can
            // state a layout, so it goes first; the ambiguity is still noted.
            lookup.CommandText = $"""
                SELECT id FROM symbol
                WHERE name = $name AND is_definition = 1 AND kind IN ({FrameTypeKinds})
                ORDER BY (SELECT COUNT(*) FROM symbol m WHERE m.container_id = symbol.id) DESC, id
                """;
            lookup.Parameters.AddWithValue("$name", match.Value);

            var ids = new List<long>();
            using (var reader = lookup.ExecuteReader())
            {
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }
            }

            if (ids.Count > 0)
            {
                structId = ids[0];
                structName = match.Value;
                structNote = ids.Count > 1 ? $"one of {ids.Count} types with this name" : null;
                break;
            }
        }

        var members = new List<IoFrameMember>();
        if (structId != 0)
        {
            using var memberQuery = connection.CreateCommand();
            memberQuery.CommandText = """
                SELECT name, type_text, value FROM symbol
                WHERE container_id = $container
                ORDER BY start_line, id
                LIMIT 64
                """;
            memberQuery.Parameters.AddWithValue("$container", structId);
            using var reader = memberQuery.ExecuteReader();
            while (reader.Read())
            {
                members.Add(new IoFrameMember(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        return new IoFrameArgument
        {
            Token = token,
            ResolutionNote = resolutionNote,
            DeclaredType = declaredType,
            StructName = structName,
            StructNote = structNote,
            Members = members,
        };
    }

    /// <summary>Whole-token match: <c>rx</c> must not match inside <c>rx_buf</c>.</summary>
    private static bool ContainsWholeToken(string text, string token)
    {
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !IsIdentifierChar(text[index - 1]);
            var endIndex = index + token.Length;
            var afterOk = endIndex >= text.Length || !IsIdentifierChar(text[endIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            index += 1;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Reference kinds that count as "the file references this type" for a co-occurrence
    /// gate: a call (constructor styles that parse as calls), a plain use, or a type use.
    /// </summary>
    private static readonly string TypeRefKinds =
        $"{(int)ReferenceKind.Call}, {(int)ReferenceKind.Use}, {(int)ReferenceKind.TypeUse}";

    /// <summary>
    /// The requirement gate. An ungated entry passes outright; a gated one needs any listed
    /// type name referenced in the calling file, or any listed dependency in the file's
    /// include/import rows — exact, or a dotted-submodule extension for Python
    /// (<c>serial.tools</c> satisfies <c>serial</c>). Dependency paths compare
    /// case-insensitively because Windows headers are included under every capitalisation.
    /// </summary>
    private static readonly string RequirementGateSql = $"""
        (
            (NOT EXISTS (SELECT 1 FROM io_entry_type_req q WHERE q.entry_id = e.id)
             AND NOT EXISTS (SELECT 1 FROM io_entry_dep_req q WHERE q.entry_id = e.id))
            OR EXISTS (
                SELECT 1 FROM io_entry_type_req q
                WHERE q.entry_id = e.id
                  AND EXISTS (SELECT 1 FROM ref t
                              WHERE t.name = q.type_name AND t.file_id = r.file_id
                                AND t.kind IN ({TypeRefKinds})))
            OR EXISTS (
                SELECT 1 FROM io_entry_dep_req q
                WHERE q.entry_id = e.id
                  AND EXISTS (SELECT 1 FROM file_dep d
                              WHERE d.file_id = r.file_id
                                AND (d.dep_path = q.dep COLLATE NOCASE
                                     OR d.dep_path LIKE q.dep || '.%')))
        )
        """;

    private const string LanguageGateSql = """
        EXISTS (SELECT 1 FROM io_entry_language l WHERE l.entry_id = e.id AND l.language = f.language)
        """;

    private const string SiteColumns = """
        r.id, r.from_symbol_id, r.name, e.direction, 0, e.family, e.gate_note,
        r.file_id, r.line, r.arg_text
        """;

    private List<IoBoundarySite> Collect(
        IReadOnlyList<IoCatalogEntry> catalog,
        IReadOnlyList<IoMark> marks,
        IReadOnlyCollection<long>? callerSymbolIds,
        CancellationToken cancellationToken)
    {
        if ((catalog.Count == 0 && marks.Count == 0)
            || callerSymbolIds is { Count: 0 })
        {
            return [];
        }

        try
        {
            CreateTempTables(callerSymbolIds);
            FillEntries(catalog);
            FillMarks(marks);
            if (callerSymbolIds is not null)
            {
                FillCallers(callerSymbolIds);
            }

            cancellationToken.ThrowIfCancellationRequested();
            InsertCatalogSites(callerSymbolIds is not null);

            cancellationToken.ThrowIfCancellationRequested();
            KeepMostSpecificCatalogMatch();
            ApplyMarkOverrides(callerSymbolIds is not null);

            cancellationToken.ThrowIfCancellationRequested();
            return ReadSites();
        }
        finally
        {
            Execute(DropTempTablesSql);
        }
    }

    private const string DropTempTablesSql = """
        DROP TABLE IF EXISTS io_entry;
        DROP TABLE IF EXISTS io_entry_language;
        DROP TABLE IF EXISTS io_entry_type_req;
        DROP TABLE IF EXISTS io_entry_dep_req;
        DROP TABLE IF EXISTS io_mark;
        DROP TABLE IF EXISTS io_caller;
        DROP TABLE IF EXISTS io_site;
        DROP TABLE IF EXISTS io_best;
        """;

    private void CreateTempTables(IReadOnlyCollection<long>? callerSymbolIds)
    {
        Execute(DropTempTablesSql);
        Execute("""
            CREATE TEMP TABLE io_entry (
                id        INTEGER PRIMARY KEY,
                name      TEXT,
                prefix    TEXT,
                prefix_hi TEXT,
                direction INTEGER NOT NULL,
                family    TEXT NOT NULL,
                gate_note TEXT
            );
            CREATE TEMP TABLE io_entry_language (entry_id INTEGER NOT NULL, language TEXT NOT NULL);
            CREATE TEMP TABLE io_entry_type_req (entry_id INTEGER NOT NULL, type_name TEXT NOT NULL);
            CREATE TEMP TABLE io_entry_dep_req  (entry_id INTEGER NOT NULL, dep TEXT NOT NULL);
            CREATE TEMP TABLE io_mark (name TEXT NOT NULL, direction INTEGER NOT NULL, scope TEXT);
            CREATE TEMP TABLE io_site (
                ref_id      INTEGER NOT NULL,
                caller_id   INTEGER,
                name        TEXT NOT NULL,
                direction   INTEGER NOT NULL,
                origin      INTEGER NOT NULL,
                family      TEXT,
                gate_note   TEXT,
                file_id     INTEGER NOT NULL,
                line        INTEGER NOT NULL,
                arg_text    TEXT,
                specificity INTEGER NOT NULL DEFAULT 0
            );
            """);

        if (callerSymbolIds is not null)
        {
            Execute("CREATE TEMP TABLE io_caller (id INTEGER PRIMARY KEY)");
        }
    }

    private void FillEntries(IReadOnlyList<IoCatalogEntry> catalog)
    {
        using var insertEntry = connection.CreateCommand();
        insertEntry.CommandText = """
            INSERT INTO io_entry (id, name, prefix, prefix_hi, direction, family, gate_note)
            VALUES ($id, $name, $prefix, $hi, $direction, $family, $note)
            """;
        var id = insertEntry.Parameters.Add("$id", SqliteType.Integer);
        var name = insertEntry.Parameters.Add("$name", SqliteType.Text);
        var prefix = insertEntry.Parameters.Add("$prefix", SqliteType.Text);
        var hi = insertEntry.Parameters.Add("$hi", SqliteType.Text);
        var direction = insertEntry.Parameters.Add("$direction", SqliteType.Integer);
        var family = insertEntry.Parameters.Add("$family", SqliteType.Text);
        var note = insertEntry.Parameters.Add("$note", SqliteType.Text);

        using var insertLanguage = connection.CreateCommand();
        insertLanguage.CommandText =
            "INSERT INTO io_entry_language (entry_id, language) VALUES ($id, $language)";
        var languageEntry = insertLanguage.Parameters.Add("$id", SqliteType.Integer);
        var language = insertLanguage.Parameters.Add("$language", SqliteType.Text);

        using var insertTypeReq = connection.CreateCommand();
        insertTypeReq.CommandText =
            "INSERT INTO io_entry_type_req (entry_id, type_name) VALUES ($id, $type)";
        var typeEntry = insertTypeReq.Parameters.Add("$id", SqliteType.Integer);
        var typeName = insertTypeReq.Parameters.Add("$type", SqliteType.Text);

        using var insertDepReq = connection.CreateCommand();
        insertDepReq.CommandText =
            "INSERT INTO io_entry_dep_req (entry_id, dep) VALUES ($id, $dep)";
        var depEntry = insertDepReq.Parameters.Add("$id", SqliteType.Integer);
        var dep = insertDepReq.Parameters.Add("$dep", SqliteType.Text);

        for (var i = 0; i < catalog.Count; i++)
        {
            var entry = catalog[i];

            id.Value = i;
            name.Value = (object?)entry.Name ?? DBNull.Value;
            prefix.Value = (object?)entry.Prefix ?? DBNull.Value;
            // The upper bound that turns "starts with prefix" into an index-range seek:
            // U+FFFF is above any character that appears in an identifier.
            hi.Value = entry.Prefix is null ? DBNull.Value : entry.Prefix + "\uFFFF";
            direction.Value = (int)entry.Direction;
            family.Value = entry.Family;
            note.Value = (object?)entry.GateNote ?? DBNull.Value;
            insertEntry.ExecuteNonQuery();

            languageEntry.Value = i;
            foreach (var lang in entry.Languages)
            {
                language.Value = lang;
                insertLanguage.ExecuteNonQuery();
            }

            typeEntry.Value = i;
            foreach (var type in entry.RequiredTypeRefs)
            {
                typeName.Value = type;
                insertTypeReq.ExecuteNonQuery();
            }

            depEntry.Value = i;
            foreach (var dependency in entry.RequiredDependencies)
            {
                dep.Value = dependency;
                insertDepReq.ExecuteNonQuery();
            }
        }
    }

    private void FillMarks(IReadOnlyList<IoMark> marks)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO io_mark (name, direction, scope) VALUES ($name, $direction, $scope)";
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var direction = insert.Parameters.Add("$direction", SqliteType.Integer);
        var scope = insert.Parameters.Add("$scope", SqliteType.Text);

        foreach (var mark in marks)
        {
            name.Value = mark.Name;
            direction.Value = (int)mark.Direction;
            scope.Value = (object?)mark.Scope ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
    }

    private void FillCallers(IReadOnlyCollection<long> callerSymbolIds)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO io_caller (id) VALUES ($id)";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);

        foreach (var callerId in callerSymbolIds)
        {
            id.Value = callerId;
            insert.ExecuteNonQuery();
        }
    }

    private void InsertCatalogSites(bool callerScoped)
    {
        if (callerScoped)
        {
            // A fragment's few callers hold few references, so drive from those and test
            // every entry against each. CROSS JOIN pins the order — temp tables have no
            // statistics, and letting the planner guess put `ref` on the outside once
            // before (M7); that mistake is not coming back.
            Execute($"""
                INSERT INTO io_site (ref_id, caller_id, name, direction, origin, family,
                                     gate_note, file_id, line, arg_text, specificity)
                SELECT {SiteColumns},
                       CASE WHEN e.name IS NOT NULL THEN length(e.name) ELSE length(e.prefix) END
                FROM io_caller c
                CROSS JOIN ref r ON r.from_symbol_id = c.id
                JOIN file f ON f.id = r.file_id
                CROSS JOIN io_entry e
                WHERE r.kind = {(int)ReferenceKind.Call}
                  AND ((e.name IS NOT NULL AND r.name = e.name)
                       OR (e.prefix IS NOT NULL AND r.name >= e.prefix AND r.name < e.prefix_hi))
                  AND {LanguageGateSql}
                  AND {RequirementGateSql}
                """);
            return;
        }

        // Workspace-wide: two passes so both stay index seeks on ref(name) — equality for
        // exact entries, a range for prefixes. One pass with OR would scan every reference.
        Execute($"""
            INSERT INTO io_site (ref_id, caller_id, name, direction, origin, family,
                                 gate_note, file_id, line, arg_text, specificity)
            SELECT {SiteColumns}, length(e.name)
            FROM io_entry e
            CROSS JOIN ref r ON r.name = e.name
            JOIN file f ON f.id = r.file_id
            WHERE e.name IS NOT NULL
              AND r.kind = {(int)ReferenceKind.Call}
              AND {LanguageGateSql}
              AND {RequirementGateSql}
            """);

        Execute($"""
            INSERT INTO io_site (ref_id, caller_id, name, direction, origin, family,
                                 gate_note, file_id, line, arg_text, specificity)
            SELECT {SiteColumns}, length(e.prefix)
            FROM io_entry e
            CROSS JOIN ref r ON r.name >= e.prefix AND r.name < e.prefix_hi
            JOIN file f ON f.id = r.file_id
            WHERE e.prefix IS NOT NULL
              AND r.kind = {(int)ReferenceKind.Call}
              AND {LanguageGateSql}
              AND {RequirementGateSql}
            """);
    }

    /// <summary>
    /// When two catalog entries match one call, the longer match wins:
    /// <c>HAL_SPI_TransmitReceive</c> must not also surface as an "out" site from the
    /// <c>HAL_SPI_Transmit</c> prefix. Equal-length matches are all kept — two satisfied
    /// gates on one generic member name are both possible, and both are stated.
    /// </summary>
    private void KeepMostSpecificCatalogMatch()
    {
        Execute("""
            CREATE TEMP TABLE io_best AS
            SELECT ref_id, MAX(specificity) AS specificity
            FROM io_site WHERE origin = 0 GROUP BY ref_id
            """);
        Execute("""
            DELETE FROM io_site
            WHERE origin = 0
              AND specificity < (SELECT b.specificity FROM io_best b WHERE b.ref_id = io_site.ref_id)
            """);
    }

    private void ApplyMarkOverrides(bool callerScoped)
    {
        // The user's statement about their own code outranks the shipped list: any mark
        // covering a called name removes every catalog site for the calls it covers,
        // whatever the mark's direction says.
        Execute("""
            DELETE FROM io_site
            WHERE origin = 0
              AND EXISTS (
                  SELECT 1 FROM io_mark m
                  WHERE m.name = io_site.name
                    AND (m.scope IS NULL OR EXISTS (
                        SELECT 1 FROM file f
                        WHERE f.id = io_site.file_id
                          AND (f.rel_path = m.scope OR f.rel_path LIKE m.scope || '/%'))))
            """);

        var callerFilter = callerScoped
            ? "AND r.from_symbol_id IN (SELECT id FROM io_caller)"
            : string.Empty;

        // Direction None is a pure suppression: it contributes nothing of its own.
        Execute($"""
            INSERT INTO io_site (ref_id, caller_id, name, direction, origin, family,
                                 gate_note, file_id, line, arg_text, specificity)
            SELECT r.id, r.from_symbol_id, r.name, m.direction, 1, NULL, NULL,
                   r.file_id, r.line, r.arg_text, 0
            FROM io_mark m
            CROSS JOIN ref r ON r.name = m.name
            JOIN file f ON f.id = r.file_id
            WHERE m.direction <> {(int)IoDirection.None}
              AND r.kind = {(int)ReferenceKind.Call}
              AND (m.scope IS NULL OR f.rel_path = m.scope OR f.rel_path LIKE m.scope || '/%')
              {callerFilter}
            """);
    }

    private List<IoBoundarySite> ReadSites()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.ref_id, s.caller_id, s.name, s.direction, s.origin, s.family,
                   s.gate_note, f.rel_path, s.line, s.arg_text, c.name
            FROM io_site s
            JOIN file f ON f.id = s.file_id
            LEFT JOIN symbol c ON c.id = s.caller_id
            ORDER BY f.rel_path, s.line, s.ref_id
            """;

        var sites = new List<IoBoundarySite>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sites.Add(new IoBoundarySite
            {
                RefId = reader.GetInt64(0),
                CallerSymbolId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                CallerName = reader.IsDBNull(10) ? null : reader.GetString(10),
                Name = reader.GetString(2),
                Direction = (IoDirection)reader.GetInt32(3),
                Origin = (IoMatchOrigin)reader.GetInt32(4),
                Family = reader.IsDBNull(5) ? null : reader.GetString(5),
                GateNote = reader.IsDBNull(6) ? null : reader.GetString(6),
                RelativePath = reader.GetString(7),
                Line = reader.GetInt32(8),
                ArgumentText = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        return sites;
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
