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
