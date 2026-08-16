using Microsoft.Data.Sqlite;

namespace CodeAnalyzer.Core.Storage;

/// <summary>What one cached workspace directory is, judged from its own recorded origin.</summary>
public enum CacheState
{
    /// <summary>The recorded workspace root still exists on disk.</summary>
    Live,

    /// <summary>The recorded workspace root is gone — the cache can serve nobody.</summary>
    Stale,

    /// <summary>No readable database, so the origin cannot be judged at all.</summary>
    Unreadable,
}

/// <summary>One directory under the cache root, and where it says it came from.</summary>
public sealed record CachedWorkspace(
    string Directory,
    string? RootPath,
    string? LastIndexUtc,
    long Bytes,
    CacheState State);

/// <summary>
/// The cache tree, read against the origin each database records about itself. Every
/// workspace's <c>meta.root_path</c> was written at index time precisely so this judgment
/// could be made later: a cache whose root no longer exists is what "stale" means, and
/// nothing else is — a cache merely unopened for a year may belong to a project the user
/// still cares about.
/// </summary>
public static class CacheInventory
{
    public static IReadOnlyList<CachedWorkspace> Read()
    {
        var root = WorkspacePaths.GetRootDirectory();
        if (!Directory.Exists(root))
        {
            return [];
        }

        var caches = new List<CachedWorkspace>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            caches.Add(Judge(directory));
        }

        return caches
            .OrderBy(c => c.State)
            .ThenBy(c => c.RootPath ?? c.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Deletes every stale cache in the list and reports what went. Only
    /// <see cref="CacheState.Stale"/> is touched: live caches are in use, and an unreadable
    /// directory cannot prove it is safe to delete, so it is left for a human.
    /// </summary>
    public static (int Deleted, long Bytes, IReadOnlyList<string> Failures) Prune(
        IReadOnlyList<CachedWorkspace> caches)
    {
        var deleted = 0;
        long bytes = 0;
        var failures = new List<string>();

        foreach (var cache in caches.Where(c => c.State == CacheState.Stale))
        {
            try
            {
                Directory.Delete(cache.Directory, recursive: true);
                deleted++;
                bytes += cache.Bytes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{cache.Directory}: {e.Message}");
            }
        }

        return (deleted, bytes, failures);
    }

    private static CachedWorkspace Judge(string directory)
    {
        var bytes = SizeOf(directory);
        var databasePath = Path.Combine(directory, "index.db");
        if (!File.Exists(databasePath))
        {
            return new CachedWorkspace(directory, null, null, bytes, CacheState.Unreadable);
        }

        try
        {
            // Pooling off so the handle closes with the connection — a pooled handle
            // would hold the very file a prune is about to delete.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            var rootPath = Schema.ReadMeta(connection, Schema.MetaRootPath);
            var lastIndex = Schema.ReadMeta(connection, Schema.MetaLastIndexUtc);

            if (rootPath is null)
            {
                return new CachedWorkspace(directory, null, lastIndex, bytes, CacheState.Unreadable);
            }

            var state = Directory.Exists(rootPath) ? CacheState.Live : CacheState.Stale;
            return new CachedWorkspace(directory, rootPath, lastIndex, bytes, state);
        }
        catch (SqliteException)
        {
            return new CachedWorkspace(directory, null, null, bytes, CacheState.Unreadable);
        }
    }

    private static long SizeOf(string directory)
    {
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that vanished mid-walk changes the size, not the judgment.
        }

        return bytes;
    }
}
