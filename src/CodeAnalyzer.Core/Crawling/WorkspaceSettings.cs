using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Crawling;

/// <summary>
/// Per-workspace crawl settings the user can change: extra ignored directory names on top
/// of the built-in <see cref="IgnoreRules"/>, and the file size cap.
/// <para>
/// Stored with the workspace index rather than with the app, because they describe the
/// workspace — the same tool pointed at two checkouts wants two answers. Everything that
/// decides what gets crawled takes one of these, so the crawler, the watcher and the
/// workspace tree cannot disagree about what is excluded.
/// </para>
/// </summary>
public sealed record WorkspaceSettings
{
    public static readonly WorkspaceSettings Default = new();

    /// <summary>Directory names to prune in addition to the built-in list. Names, not paths.</summary>
    public IReadOnlyList<string> ExtraIgnoredDirectories { get; init; } = [];

    /// <summary>Files larger than this are skipped as almost certainly generated or vendored.</summary>
    public long MaxFileSizeBytes { get; init; } = IgnoreRules.DefaultMaxFileSizeBytes;

    /// <summary>
    /// Whether the crawl honors the repository's .gitignore. Three states on purpose:
    /// null means the user has never been asked — the shell asks once, the first time a
    /// workspace with a .gitignore is opened, and stores the answer here. A corrupt
    /// settings blob falls back to defaults, which maps to null, i.e. ask again — the
    /// safe direction.
    /// </summary>
    public bool? HonorGitIgnore { get; init; }

    /// <summary>
    /// The user's own I/O boundary marks. They live with the crawl settings because they
    /// describe the workspace, but unlike the ignore rules they are read at query time —
    /// editing a mark never requires a re-index.
    /// </summary>
    public IReadOnlyList<IoMark> IoMarks { get; init; } = [];

    private HashSet<string>? _extraSet;

    /// <summary>The built-in rules plus this workspace's extras.</summary>
    public bool IsIgnoredDirectoryName(string directoryName)
    {
        if (IgnoreRules.IsIgnoredDirectoryName(directoryName))
        {
            return true;
        }

        _extraSet ??= new HashSet<string>(
            ExtraIgnoredDirectories.Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        return _extraSet.Contains(directoryName);
    }

    /// <summary>
    /// The full ignore decision for a directory that exists on disk: the name rules plus
    /// the structural environment check. Every consumer that has the full path in hand —
    /// the crawler, the workspace tree, the watcher's batch classification — goes through
    /// this one method, so they cannot disagree about what is excluded. The name-only
    /// overload above remains for the watcher's cheap per-event path check, where a disk
    /// stat on the callback risks overflowing the buffer behind it.
    /// </summary>
    public bool IsIgnoredDirectory(string fullPath, string directoryName) =>
        IsIgnoredDirectoryName(directoryName) || IgnoreRules.IsEnvironmentRoot(fullPath);

    /// <summary>
    /// Parses the settings dialog's free-form list: one name per line, commas and
    /// semicolons tolerated. An entry containing a path separator is dropped rather than
    /// kept broken — ignore rules match single directory names, never paths.
    /// </summary>
    public static IReadOnlyList<string> ParseDirectoryNames(string text) =>
        text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => !n.Contains('/') && !n.Contains('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
