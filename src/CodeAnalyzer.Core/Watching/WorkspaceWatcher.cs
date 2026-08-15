using System.IO;
using CodeAnalyzer.Core.Crawling;

namespace CodeAnalyzer.Core.Watching;

/// <summary>
/// Watches a workspace and reports coalesced batches of changes.
/// <para>
/// Two timers, not one. <see cref="QuietPeriod"/> waits for the filesystem to go still, which
/// is what turns an editor's save — often three or four events for one file — into a single
/// batch. <see cref="MaxDelay"/> caps how long that waiting can go on: a build or a branch
/// switch produces a continuous stream, and a pure quiet-period debounce would keep deferring
/// forever and never update anything.
/// </para>
/// <para>
/// Filtering happens twice on purpose. Cheap checks run on the watcher's own callback, where
/// blocking risks overflowing the buffer behind it; the expensive question — is this path a
/// file, a directory, or gone — is asked once per batch, after the changes have settled.
/// </para>
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    /// <summary>
    /// Raised on a timer thread once a batch closes. Never raised for an empty batch, and
    /// never re-entrantly: the next batch starts collecting only after this returns.
    /// </summary>
    public event EventHandler<WorkspaceChangeBatch>? ChangesReady;

    /// <summary>How still the filesystem must go before a batch closes.</summary>
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Longest a batch may be held open while changes keep arriving.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Watcher buffer. The default 8 KB overflows on a branch switch or a build; 64 KB is the
    /// documented maximum that still avoids a non-paged pool penalty, and an overflow is
    /// reported rather than hidden either way.
    /// </summary>
    private const int InternalBufferBytes = 64 * 1024;

    private readonly string _root;
    private readonly Func<string, bool> _isSupportedExtension;
    private readonly WorkspaceSettings _settings;
    private readonly object _sync = new();

    /// <summary>Full paths seen since the current batch opened.</summary>
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _timer;

    /// <summary>Selected directories as normalised relative prefixes. Empty means the whole tree.</summary>
    private string[] _scope = [string.Empty];

    private long _batchOpenedTicks;
    private bool _resyncRequired;
    private bool _flushing;
    private bool _disposed;

    /// <param name="isSupportedExtension">
    /// Decides whether an extension has a language mapping — the same predicate the crawler
    /// uses, so the watcher cannot wake the indexer for a file the crawl would skip.
    /// </param>
    public WorkspaceWatcher(
        string rootPath,
        Func<string, bool> isSupportedExtension,
        WorkspaceSettings? settings = null)
    {
        _root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _isSupportedExtension = isSupportedExtension;
        _settings = settings ?? WorkspaceSettings.Default;
    }

    public bool IsWatching { get; private set; }

    /// <summary>
    /// Supplies the repository's ignore rules at batch time, or null when the workspace
    /// does not honor them. A provider rather than an instance because GitIgnoreRules
    /// caches rule files internally — each batch asks fresh so an edited .gitignore is
    /// respected from the very next batch.
    /// </summary>
    public Func<GitIgnoreRules?>? GitIgnoreProvider { get; init; }

    /// <summary>
    /// Starts watching, limited to the given selection. Restarting with a new selection is
    /// how the watcher follows the user checking or unchecking directories.
    /// </summary>
    public void Start(IReadOnlyList<string> selectedDirectories)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            StopCore();

            _scope = NormaliseScope(selectedDirectories);

            if (!Directory.Exists(_root))
            {
                return;
            }

            var watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = InternalBufferBytes,

                // Attribute and security changes are noise: a virus scanner touching every
                // file in the tree would otherwise read as a workspace-wide edit.
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
            };

            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;

            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            IsWatching = true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        IsWatching = false;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        _timer?.Dispose();
        _timer = null;

        _pending.Clear();
        _resyncRequired = false;
    }

    // ---- Event intake ------------------------------------------------------

    private void OnChanged(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Both ends matter: the old path has to leave the index and the new one has to enter
        // it. Classification later works out which is which by looking at disk.
        Enqueue(e.OldFullPath);
        Enqueue(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_sync)
        {
            if (!IsWatching)
            {
                return;
            }

            _resyncRequired = true;
            Schedule();
        }
    }

    private void Enqueue(string fullPath)
    {
        if (!IsRelevant(fullPath, out var _))
        {
            return;
        }

        lock (_sync)
        {
            if (!IsWatching)
            {
                return;
            }

            _pending.Add(fullPath);
            Schedule();
        }
    }

    /// <summary>
    /// Cheap pre-filter, run on the watcher's callback. It answers "could this path ever
    /// matter" using the path text alone — no disk access, because a slow callback is what
    /// overflows the buffer behind it.
    /// </summary>
    private bool IsRelevant(string fullPath, out string relativePath)
    {
        relativePath = string.Empty;

        if (!fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = fullPath.Length == _root.Length
            ? string.Empty
            : fullPath[(_root.Length + 1)..].Replace('\\', '/');

        if (relative.Length == 0)
        {
            return false;
        }

        var segments = relative.Split('/');

        // Every segment but the last names a directory on the way down. One ignored segment
        // means the crawler would never have descended here.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (_settings.IsIgnoredDirectoryName(segments[i]))
            {
                return false;
            }
        }

        var name = segments[^1];

        // An edited .gitignore changes what the whole crawl means, so it must reach the
        // batch even though its "extension" maps to no language. Classification answers
        // it with a full re-index rather than a parse.
        if (name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = relative;
            return true;
        }
        var extension = Path.GetExtension(name);

        // A path with no extension is very likely a directory, and a directory that has been
        // deleted cannot be interrogated. Let it through and classify it later.
        if (extension.Length > 0)
        {
            if (IgnoreRules.IsIgnoredFileExtension(extension) || !_isSupportedExtension(extension))
            {
                return false;
            }
        }
        else if (_settings.IsIgnoredDirectoryName(name))
        {
            return false;
        }

        if (!IsInScope(relative))
        {
            return false;
        }

        relativePath = relative;
        return true;
    }

    private bool IsInScope(string relativePath)
    {
        foreach (var prefix in _scope)
        {
            if (prefix.Length == 0
                || relativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Batching ----------------------------------------------------------

    /// <summary>Called under <see cref="_sync"/>.</summary>
    private void Schedule()
    {
        if (_timer is null)
        {
            return;
        }

        var now = Environment.TickCount64;

        if (_batchOpenedTicks == 0)
        {
            _batchOpenedTicks = now;
        }

        var heldFor = now - _batchOpenedTicks;
        var remainingBudget = (long)MaxDelay.TotalMilliseconds - heldFor;

        // Extending the quiet period past the cap is what would let a build defer the batch
        // indefinitely, so the delay is clamped to whatever budget is left.
        var delay = Math.Max(0, Math.Min((long)QuietPeriod.TotalMilliseconds, remainingBudget));

        _timer.Change(delay, Timeout.Infinite);
    }

    private void OnTimer(object? state)
    {
        WorkspaceChangeBatch batch;

        lock (_sync)
        {
            if (!IsWatching || _flushing)
            {
                return;
            }

            if (_pending.Count == 0 && !_resyncRequired)
            {
                _batchOpenedTicks = 0;
                return;
            }

            var paths = _pending.ToArray();
            var resync = _resyncRequired;

            _pending.Clear();
            _resyncRequired = false;
            _batchOpenedTicks = 0;

            batch = Classify(paths, resync);

            // Held across the callback so a change arriving mid-notification opens the next
            // batch rather than racing into this one.
            _flushing = true;
        }

        try
        {
            if (!batch.IsEmpty)
            {
                ChangesReady?.Invoke(this, batch);
            }
        }
        finally
        {
            lock (_sync)
            {
                _flushing = false;

                // A change that arrived while the callback ran left the timer disarmed.
                if (_pending.Count > 0 || _resyncRequired)
                {
                    Schedule();
                }
            }
        }
    }

    /// <summary>
    /// Sorts settled paths into parse / re-crawl / remove by asking the filesystem what is
    /// actually there. A rename shows up here as two paths that classify independently.
    /// </summary>
    private WorkspaceChangeBatch Classify(IReadOnlyList<string> fullPaths, bool resyncRequired)
    {
        var files = new List<string>();
        var directories = new List<string>();
        var removed = new List<string>();

        // The cheap per-event filter can only use names; here, with the batch settled and
        // disk access allowed, the structural environment rule and the repository's own
        // .gitignore join in so the watcher agrees with the crawler. Memoised per batch —
        // sibling files share every ancestor.
        var environmentMemo = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var gitIgnore = GitIgnoreProvider?.Invoke();

        foreach (var fullPath in fullPaths)
        {
            // An edited, created or deleted .gitignore invalidates crawl decisions the
            // index already embodies, and nothing tracks which ones. A full re-index is
            // the honest answer, and the size-and-stamp gate makes it cheap. Only when
            // rules are being honored — otherwise the file changes nothing.
            if (Path.GetFileName(fullPath).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
            {
                if (gitIgnore is not null)
                {
                    resyncRequired = true;
                }

                continue;
            }

            if (!IsRelevant(fullPath, out var relative))
            {
                continue;
            }

            if (IsUnderEnvironmentRoot(fullPath, environmentMemo))
            {
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                if (IgnoreRules.IsEnvironmentRoot(fullPath))
                {
                    continue;
                }

                if (gitIgnore?.IsPathIgnoredIncludingAncestors(fullPath, isDirectory: true) == true)
                {
                    continue;
                }

                directories.Add(relative);
            }
            else if (File.Exists(fullPath))
            {
                if (gitIgnore?.IsPathIgnoredIncludingAncestors(fullPath, isDirectory: false) == true)
                {
                    continue;
                }

                // Extension-less files slip past the cheap filter because they look like
                // directories; the crawler would not have indexed them.
                var extension = Path.GetExtension(fullPath);
                if (extension.Length > 0 && _isSupportedExtension(extension))
                {
                    files.Add(relative);
                }
            }
            else
            {
                removed.Add(relative);
            }
        }

        return new WorkspaceChangeBatch
        {
            ChangedFiles = files,
            ChangedDirectories = directories,
            RemovedPaths = removed,
            ResyncRequired = resyncRequired,
        };
    }

    /// <summary>
    /// Walks a path's ancestors (below the workspace root, exclusive) asking whether any
    /// is a Python environment root. Ancestors of a deleted path may be gone too — the
    /// stats just answer false, and the removal flows through, which is harmless: a path
    /// that was never indexed removes nothing.
    /// </summary>
    private bool IsUnderEnvironmentRoot(string fullPath, Dictionary<string, bool> memo)
    {
        var directory = Path.GetDirectoryName(fullPath);

        while (directory is not null
            && directory.Length > _root.Length
            && directory.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            if (!memo.TryGetValue(directory, out var isEnvironment))
            {
                isEnvironment = IgnoreRules.IsEnvironmentRoot(directory);
                memo[directory] = isEnvironment;
            }

            if (isEnvironment)
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static string[] NormaliseScope(IReadOnlyList<string> selectedDirectories)
    {
        if (selectedDirectories.Count == 0)
        {
            return [string.Empty];
        }

        var normalised = selectedDirectories
            .Select(d => d.Replace('\\', '/').Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalised.Contains(string.Empty) ? [string.Empty] : normalised;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCore();
        }
    }
}
