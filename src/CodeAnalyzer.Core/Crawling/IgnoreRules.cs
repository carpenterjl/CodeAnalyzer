using System.IO;

namespace CodeAnalyzer.Core.Crawling;

/// <summary>
/// Directories and files the crawler skips. Kept in Core so the workspace tree and the
/// crawler agree on what is excluded — the tree greys out exactly what the crawler will skip.
/// </summary>
public static class IgnoreRules
{
    /// <summary>
    /// Directory names pruned before descending. Pruning at the directory level is what keeps
    /// a 20k-file crawl from turning into a 200k-file crawl through build output.
    /// </summary>
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".bzr",
        ".vs", ".vscode", ".idea",
        "bin", "obj", "build", "dist", "out", "target",
        "node_modules", "bower_components", "vendor",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".tox",
        ".venv", "venv", "env",
        "packages", ".nuget",
        ".next", ".nuxt", ".cache",
        "CMakeFiles",
        "site-packages", "TestResults",
        "x64", "Win32", "ARM64",

        // Debug and Release are deliberately NOT here. Extras are add-only: a user whose
        // build outputs to Debug/ fixes it with one settings line, but a user whose
        // *source* lives in Debug/ would have no way to un-ignore a built-in at all.
    };

    /// <summary>File extensions that never contain source we can index.</summary>
    private static readonly HashSet<string> IgnoredFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".a", ".lib", ".o", ".obj", ".pdb", ".ilk",
        ".zip", ".tar", ".gz", ".7z", ".rar", ".bz2", ".xz",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp", ".tiff",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac", ".mkv",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".db", ".sqlite", ".sqlite3", ".mdb",
        ".bin", ".dat", ".iso", ".img",
        ".pyc", ".pyo", ".class", ".jar",
    };

    public static bool IsIgnoredDirectoryName(string directoryName) =>
        IgnoredDirectoryNames.Contains(directoryName)
        || (directoryName.Length > 1 && directoryName[0] == '.' && directoryName != "..");

    public static bool IsIgnoredFileExtension(string extension) =>
        IgnoredFileExtensions.Contains(extension);

    /// <summary>
    /// True for a minified bundle: <c>cytoscape.min.js</c> and friends.
    /// <para>
    /// A minifier rewrites every local name to one or two letters, so the symbols such a
    /// file yields are <c>t</c>, <c>e</c> and <c>n</c> — nobody can search for them and no
    /// caller list built from them means anything. It is not a marginal call: adding the
    /// JavaScript pack to this repo took its index from 5,000 definitions to 21,827 and
    /// its links from 11,236 to 306,922, almost all of it three vendored <c>.min.js</c>
    /// files, and a re-index from 0.6 s to 138 s. The readable source those were built
    /// from is what belongs in an index; the build output of it never is.
    /// </para>
    /// <para>
    /// Deliberately narrow. It matches the <c>.min.js</c> naming convention and nothing
    /// else — no line-length heuristic, which would eventually refuse a real file someone
    /// wrote long lines in.
    /// </para>
    /// </summary>
    public static bool IsMinifiedBundle(string fileName) =>
        fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".min.mjs", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".min.cjs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a directory is the root of a Python environment, whatever it is named.
    /// A venv declares itself with its own pyvenv.cfg (conda with conda-meta); the
    /// name list above catches .venv/venv/env, but a checked-in environment called
    /// anything else — the reported case was "Environment", 4,458 parseable files —
    /// walks straight past a name rule. Cost: two stats per crawled directory, beside
    /// an enumeration that already touches it.
    /// </summary>
    public static bool IsEnvironmentRoot(string directoryFullPath) =>
        File.Exists(Path.Combine(directoryFullPath, "pyvenv.cfg"))
        || Directory.Exists(Path.Combine(directoryFullPath, "conda-meta"));

    /// <summary>
    /// Default cap on file size. Anything larger is almost always generated or vendored,
    /// and parsing it would stall a worker for no useful symbols.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Heuristic binary sniff: a NUL byte inside the leading window means we should not
    /// hand this to a text parser. Mirrors what git does.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> leadingBytes) =>
        leadingBytes.IndexOf((byte)0) >= 0;

    public const int BinarySniffWindowBytes = 8192;
}
