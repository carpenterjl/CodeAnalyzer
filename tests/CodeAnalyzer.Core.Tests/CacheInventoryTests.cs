using CodeAnalyzer.Core.Storage;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// The cache inventory's one judgment call — live, stale, or unreadable — and the rule
/// that prune touches exactly the middle one. A live cache is in use; an unreadable
/// directory cannot prove it is safe to delete; only a cache whose recorded root is gone
/// is the tool's own garbage to collect.
/// </summary>
[Collection(CacheRootCollection.Name)]
public class CacheInventoryTests : IDisposable
{
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), "codeanalyzer-cache-inv", Guid.NewGuid().ToString("N"));

    private readonly string? _previous =
        Environment.GetEnvironmentVariable(WorkspacePaths.RootDirectoryVariable);

    public CacheInventoryTests()
    {
        Directory.CreateDirectory(_sandbox);
        Environment.SetEnvironmentVariable(WorkspacePaths.RootDirectoryVariable, _sandbox);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkspacePaths.RootDirectoryVariable, _previous);
        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }
    }

    /// <summary>Writes a real index database whose meta records the given workspace root.</summary>
    private static void WriteCache(string workspaceRoot)
    {
        using var store = SqliteIndexStore.Open(
            WorkspacePaths.GetDatabasePath(workspaceRoot), workspaceRoot);
    }

    [Fact]
    public void EachDirectoryIsJudgedByItsOwnRecordedOrigin()
    {
        var liveRoot = Path.Combine(_sandbox, "still-here");
        Directory.CreateDirectory(liveRoot);
        WriteCache(liveRoot);

        var staleRoot = Path.Combine(_sandbox, "gone");
        Directory.CreateDirectory(staleRoot);
        WriteCache(staleRoot);
        Directory.Delete(staleRoot, recursive: true);

        Directory.CreateDirectory(Path.Combine(WorkspacePaths.GetRootDirectory(), "empty-imposter"));

        var caches = CacheInventory.Read();

        Assert.Equal(3, caches.Count);
        Assert.Equal(CacheState.Live, caches.Single(c => c.RootPath == liveRoot).State);
        Assert.Equal(CacheState.Stale, caches.Single(c => c.RootPath == staleRoot).State);
        Assert.Equal(CacheState.Unreadable, caches.Single(c => c.RootPath is null).State);
        Assert.All(caches, c => Assert.True(c.Bytes >= 0));
    }

    [Fact]
    public void PruneDeletesStaleCachesAndNothingElse()
    {
        var liveRoot = Path.Combine(_sandbox, "alive");
        Directory.CreateDirectory(liveRoot);
        WriteCache(liveRoot);

        var staleRoot = Path.Combine(_sandbox, "dead");
        Directory.CreateDirectory(staleRoot);
        WriteCache(staleRoot);
        Directory.Delete(staleRoot, recursive: true);

        var unreadable = Path.Combine(WorkspacePaths.GetRootDirectory(), "mystery");
        Directory.CreateDirectory(unreadable);

        var (deleted, bytes, failures) = CacheInventory.Prune(CacheInventory.Read());

        Assert.Equal(1, deleted);
        Assert.True(bytes > 0);
        Assert.Empty(failures);

        var survivors = CacheInventory.Read();
        Assert.Equal(2, survivors.Count);
        Assert.DoesNotContain(survivors, c => c.State == CacheState.Stale);
        Assert.True(Directory.Exists(unreadable));
    }

    [Fact]
    public void AMissingCacheRootIsAnEmptyInventoryNotAnError()
    {
        Environment.SetEnvironmentVariable(
            WorkspacePaths.RootDirectoryVariable, Path.Combine(_sandbox, "never-created"));

        Assert.Empty(CacheInventory.Read());
    }
}
