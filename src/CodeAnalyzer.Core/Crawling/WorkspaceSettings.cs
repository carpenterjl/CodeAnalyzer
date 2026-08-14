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
