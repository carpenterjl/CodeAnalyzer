using CodeAnalyzer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// Workspace-shaped views: where the code is, and which parts of it lean on which.
/// <para>
/// Both queries here answer questions about the whole workspace, which is exactly the kind
/// of query that gets slow first. The treemap therefore reads one level at a time — never
/// the whole tree — and the wheel aggregates to top-level directories, of which there are
/// a handful even in a very large checkout.
/// </para>
/// </summary>
public sealed class StructureQueryService(SqliteConnection connection)
{
    /// <summary>Tiles returned per level. Beyond this a treemap is unreadable anyway.</summary>
    public int MaxTiles { get; init; } = 400;

    /// <summary>Arcs on the wheel. The rest are counted and reported, not silently dropped.</summary>
    public int MaxWheelGroups { get; init; } = 24;

    // ---- Treemap -----------------------------------------------------------

    /// <summary>
    /// Reads the children of one path: sub-directories and files, or — once the path names
    /// a single file — the top-level symbols inside it.
    /// </summary>
    public TreemapLevel GetTreemapLevel(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalised = (path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

        var fileId = TryGetFileId(normalised);
        return fileId is not null
            ? ReadSymbolTiles(normalised, fileId.Value, cancellationToken)
            : ReadDirectoryTiles(normalised, cancellationToken);
    }

    private long? TryGetFileId(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM file WHERE rel_path = $path";
        command.Parameters.AddWithValue("$path", relativePath);
        return command.ExecuteScalar() is long id ? id : null;
    }

    private TreemapLevel ReadDirectoryTiles(string prefix, CancellationToken cancellationToken)
    {
        // Bucket every file under the prefix by its next path segment. Held in a temp table
        // rather than a CTE because the link query below joins it twice, and a CTE would be
        // recomputed each time.
        Execute("DROP TABLE IF EXISTS temp.tm_bucket");
        Execute("""
            CREATE TEMP TABLE tm_bucket (
                file_id INTEGER PRIMARY KEY,
                name    TEXT NOT NULL,
                is_dir  INTEGER NOT NULL
            ) WITHOUT ROWID
            """);

        try
        {
            using (var fill = connection.CreateCommand())
            {
                // A prefix scan expressed as a range, not a LIKE: LIKE is case-insensitive
                // by default and would give up the index on rel_path. '0' is the byte just
                // after '/', so the range covers exactly "prefix/..." and nothing else.
                fill.CommandText = """
                    INSERT INTO tm_bucket (file_id, name, is_dir)
                    SELECT f.id,
                           CASE WHEN instr(substr(f.rel_path, $skip), '/') > 0
                                THEN substr(f.rel_path, $skip, instr(substr(f.rel_path, $skip), '/') - 1)
                                ELSE substr(f.rel_path, $skip) END,
                           CASE WHEN instr(substr(f.rel_path, $skip), '/') > 0 THEN 1 ELSE 0 END
                    FROM file f
                    WHERE $skip = 1 OR (f.rel_path >= $low AND f.rel_path < $high)
                    """;
                fill.Parameters.AddWithValue("$skip", prefix.Length == 0 ? 1 : prefix.Length + 2);
                fill.Parameters.AddWithValue("$low", prefix + "/");
                fill.Parameters.AddWithValue("$high", prefix + "0");
                fill.ExecuteNonQuery();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var links = ReadDirectoryLinkCounts(cancellationToken);
            var tiles = new List<TreemapTile>();
            var truncated = false;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT b.name, b.is_dir, COUNT(*),
                           SUM((SELECT COUNT(*) FROM symbol s WHERE s.file_id = b.file_id)),
                           SUM(f.size)
                    FROM tm_bucket b
                    JOIN file f ON f.id = b.file_id
                    GROUP BY b.name, b.is_dir
                    ORDER BY 4 DESC, 3 DESC, 1
                    LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$limit", MaxTiles + 1);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (tiles.Count == MaxTiles)
                    {
                        truncated = true;
                        break;
                    }

                    var name = reader.GetString(0);
                    var isDirectory = reader.GetInt32(1) == 1;
                    var symbols = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    var counts = links.GetValueOrDefault(name);

                    tiles.Add(new TreemapTile(
                        name,
                        prefix.Length == 0 ? name : prefix + "/" + name,
                        isDirectory ? TreemapTileType.Directory : TreemapTileType.File,
                        symbols,
                        reader.GetInt32(2),
                        symbols,
                        reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        counts.Internal,
                        counts.Outgoing));
                }
            }

            return new TreemapLevel
            {
                Path = prefix,
                ChildType = TreemapTileType.Directory,
                Tiles = tiles,
                Truncated = truncated,
            };
        }
        finally
        {
            Execute("DROP TABLE IF EXISTS temp.tm_bucket");
        }
    }

    /// <summary>
    /// Per-bucket reference counts, split by whether the reference stayed inside the bucket.
    /// Driven from the bucket table so a deep level costs what that level contains rather
    /// than what the workspace contains.
    /// <para>
    /// Reads the file ids and the member-display rule denormalised onto <c>edge</c> at
    /// resolve time (schema v8) instead of seeking through <c>ref</c> and <c>symbol</c> per
    /// row — this query touches every edge under the level, and those two seeks per edge
    /// were most of the treemap's cost at 20k files.
    /// </para>
    /// </summary>
    private Dictionary<string, (int Internal, int Outgoing)> ReadDirectoryLinkCounts(
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT bs.name,
                   SUM(CASE WHEN bt.file_id IS NOT NULL AND bt.name = bs.name THEN 1 ELSE 0 END),
                   SUM(CASE WHEN bt.file_id IS NULL OR bt.name <> bs.name THEN 1 ELSE 0 END)
            FROM tm_bucket bs
            JOIN edge e ON e.src_file_id = bs.file_id
            LEFT JOIN tm_bucket bt ON bt.file_id = e.dst_file_id
            WHERE e.to_own_member = 0
            GROUP BY bs.name
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        }

        return results;
    }

    /// <summary>
    /// The innermost level: one tile per top-level symbol in a file, sized by how many
    /// lines it occupies. Link counts roll up through the containment chain, so a class
    /// tile accounts for what its methods reach.
    /// <para>
    /// "Top-level" descends through namespaces: a block-scoped C# <c>namespace X { … }</c>
    /// contains every class in the file, and a level showing one namespace tile says
    /// nothing — the classes are what the drill is for. A file-scoped namespace already
    /// leaves them top-level, so the two namespace styles must not render differently.
    /// </para>
    /// </summary>
    private TreemapLevel ReadSymbolTiles(string relativePath, long fileId, CancellationToken cancellationToken)
    {
        var rootIds = ReadRootSymbolIds(fileId);
        if (rootIds.Count == 0)
        {
            return new TreemapLevel
            {
                Path = relativePath,
                ChildType = TreemapTileType.Symbol,
                Tiles = [],
            };
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The roots drive both queries below: the rollup and the tiles must agree on
        // what a root is, or a tile would be sized for links it does not display.
        var rootIdList = string.Join(",", rootIds);

        var links = new Dictionary<long, (int Internal, int Outgoing)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                WITH RECURSIVE owner(id, root) AS (
                    SELECT id, id FROM symbol WHERE id IN ({rootIdList})
                    UNION ALL
                    SELECT s.id, o.root
                    FROM symbol s JOIN owner o ON s.container_id = o.id
                    WHERE s.file_id = $fileId
                )
                SELECT o.root,
                       SUM(CASE WHEN e.dst_file_id = $fileId THEN 1 ELSE 0 END),
                       SUM(CASE WHEN e.dst_file_id <> $fileId THEN 1 ELSE 0 END)
                FROM owner o
                JOIN ref r ON r.from_symbol_id = o.id
                JOIN edge e ON e.ref_id = r.id
                WHERE e.to_own_member = 0
                GROUP BY o.root
                """;
            command.Parameters.AddWithValue("$fileId", fileId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                links[reader.GetInt64(0)] = (reader.GetInt32(1), reader.GetInt32(2));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tiles = new List<TreemapTile>();
        var truncated = false;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT s.id, s.name, s.kind, s.start_line, s.end_line,
                       (SELECT COUNT(*) FROM symbol c WHERE c.container_id = s.id)
                FROM symbol s
                WHERE s.id IN ({rootIdList})
                ORDER BY s.start_offset
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", MaxTiles + 1);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (tiles.Count == MaxTiles)
                {
                    truncated = true;
                    break;
                }

                var id = reader.GetInt64(0);
                var startLine = reader.GetInt32(3);
                var endLine = reader.GetInt32(4);
                var counts = links.GetValueOrDefault(id);

                tiles.Add(new TreemapTile(
                    reader.GetString(1),
                    relativePath,
                    TreemapTileType.Symbol,
                    Math.Max(1, endLine - startLine + 1),
                    Files: 0,
                    Symbols: 1 + reader.GetInt32(5),
                    Bytes: 0,
                    counts.Internal,
                    counts.Outgoing,
                    SymbolId: id,
                    Kind: (SymbolKind)reader.GetInt32(2)));
            }
        }

        return new TreemapLevel
        {
            Path = relativePath,
            ChildType = TreemapTileType.Symbol,
            Tiles = tiles,
            Truncated = truncated,
        };
    }

    /// <summary>
    /// The symbols a file's treemap level shows: everything whose container chain holds
    /// only namespaces, namespaces themselves excluded. Falls back to the raw top level
    /// when that leaves nothing — a file declaring only an empty namespace should still
    /// show the namespace tile rather than a blank level.
    /// </summary>
    private List<long> ReadRootSymbolIds(long fileId)
    {
        var roots = new List<long>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE ns(id) AS (
                    SELECT id FROM symbol
                    WHERE file_id = $fileId AND container_id IS NULL AND kind = $namespace
                    UNION ALL
                    SELECT s.id FROM symbol s JOIN ns ON s.container_id = ns.id
                    WHERE s.file_id = $fileId AND s.kind = $namespace
                )
                SELECT s.id
                FROM symbol s
                WHERE s.file_id = $fileId
                  AND s.kind <> $namespace
                  AND (s.container_id IS NULL OR s.container_id IN (SELECT id FROM ns))
                """;
            command.Parameters.AddWithValue("$fileId", fileId);
            command.Parameters.AddWithValue("$namespace", (int)SymbolKind.Namespace);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                roots.Add(reader.GetInt64(0));
            }
        }

        if (roots.Count > 0)
        {
            return roots;
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id FROM symbol WHERE file_id = $fileId AND container_id IS NULL
                """;
            command.Parameters.AddWithValue("$fileId", fileId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                roots.Add(reader.GetInt64(0));
            }
        }

        return roots;
    }

    // ---- Dependency wheel --------------------------------------------------

    /// <summary>
    /// Aggregates dependencies between top-level directories.
    /// <para>
    /// Two sources are offered because neither alone covers every language. Includes and
    /// imports are the most literal answer, but a C# <c>using</c> names a namespace rather
    /// than a file and never resolves to one, so a C# workspace would draw an empty wheel.
    /// Symbol references cover it, at the cost of counting a use of a name rather than a
    /// declared dependency. Which one is on screen is stated in the view.
    /// </para>
    /// </summary>
    public DependencyWheel GetDependencyWheel(
        WheelSource source = WheelSource.Includes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pairs = source == WheelSource.Includes
            ? ReadIncludePairs()
            : ReadReferencePairs();

        var unresolved = source == WheelSource.Includes
            ? ReadUnresolvedIncludes()
            : new Dictionary<string, int>(StringComparer.Ordinal);
        var files = ReadFileCountsByTopDirectory();

        // Rank groups by how much dependency traffic they carry, then keep the busiest.
        var volume = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (from, to, count) in pairs)
        {
            volume[from] = volume.GetValueOrDefault(from) + count;
            volume[to] = volume.GetValueOrDefault(to) + count;
        }

        foreach (var (group, count) in unresolved)
        {
            volume[group] = volume.GetValueOrDefault(group) + count;
        }

        var ranked = volume
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        var kept = ranked.Take(MaxWheelGroups).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        var groups = new List<WheelGroup>();
        foreach (var name in kept.OrderBy(name => name, StringComparer.Ordinal))
        {
            groups.Add(new WheelGroup(
                name,
                files.GetValueOrDefault(name),
                volume.GetValueOrDefault(name),
                unresolved.GetValueOrDefault(name)));
        }

        var links = pairs
            .Where(pair => kept.Contains(pair.From) && kept.Contains(pair.To))
            .Select(pair => new WheelLink(pair.From, pair.To, pair.Count))
            .ToList();

        return new DependencyWheel
        {
            Source = source,
            Groups = groups,
            Links = links,
            OmittedGroups = Math.Max(0, ranked.Count - kept.Count),
        };
    }

    private List<(string From, string To, int Count)> ReadIncludePairs()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sf.top_dir, tf.top_dir, COUNT(*)
            FROM file_dep d
            JOIN file sf ON sf.id = d.file_id
            JOIN file tf ON tf.id = d.dep_file_id
            WHERE d.dep_file_id IS NOT NULL
            GROUP BY sf.top_dir, tf.top_dir
            """;
        return ReadPairs(command);
    }

    private List<(string From, string To, int Count)> ReadReferencePairs()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sf.top_dir, tf.top_dir, COUNT(*)
            FROM edge e
            JOIN file sf ON sf.id = e.src_file_id
            JOIN file tf ON tf.id = e.dst_file_id
            WHERE e.to_own_member = 0
            GROUP BY sf.top_dir, tf.top_dir
            """;
        return ReadPairs(command);
    }

    private static List<(string From, string To, int Count)> ReadPairs(SqliteCommand command)
    {
        var results = new List<(string, string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return results;
    }

    /// <summary>
    /// Dependencies that name a file we never found. An empty probe is excluded: it means
    /// the dependency could not name a file in the first place, so calling it unresolved
    /// would be an accusation rather than a fact.
    /// </summary>
    private Dictionary<string, int> ReadUnresolvedIncludes()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sf.top_dir, COUNT(*)
            FROM file_dep d
            JOIN file sf ON sf.id = d.file_id
            WHERE d.dep_file_id IS NULL AND d.dep_probe <> ''
            GROUP BY sf.top_dir
            """;

        return ReadCounts(command);
    }

    private Dictionary<string, int> ReadFileCountsByTopDirectory()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT top_dir, COUNT(*) FROM file GROUP BY top_dir";
        return ReadCounts(command);
    }

    private static Dictionary<string, int> ReadCounts(SqliteCommand command)
    {
        var results = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results[reader.GetString(0)] = reader.GetInt32(1);
        }

        return results;
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
