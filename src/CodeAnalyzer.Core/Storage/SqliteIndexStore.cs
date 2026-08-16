using System.Data;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// The index database: batched writer, incremental gate, and the connection every
/// reader shares.
/// <para>
/// Writes arrive from the pipeline's single consumer task, so this type needs no
/// internal locking. Rows are committed in transactions of
/// <see cref="BatchSize"/> files, because per-file transactions are what make a
/// large index slow.
/// </para>
/// </summary>
public sealed class SqliteIndexStore : IParseResultSink, IIncrementalGate, IDisposable
{
    /// <summary>Files per transaction. Large enough to amortise commit cost, small enough to bound a rollback.</summary>
    private const int BatchSize = 200;

    private readonly SqliteConnection _connection;
    private readonly List<ParseResult> _pending = new(BatchSize);

    /// <summary>Ids are allocated in-process rather than read back, so inserts never round-trip.</summary>
    private long _nextFileId = 1;
    private long _nextSymbolId = 1;
    private long _nextReferenceId = 1;

    private readonly Dictionary<string, long> _fileIdsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Size, long ModifiedUnixMs)> _fileStamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths seen during this run, used to detect deletions at the end.</summary>
    private readonly HashSet<string> _touchedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Paths actually re-parsed during this run, as opposed to skipped by the incremental
    /// gate. An incremental resolve needs to know precisely which files changed.
    /// </summary>
    private readonly HashSet<string> _writtenPaths = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    private SqliteIndexStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public SqliteConnection Connection => _connection;

    /// <summary>
    /// Serialises access to <see cref="Connection"/>. Everything that touches the database —
    /// including the query services, which read it from another thread — goes through this.
    /// </summary>
    public DatabaseGate Gate { get; } = new();

    /// <summary>Opens (or creates) the index for a workspace.</summary>
    public static SqliteIndexStore Open(string databasePath, string workspaceRoot)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // The store holds one long-lived connection, so pooling buys nothing here —
            // and it costs something real: a pooled connection keeps the file locked
            // after Dispose, so the cache directory cannot be deleted until the pool
            // lets go. Closing must actually close.
            Pooling = false,
        }.ToString());

        connection.Open();
        Schema.EnsureCreated(connection);
        Schema.WriteMeta(connection, Schema.MetaRootPath, workspaceRoot);

        var store = new SqliteIndexStore(connection);
        store.LoadState();
        return store;
    }

    /// <summary>Opens the index for a workspace at its conventional per-user location.</summary>
    public static SqliteIndexStore OpenForWorkspace(string workspaceRoot) =>
        Open(WorkspacePaths.GetDatabasePath(workspaceRoot), workspaceRoot);

    /// <summary>
    /// Loads id watermarks and the file stamps the incremental gate compares against.
    /// One scan at open is far cheaper than querying per file during the crawl.
    /// </summary>
    private void LoadState()
    {
        _nextFileId = ReadMaxId("file") + 1;
        _nextSymbolId = ReadMaxId("symbol") + 1;
        _nextReferenceId = ReadMaxId("ref") + 1;

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, rel_path, size, mtime FROM file";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var path = reader.GetString(1);

            _fileIdsByPath[path] = id;
            _fileStamps[path] = (reader.GetInt64(2), reader.GetInt64(3));
        }
    }

    private long ReadMaxId(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT COALESCE(MAX(id), 0) FROM \"{table}\"";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    // ---- Incremental gate -------------------------------------------------

    public bool IsUnchanged(string relativePath, long size, long modifiedUnixMs)
    {
        // Size and timestamp only: hashing every file would defeat the purpose of
        // skipping unchanged ones. A content hash still guards the write path.
        var unchanged = _fileStamps.TryGetValue(relativePath, out var stamp)
            && stamp.Size == size
            && stamp.ModifiedUnixMs == modifiedUnixMs;

        if (unchanged)
        {
            // Still counts as seen, so it is not treated as a deletion.
            lock (_touchedPaths)
            {
                _touchedPaths.Add(relativePath);
            }
        }

        return unchanged;
    }

    // ---- Writer -----------------------------------------------------------

    public Task WriteAsync(ParseResult result, CancellationToken cancellationToken)
    {
        // The crawl task adds to these sets through the incremental gate at the same time as
        // the writer task adds here, so both sides take the lock.
        lock (_touchedPaths)
        {
            _touchedPaths.Add(result.RelativePath);
            _writtenPaths.Add(result.RelativePath);
        }

        _pending.Add(result);

        if (_pending.Count >= BatchSize)
        {
            Flush(cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        Flush(cancellationToken);
        Gate.Run(() => Schema.WriteMeta(_connection, Schema.MetaLastIndexUtc, DateTimeOffset.UtcNow.ToString("O")));
        return Task.CompletedTask;
    }

    private void Flush(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        Gate.Run(() => FlushCore(cancellationToken));
    }

    private void FlushCore(CancellationToken cancellationToken)
    {
        using var transaction = _connection.BeginTransaction();

        // symbol.container_id can point FORWARD within one file: tree-sitter reports a
        // `typedef struct { … } Name;` only when it reaches the trailing name, so the
        // members precede their container in the symbol list and get smaller row ids.
        // SQLite checks foreign keys per statement by default, which made every such
        // file — STM32 vendor headers, Cython-generated C — fail the batch. Deferring
        // moves the check to COMMIT, where the whole batch is present; a genuinely
        // dangling id still fails, just at the end. The pragma is transaction-scoped
        // and resets itself on commit or rollback.
        using (var defer = _connection.CreateCommand())
        {
            defer.Transaction = transaction;
            defer.CommandText = "PRAGMA defer_foreign_keys = ON";
            defer.ExecuteNonQuery();
        }

        using var deleteSymbols = CreateCommand(transaction, "DELETE FROM symbol WHERE file_id = $fileId");
        using var deleteReferences = CreateCommand(transaction, "DELETE FROM ref WHERE file_id = $fileId");
        using var deleteDependencies = CreateCommand(transaction, "DELETE FROM file_dep WHERE file_id = $fileId");

        using var upsertFile = CreateCommand(transaction, """
            INSERT INTO file (id, rel_path, top_dir, base_name, dir_path, language, content_hash, size, mtime, status, error)
            VALUES ($id, $relPath, $topDir, $baseName, $dirPath, $language, $hash, $size, $mtime, $status, $error)
            ON CONFLICT(rel_path) DO UPDATE SET
                top_dir = excluded.top_dir,
                base_name = excluded.base_name,
                dir_path = excluded.dir_path,
                language = excluded.language,
                content_hash = excluded.content_hash,
                size = excluded.size,
                mtime = excluded.mtime,
                status = excluded.status,
                error = excluded.error
            """);

        using var insertSymbol = CreateCommand(transaction, """
            INSERT INTO symbol
                (id, file_id, name, kind, container_id, scope_path, signature, value,
                 value_num, value_str, type_text,
                 modifiers, language, start_line, start_col, end_line, end_col, start_offset,
                 end_offset, is_definition, param_count, param_text)
            VALUES
                ($id, $fileId, $name, $kind, $containerId, $scopePath, $signature, $value,
                 $valueNum, $valueStr, $typeText,
                 $modifiers, $language, $startLine, $startCol, $endLine, $endCol, $startOffset,
                 $endOffset, $isDefinition, $paramCount, $paramText)
            """);

        using var insertReference = CreateCommand(transaction, """
            INSERT INTO ref (id, file_id, from_symbol_id, name, kind, arg_count, arg_text, line, col)
            VALUES ($id, $fileId, $fromSymbolId, $name, $kind, $argCount, $argText, $line, $col)
            """);

        using var insertDependency = CreateCommand(transaction, """
            INSERT INTO file_dep (file_id, dep_path, dep_probe, dep_base, dep_file_id)
            VALUES ($fileId, $depPath, $depProbe, $depBase, NULL)
            ON CONFLICT(file_id, dep_path) DO NOTHING
            """);

        foreach (var result in _pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteOne(result, upsertFile, deleteSymbols, deleteReferences, deleteDependencies,
                insertSymbol, insertReference, insertDependency);
        }

        transaction.Commit();
        _pending.Clear();
    }

    private void WriteOne(
        ParseResult result,
        SqliteCommand upsertFile,
        SqliteCommand deleteSymbols,
        SqliteCommand deleteReferences,
        SqliteCommand deleteDependencies,
        SqliteCommand insertSymbol,
        SqliteCommand insertReference,
        SqliteCommand insertDependency)
    {
        var fileId = GetOrAllocateFileId(result.RelativePath);

        Set(upsertFile, "$id", fileId);
        Set(upsertFile, "$relPath", result.RelativePath);
        Set(upsertFile, "$topDir", GetTopDirectory(result.RelativePath));
        Set(upsertFile, "$baseName", GetBaseName(result.RelativePath));
        Set(upsertFile, "$dirPath", GetDirectory(result.RelativePath));
        Set(upsertFile, "$language", result.Language);
        Set(upsertFile, "$hash", result.ContentHash);
        Set(upsertFile, "$size", result.Size);
        Set(upsertFile, "$mtime", result.ModifiedUnixMs);
        Set(upsertFile, "$status", (int)result.Status);
        Set(upsertFile, "$error", result.ErrorMessage);
        upsertFile.ExecuteNonQuery();

        // Replace rather than merge: re-parsing a file supersedes everything it declared.
        Set(deleteSymbols, "$fileId", fileId);
        deleteSymbols.ExecuteNonQuery();
        Set(deleteReferences, "$fileId", fileId);
        deleteReferences.ExecuteNonQuery();
        Set(deleteDependencies, "$fileId", fileId);
        deleteDependencies.ExecuteNonQuery();

        // Ids are allocated up front so local indices can be translated without
        // reading anything back.
        var symbolIdBase = _nextSymbolId;
        _nextSymbolId += result.Symbols.Count;

        for (var i = 0; i < result.Symbols.Count; i++)
        {
            var symbol = result.Symbols[i];

            Set(insertSymbol, "$id", symbolIdBase + i);
            Set(insertSymbol, "$fileId", fileId);
            Set(insertSymbol, "$name", symbol.Name);
            Set(insertSymbol, "$kind", (int)symbol.Kind);
            Set(insertSymbol, "$containerId", symbol.ContainerLocalIndex is { } c ? symbolIdBase + c : null);
            Set(insertSymbol, "$scopePath", symbol.ScopePath);
            Set(insertSymbol, "$signature", symbol.Signature);
            Set(insertSymbol, "$value", symbol.Value);

            // The verbatim slice is the fact on display; this is what its own grammar says
            // it denotes, so 0xA5 in C# can find 165 in C. Stamped here rather than in the
            // analyzer because it is a property of the literal's notation, not of the
            // syntax tree — the .scm packs stay untouched.
            var literal = LiteralValueParser.Parse(symbol.Value, symbol.Language);
            Set(insertSymbol, "$valueNum", literal.Number);
            Set(insertSymbol, "$valueStr", literal.Text);

            Set(insertSymbol, "$typeText", symbol.TypeText);
            Set(insertSymbol, "$modifiers", symbol.Modifiers);
            Set(insertSymbol, "$language", symbol.Language);
            Set(insertSymbol, "$startLine", symbol.Span.Start.Line);
            Set(insertSymbol, "$startCol", symbol.Span.Start.Column);
            Set(insertSymbol, "$endLine", symbol.Span.End.Line);
            Set(insertSymbol, "$endCol", symbol.Span.End.Column);
            Set(insertSymbol, "$startOffset", symbol.Span.StartOffset);
            Set(insertSymbol, "$endOffset", symbol.Span.EndOffset);
            Set(insertSymbol, "$isDefinition", symbol.IsDefinition ? 1 : 0);
            Set(insertSymbol, "$paramCount", symbol.ParameterCount);
            Set(insertSymbol, "$paramText", symbol.ParameterText);
            insertSymbol.ExecuteNonQuery();
        }

        foreach (var reference in result.References)
        {
            Set(insertReference, "$id", _nextReferenceId++);
            Set(insertReference, "$fileId", fileId);
            Set(insertReference, "$fromSymbolId", reference.FromSymbolLocalIndex is { } f ? symbolIdBase + f : null);
            Set(insertReference, "$name", reference.Name);
            Set(insertReference, "$kind", (int)reference.Kind);
            Set(insertReference, "$argCount", reference.ArgumentCount);
            Set(insertReference, "$argText", reference.ArgumentText);
            Set(insertReference, "$line", reference.Position.Line);
            Set(insertReference, "$col", reference.Position.Column);
            insertReference.ExecuteNonQuery();
        }

        // Include and import references double as the file dependency graph.
        foreach (var reference in result.References)
        {
            if (reference.Kind is not (ReferenceKind.Include or ReferenceKind.Import))
            {
                continue;
            }

            var probe = DependencyProbe.For(result.Language, result.RelativePath, reference.Name);

            Set(insertDependency, "$fileId", fileId);
            Set(insertDependency, "$depPath", reference.Name);
            Set(insertDependency, "$depProbe", probe);
            Set(insertDependency, "$depBase", DependencyProbe.BaseNameOf(probe));
            insertDependency.ExecuteNonQuery();
        }

        _fileStamps[result.RelativePath] = (result.Size, result.ModifiedUnixMs);
    }

    /// <summary>First path segment, or empty for files sitting at the workspace root.</summary>
    private static string GetTopDirectory(string relativePath)
    {
        var slash = relativePath.IndexOf('/');
        return slash <= 0 ? string.Empty : relativePath[..slash];
    }

    /// <summary>
    /// Final path segment. Dependencies name files with arbitrary leading directories
    /// (<c>"drivers/uart.h"</c>), so matching on the filename is what makes resolution
    /// an indexed lookup rather than a scan.
    /// </summary>
    private static string GetBaseName(string path) => DependencyProbe.BaseNameOf(path);

    /// <summary>Everything before the final path segment, empty at the workspace root.</summary>
    private static string GetDirectory(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private long GetOrAllocateFileId(string relativePath)
    {
        if (_fileIdsByPath.TryGetValue(relativePath, out var existing))
        {
            return existing;
        }

        var id = _nextFileId++;
        _fileIdsByPath[relativePath] = id;
        return id;
    }

    /// <summary>
    /// Removes every indexed file the run just finished did not see.
    /// <para>
    /// A run crawls the whole of its selection, so a file that survived it is one of three
    /// things: deleted from disk, sitting under a directory the user unchecked, or newly
    /// excluded by an ignore rule or the size cap. All three mean the same thing — the index
    /// is meant to describe the current selection, and anything outside it would go on
    /// answering searches and drawing callers for source the user said not to look at.
    /// </para>
    /// <para>
    /// Only safe after a complete, uncancelled index of the full selection: a partial crawl
    /// leaves a partial touched set, and this would read that as a mass deletion.
    /// </para>
    /// </summary>
    public int RemoveFilesNotSeenThisRun()
    {
        var stale = _fileIdsByPath.Keys
            .Where(path => !_touchedPaths.Contains(path))
            .ToList();

        return DeleteFiles(stale);
    }

    /// <summary>
    /// Removes each path from the index, treating it as both a file and a directory prefix.
    /// <para>
    /// The prefix half is not belt and braces: a deleted directory is announced by its own
    /// path only, with nothing said about the files that went with it.
    /// </para>
    /// </summary>
    public int RemoveFilesAtOrUnder(IEnumerable<string> relativePaths)
    {
        var doomed = new List<string>();

        foreach (var path in relativePaths)
        {
            var normalised = path.Replace('\\', '/').Trim('/');
            if (normalised.Length == 0)
            {
                continue;
            }

            foreach (var indexed in _fileIdsByPath.Keys)
            {
                if (indexed.Equals(normalised, StringComparison.OrdinalIgnoreCase)
                    || indexed.StartsWith(normalised + "/", StringComparison.OrdinalIgnoreCase))
                {
                    doomed.Add(indexed);
                }
            }
        }

        return DeleteFiles(doomed.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private int DeleteFiles(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return 0;
        }

        Gate.Run(() =>
        {
            using var transaction = _connection.BeginTransaction();
            using var command = CreateCommand(transaction, "DELETE FROM file WHERE rel_path = $relPath");

            foreach (var path in relativePaths)
            {
                Set(command, "$relPath", path);
                command.ExecuteNonQuery();

                _fileIdsByPath.Remove(path);
                _fileStamps.Remove(path);
            }

            transaction.Commit();
        });

        return relativePaths.Count;
    }

    /// <summary>Indexed paths at or under any of the given paths. Empty entries are ignored.</summary>
    public List<string> FindIndexedPathsAtOrUnder(IEnumerable<string> relativePaths)
    {
        var matches = new List<string>();

        foreach (var path in relativePaths)
        {
            var normalised = path.Replace('\\', '/').Trim('/');
            if (normalised.Length == 0)
            {
                continue;
            }

            foreach (var indexed in _fileIdsByPath.Keys)
            {
                if (indexed.Equals(normalised, StringComparison.OrdinalIgnoreCase)
                    || indexed.StartsWith(normalised + "/", StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(indexed);
                }
            }
        }

        return matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The id of an indexed file, or null when it is not in the index.</summary>
    public long? FindFileId(string relativePath) =>
        _fileIdsByPath.TryGetValue(relativePath, out var id) ? id : null;

    /// <summary>Every name defined by the given files. Used to compute the dirty-name set.</summary>
    public HashSet<string> ReadDefinedNames(IReadOnlyCollection<long> fileIds)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (fileIds.Count == 0)
        {
            return names;
        }

        Gate.Run(() =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"SELECT DISTINCT name FROM symbol WHERE file_id IN ({string.Join(",", fileIds)})";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        });

        return names;
    }

    /// <summary>
    /// The dependency edges the given files declare, as <c>"file:dep → target"</c> strings.
    /// <para>
    /// An incremental resolve is only exact while the include graph holds still, so the
    /// caller compares this before and after and falls back to a full resolve when it moves.
    /// </para>
    /// </summary>
    public HashSet<string> ReadDependencyEdges(IReadOnlyCollection<long> fileIds)
    {
        var edges = new HashSet<string>(StringComparer.Ordinal);

        if (fileIds.Count == 0)
        {
            return edges;
        }

        Gate.Run(() =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT file_id, dep_path, COALESCE(dep_file_id, -1)
                FROM file_dep
                WHERE file_id IN ({string.Join(",", fileIds)})
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                edges.Add($"{reader.GetInt64(0)}:{reader.GetString(1)}→{reader.GetInt64(2)}");
            }
        });

        return edges;
    }

    /// <summary>Clears the record of which files this run touched, before a new run.</summary>
    public void BeginRun()
    {
        lock (_touchedPaths)
        {
            _touchedPaths.Clear();
            _writtenPaths.Clear();
        }
    }

    /// <summary>Paths re-parsed by the run that just finished.</summary>
    public List<string> TakeWrittenPaths()
    {
        lock (_touchedPaths)
        {
            return _writtenPaths.ToList();
        }
    }

    /// <summary>Key under which the workspace's crawl settings are stored in <c>meta</c>.</summary>
    private const string MetaSettingsJson = "settings_json";

    /// <summary>Persists the workspace's crawl settings alongside the index they shaped.</summary>
    public void SaveSettings(Crawling.WorkspaceSettings settings) => Gate.Run(() =>
        Schema.WriteMeta(_connection, MetaSettingsJson, System.Text.Json.JsonSerializer.Serialize(settings)));

    /// <summary>
    /// Loads the workspace's crawl settings, or the defaults when none were saved or the
    /// stored blob no longer parses — settings are a convenience, never a reason to fail.
    /// </summary>
    public Crawling.WorkspaceSettings LoadSettings() => Gate.Read(() => ReadSettings(_connection));

    /// <summary>
    /// The settings read shared with read-only consumers (the CLI opens its own connection
    /// and has no store). Static so the key and the fallback rule cannot fork; the caller
    /// owns whatever locking its connection needs.
    /// </summary>
    public static Crawling.WorkspaceSettings ReadSettings(SqliteConnection connection)
    {
        var json = Schema.ReadMeta(connection, MetaSettingsJson);
        if (string.IsNullOrEmpty(json))
        {
            return Crawling.WorkspaceSettings.Default;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Crawling.WorkspaceSettings>(json)
                ?? Crawling.WorkspaceSettings.Default;
        }
        catch (System.Text.Json.JsonException)
        {
            return Crawling.WorkspaceSettings.Default;
        }
    }

    /// <summary>
    /// Files whose last parse was imperfect. A null message is a routine syntax error —
    /// tree-sitter recovered and the partial symbols are indexed; a message is a hard
    /// failure that produced nothing.
    /// </summary>
    public List<FileErrorRecord> ReadFileErrors() => Gate.Read(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT rel_path, language, error,
                   (SELECT COUNT(*) FROM symbol s WHERE s.file_id = file.id)
            FROM file
            WHERE status <> 0
            ORDER BY rel_path
            """;

        var results = new List<FileErrorRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FileErrorRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3)));
        }

        return results;
    });

    /// <summary>Persists the user's directory selection so reopening restores it.</summary>
    public void SaveSelectedDirectories(IReadOnlyList<string> relativePaths) => Gate.Run(() =>
    {
        using var transaction = _connection.BeginTransaction();

        using var clear = CreateCommand(transaction, "DELETE FROM selected_dir");
        clear.ExecuteNonQuery();

        using var insert = CreateCommand(transaction, "INSERT INTO selected_dir (rel_path) VALUES ($relPath)");
        foreach (var path in relativePaths)
        {
            Set(insert, "$relPath", path);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    });

    public List<string> LoadSelectedDirectories() => Gate.Read(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT rel_path FROM selected_dir";

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    });

    private SqliteCommand CreateCommand(SqliteTransaction transaction, string sql)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Set(SqliteCommand command, string name, object? value)
    {
        if (command.Parameters.Contains(name))
        {
            command.Parameters[name].Value = value ?? DBNull.Value;
            return;
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}
