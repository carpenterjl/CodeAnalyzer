namespace CodeAnalyzer.Core.Watching;

/// <summary>
/// A coalesced set of filesystem changes, classified against what is on disk at the moment
/// the batch was closed rather than against the event that announced them.
/// <para>
/// Trusting the event kind does not survive contact with real editors. A save can arrive as
/// Created, Changed, Renamed, or several of those in a row; a directory rename reports one
/// event and none for the files inside it. Looking at disk once the dust has settled gives
/// the same answer whichever path the editor took.
/// </para>
/// </summary>
public sealed record WorkspaceChangeBatch
{
    /// <summary>Workspace-relative paths of files that exist and should be (re)parsed.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    /// <summary>
    /// Workspace-relative directories that should be re-crawled. A directory appears here
    /// when it was created or renamed into place, because the watcher reports one event for
    /// the directory and none for the files that arrived with it.
    /// </summary>
    public IReadOnlyList<string> ChangedDirectories { get; init; } = [];

    /// <summary>
    /// Workspace-relative paths that no longer exist. Each is removed from the index both as
    /// a file and as a directory prefix, since a deleted directory is announced only by its
    /// own path.
    /// </summary>
    public IReadOnlyList<string> RemovedPaths { get; init; } = [];

    /// <summary>
    /// True when the watcher lost events and cannot say what changed — its internal buffer
    /// overflowed. The only honest response is a full re-index; quietly carrying on would
    /// leave the index silently wrong.
    /// </summary>
    public bool ResyncRequired { get; init; }

    public bool IsEmpty =>
        !ResyncRequired
        && ChangedFiles.Count == 0
        && ChangedDirectories.Count == 0
        && RemovedPaths.Count == 0;

    public int TouchedCount => ChangedFiles.Count + ChangedDirectories.Count + RemovedPaths.Count;
}
