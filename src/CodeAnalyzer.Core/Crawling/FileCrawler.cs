using System.IO;

namespace CodeAnalyzer.Core.Crawling;

/// <summary>A file the crawler decided is worth parsing.</summary>
public sealed record FileWorkItem(string RelativePath, string FullPath, long Size, long ModifiedUnixMs);

/// <summary>Which directories of a workspace to walk.</summary>
public sealed record WorkspaceSelection(string RootPath, IReadOnlyList<string> RelativeDirectories)
{
    /// <summary>An empty relative path means the whole workspace.</summary>
    public static WorkspaceSelection EntireWorkspace(string rootPath) => new(rootPath, [string.Empty]);
}

public interface IFileCrawler
{
    IEnumerable<FileWorkItem> Crawl(WorkspaceSelection selection, CancellationToken cancellationToken);
}

/// <summary>
/// Walks selected directories and yields indexable files.
/// <para>
/// The walk is iterative rather than recursive so deep trees cannot overflow the stack,
/// and ignored directories are pruned before descending, which is what keeps a large
/// workspace from ballooning into build output and dependency folders.
/// </para>
/// </summary>
public sealed class FileCrawler : IFileCrawler
{
    private readonly WorkspaceSettings _settings;
    private readonly Func<string, bool> _isSupportedExtension;

    /// <param name="isSupportedExtension">
    /// Decides whether an extension has a language mapping. Injected so Core stays
    /// independent of the parsing project.
    /// </param>
    /// <param name="settings">
    /// The workspace's ignore extras and size cap. Defaults to the built-in rules alone.
    /// </param>
    public FileCrawler(
        Func<string, bool> isSupportedExtension,
        WorkspaceSettings? settings = null)
    {
        _isSupportedExtension = isSupportedExtension;
        _settings = settings ?? WorkspaceSettings.Default;
    }

    public IEnumerable<FileWorkItem> Crawl(WorkspaceSelection selection, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(selection.RootPath);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        // Nested selections would otherwise emit the same file more than once.
        var startDirectories = Deduplicate(selection.RelativeDirectories);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativeStart in startDirectories)
        {
            var startPath = string.IsNullOrEmpty(relativeStart)
                ? root
                : Path.Combine(root, relativeStart.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(startPath))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(startPath);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var directory = pending.Pop();
                if (!visited.Add(directory))
                {
                    continue;
                }

                foreach (var subdirectory in SafeEnumerateDirectories(directory))
                {
                    if (!_settings.IsIgnoredDirectoryName(Path.GetFileName(subdirectory)))
                    {
                        pending.Push(subdirectory);
                    }
                }

                foreach (var file in SafeEnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var item = TryCreateWorkItem(root, file);
                    if (item is not null)
                    {
                        yield return item;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Drops any selected directory that already sits under another selected directory,
    /// and collapses everything to the root when the root itself is selected.
    /// </summary>
    private static List<string> Deduplicate(IReadOnlyList<string> relativeDirectories)
    {
        var normalized = relativeDirectories
            .Select(d => d.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Length)
            .ToList();

        if (normalized.Contains(string.Empty))
        {
            return [string.Empty];
        }

        var kept = new List<string>();
        foreach (var candidate in normalized)
        {
            var isNested = kept.Any(existing =>
                candidate.StartsWith(existing + "/", StringComparison.OrdinalIgnoreCase));

            if (!isNested)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    /// <summary>
    /// Applies the extension, size and readability rules to one path. Public so a targeted
    /// re-index of named files decides exactly what a full crawl would have decided.
    /// </summary>
    public FileWorkItem? TryCreateWorkItem(string root, string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        if (extension.Length == 0
            || IgnoreRules.IsIgnoredFileExtension(extension)
            || !_isSupportedExtension(extension))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > _settings.MaxFileSizeBytes)
            {
                return null;
            }

            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');

            return new FileWorkItem(
                relative,
                fullPath,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }
}
