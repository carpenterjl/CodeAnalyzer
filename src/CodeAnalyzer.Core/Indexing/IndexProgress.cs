namespace CodeAnalyzer.Core.Indexing;

public enum IndexPhase
{
    Idle,
    Crawling,
    Parsing,
    Resolving,
    Complete,
    Cancelled,
}

/// <summary>
/// A progress snapshot. Reported through IProgress and throttled, so the UI thread only
/// ever applies a handful of updates per second regardless of indexing speed.
/// </summary>
public sealed record IndexProgress
{
    public IndexPhase Phase { get; init; } = IndexPhase.Idle;

    public int FilesDiscovered { get; init; }

    public int FilesParsed { get; init; }

    /// <summary>Files whose content hash matched the stored one, so parsing was skipped.</summary>
    public int FilesUnchanged { get; init; }

    /// <summary>Files that produced no result at all: unreadable, or binary despite a source extension.</summary>
    public int FilesFailed { get; init; }

    /// <summary>
    /// Files that were parsed but contained syntax errors. These still contribute symbols,
    /// so they are counted separately from <see cref="FilesFailed"/> rather than as skipped.
    /// </summary>
    public int FilesWithSyntaxErrors { get; init; }

    public int SymbolsFound { get; init; }

    public string? CurrentFile { get; init; }

    /// <summary>
    /// Null while crawling, because the total is not known until the walk finishes.
    /// The UI shows an indeterminate bar in that case.
    /// </summary>
    public double? PercentComplete =>
        Phase is IndexPhase.Crawling or IndexPhase.Idle || FilesDiscovered == 0
            ? null
            : Math.Min(100d, (FilesParsed + FilesUnchanged + FilesFailed) * 100d / FilesDiscovered);
}
