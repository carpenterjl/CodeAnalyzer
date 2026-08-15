using System.IO;
using System.Text.RegularExpressions;

namespace CodeAnalyzer.Core.Crawling;

/// <summary>
/// The repository's own statement of what is not source: .gitignore rules, applied during
/// the crawl when the workspace opts in.
/// <para>
/// Discovery walks up from the workspace root to find the repository (the workspace is
/// often a subfolder of it), loads <c>.git/info/exclude</c> and every <c>.gitignore</c>
/// from the git root down, and lazily picks up nested <c>.gitignore</c> files as paths
/// under them are asked about. Git's precedence is kept: deeper files win, later lines in
/// a file win, and <c>info/exclude</c> is checked first so everything else outranks it.
/// </para>
/// <para>
/// The matcher covers the subset real repositories use: comments and blanks, <c>!</c>
/// negation with last-match-wins, trailing-<c>/</c> directory-only rules, anchoring by
/// any non-trailing slash, <c>*</c>, <c>?</c> and <c>**</c>. Character classes
/// (<c>[abc]</c>) and backslash escapes are not supported — a pattern using them is
/// ignored entirely rather than half-matched, which errs toward indexing too much, never
/// toward silently dropping source. One git rule is deliberately not implemented:
/// re-including (<c>!</c>) inside an excluded directory, because the crawler prunes
/// excluded directories before ever reaching their contents — the same reason git itself
/// documents that rule as a no-op without a matching directory re-include.
/// </para>
/// <para>
/// Not thread-safe: each consumer (a crawl, a watcher batch, the workspace tree) creates
/// its own instance, which is cheap — rule files load once per directory per instance.
/// </para>
/// </summary>
public sealed class GitIgnoreRules
{
    private readonly string _gitRoot;

    /// <summary>Patterns from .git/info/exclude, scoped to the git root, lowest precedence.</summary>
    private readonly IReadOnlyList<CompiledPattern> _excludePatterns;

    /// <summary>
    /// Per-directory .gitignore contents, keyed by the directory's path relative to the
    /// git root ("" for the root itself), loaded on first use. Null marks "looked, none".
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<CompiledPattern>?> _byDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    private GitIgnoreRules(string gitRoot, IReadOnlyList<CompiledPattern> excludePatterns)
    {
        _gitRoot = gitRoot;
        _excludePatterns = excludePatterns;
    }

    /// <summary>The directory containing .git — rules are evaluated relative to it.</summary>
    public string GitRootPath => _gitRoot;

    /// <summary>
    /// Whether any rule file was actually found between the git root and the workspace
    /// root. This is what gates the ask-once prompt: a repository with no .gitignore has
    /// nothing to honor, so there is nothing to ask about.
    /// </summary>
    public bool HasAnyRules { get; private init; }

    /// <summary>
    /// Finds the enclosing git repository and loads its rules, or returns null when the
    /// workspace is not inside one. A .git *file* (a worktree or submodule) still marks
    /// the root; its info/exclude lives elsewhere and is skipped — .gitignore files are
    /// what carry the rules that matter in practice.
    /// </summary>
    public static GitIgnoreRules? TryDiscover(string workspaceRoot)
    {
        string? current;
        try
        {
            current = Path.GetFullPath(workspaceRoot);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return null;
        }

        string? gitRoot = null;
        var isGitDirectory = false;

        while (current is not null)
        {
            var gitPath = Path.Combine(current, ".git");

            if (Directory.Exists(gitPath))
            {
                gitRoot = current;
                isGitDirectory = true;
                break;
            }

            if (File.Exists(gitPath))
            {
                gitRoot = current;
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        if (gitRoot is null)
        {
            return null;
        }

        var exclude = isGitDirectory
            ? LoadPatternFile(Path.Combine(gitRoot, ".git", "info", "exclude"))
            : null;

        var rules = new GitIgnoreRules(gitRoot, exclude ?? [])
        {
            HasAnyRules = exclude is { Count: > 0 }
                || AnyGitIgnoreBetween(gitRoot, Path.GetFullPath(workspaceRoot)),
        };

        return rules;
    }

    /// <summary>True when the repository says this directory is not source.</summary>
    public bool IsDirectoryIgnored(string fullPath) => IsIgnored(fullPath, isDirectory: true);

    /// <summary>True when the repository says this file is not source.</summary>
    public bool IsFileIgnored(string fullPath) => IsIgnored(fullPath, isDirectory: false);

    /// <summary>
    /// The check for a path that arrives without its ancestors having been walked — the
    /// watcher's batch classification. The crawler never needs this: it prunes an ignored
    /// directory before reaching anything inside it, but a watcher event lands directly
    /// on a file whose parent may be the thing the rules name.
    /// </summary>
    public bool IsPathIgnoredIncludingAncestors(string fullPath, bool isDirectory)
    {
        var relative = ToGitRelative(fullPath);
        if (relative is null || relative.Length == 0)
        {
            return false;
        }

        var segments = relative.Split('/');
        var prefix = string.Empty;

        for (var i = 0; i < segments.Length; i++)
        {
            prefix = i == 0 ? segments[0] : $"{prefix}/{segments[i]}";
            var isLast = i == segments.Length - 1;

            if (IsRelativeIgnored(prefix, isLast ? isDirectory : true))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsIgnored(string fullPath, bool isDirectory)
    {
        var relative = ToGitRelative(fullPath);
        if (relative is null || relative.Length == 0)
        {
            return false;
        }

        return IsRelativeIgnored(relative, isDirectory);
    }

    private bool IsRelativeIgnored(string relative, bool isDirectory)
    {
        // Last match wins, so evaluation runs lowest-precedence first: info/exclude,
        // then each .gitignore from the git root down toward the path.
        bool? decision = null;

        Apply(_excludePatterns, relative, isDirectory, ref decision);

        var separators = relative.Split('/');
        var scope = string.Empty;

        for (var depth = 0; depth < separators.Length; depth++)
        {
            var patterns = PatternsFor(scope);
            if (patterns is not null)
            {
                // A pattern matches against the path relative to its own .gitignore.
                var scoped = scope.Length == 0 ? relative : relative[(scope.Length + 1)..];
                Apply(patterns, scoped, isDirectory, ref decision);
            }

            // Descend one level for the next iteration; the last segment is the path's
            // own name and holds no .gitignore that could apply to it.
            if (depth < separators.Length - 1)
            {
                scope = scope.Length == 0 ? separators[depth] : $"{scope}/{separators[depth]}";
            }
        }

        return decision == true;
    }

    private static void Apply(
        IReadOnlyList<CompiledPattern> patterns,
        string scopedRelativePath,
        bool isDirectory,
        ref bool? decision)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            if (pattern.Regex.IsMatch(scopedRelativePath))
            {
                decision = !pattern.Negated;
            }
        }
    }

    private IReadOnlyList<CompiledPattern>? PatternsFor(string scopeRelative)
    {
        if (_byDirectory.TryGetValue(scopeRelative, out var cached))
        {
            return cached;
        }

        var directory = scopeRelative.Length == 0
            ? _gitRoot
            : Path.Combine(_gitRoot, scopeRelative.Replace('/', Path.DirectorySeparatorChar));

        var patterns = LoadPatternFile(Path.Combine(directory, ".gitignore"));
        _byDirectory[scopeRelative] = patterns;
        return patterns;
    }

    private string? ToGitRelative(string fullPath)
    {
        if (!fullPath.StartsWith(_gitRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (fullPath.Length == _gitRoot.Length)
        {
            return string.Empty;
        }

        var next = fullPath[_gitRoot.Length];
        if (next is not ('\\' or '/'))
        {
            return null;
        }

        return fullPath[(_gitRoot.Length + 1)..].Replace('\\', '/').TrimEnd('/');
    }

    private static bool AnyGitIgnoreBetween(string gitRoot, string workspaceRoot)
    {
        var current = workspaceRoot;

        while (current is not null && current.Length >= gitRoot.Length)
        {
            if (File.Exists(Path.Combine(current, ".gitignore")))
            {
                return true;
            }

            if (string.Equals(current, gitRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static IReadOnlyList<CompiledPattern>? LoadPatternFile(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var patterns = new List<CompiledPattern>();

        foreach (var raw in lines)
        {
            var compiled = CompiledPattern.TryParse(raw);
            if (compiled is not null)
            {
                patterns.Add(compiled);
            }
        }

        return patterns;
    }

    private sealed record CompiledPattern(Regex Regex, bool Negated, bool DirectoryOnly)
    {
        public static CompiledPattern? TryParse(string rawLine)
        {
            var line = rawLine.TrimEnd();

            if (line.Length == 0 || line[0] == '#')
            {
                return null;
            }

            var negated = line[0] == '!';
            if (negated)
            {
                line = line[1..];
            }

            var directoryOnly = line.EndsWith('/');
            if (directoryOnly)
            {
                line = line.TrimEnd('/');
            }

            if (line.Length == 0)
            {
                return null;
            }

            // Unsupported syntax is dropped whole rather than half-matched: a wrong
            // "ignore" silently loses source, a wrong "keep" only indexes extra.
            if (line.Contains('[') || line.Contains('\\'))
            {
                return null;
            }

            // A slash anywhere except the very end anchors the pattern to the rule
            // file's own directory; without one it floats, matching a name at any depth.
            var anchored = line.IndexOf('/') >= 0;
            if (line.StartsWith('/'))
            {
                line = line[1..];
                if (line.Length == 0)
                {
                    return null;
                }
            }

            var body = TranslateGlob(line);
            var pattern = anchored
                ? $"^{body}$"
                : $"(^|/){body}$";

            return new CompiledPattern(
                new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                negated,
                directoryOnly);
        }

        private static string TranslateGlob(string glob)
        {
            var result = new System.Text.StringBuilder();
            var i = 0;

            while (i < glob.Length)
            {
                var c = glob[i];

                if (c == '*')
                {
                    var isDouble = i + 1 < glob.Length && glob[i + 1] == '*';

                    if (isDouble)
                    {
                        // "**/" spans any number of directories including none;
                        // a trailing "**" spans the rest of the path.
                        if (i + 2 < glob.Length && glob[i + 2] == '/')
                        {
                            result.Append("(.*/)?");
                            i += 3;
                            continue;
                        }

                        result.Append(".*");
                        i += 2;
                        continue;
                    }

                    result.Append("[^/]*");
                    i++;
                    continue;
                }

                if (c == '?')
                {
                    result.Append("[^/]");
                    i++;
                    continue;
                }

                result.Append(Regex.Escape(c.ToString()));
                i++;
            }

            return result.ToString();
        }
    }
}
