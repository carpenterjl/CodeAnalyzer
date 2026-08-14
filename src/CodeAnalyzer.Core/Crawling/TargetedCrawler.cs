using System.IO;

namespace CodeAnalyzer.Core.Crawling;

/// <summary>
/// Crawls a named set of files, plus whatever a full crawl of the given directories finds.
/// <para>
/// A live update knows exactly which paths changed, so walking the workspace to rediscover
/// them would be the whole cost of indexing for none of the benefit. Reusing
/// <see cref="FileCrawler"/> for both halves is what guarantees a targeted pass applies the
/// same extension, size and readability rules a full pass would.
/// </para>
/// </summary>
public sealed class TargetedCrawler : IFileCrawler
{
    private readonly FileCrawler _crawler;
    private readonly IReadOnlyList<string> _relativeFiles;

    /// <param name="relativeFiles">
    /// Workspace-relative paths to index directly. Directories come from the
    /// <see cref="WorkspaceSelection"/> passed to <see cref="Crawl"/>.
    /// </param>
    public TargetedCrawler(FileCrawler crawler, IReadOnlyList<string> relativeFiles)
    {
        _crawler = crawler;
        _relativeFiles = relativeFiles;
    }

    public IEnumerable<FileWorkItem> Crawl(WorkspaceSelection selection, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(selection.RootPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in _relativeFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var item = _crawler.TryCreateWorkItem(root, fullPath);

            if (item is not null && seen.Add(item.RelativePath))
            {
                yield return item;
            }
        }

        if (selection.RelativeDirectories.Count == 0)
        {
            yield break;
        }

        // A directory that was created or renamed into place arrives as one event, so its
        // contents have to be discovered the ordinary way.
        foreach (var item in _crawler.Crawl(selection, cancellationToken))
        {
            if (seen.Add(item.RelativePath))
            {
                yield return item;
            }
        }
    }
}
