using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Resolution;

/// <summary>
/// Turns an include or import, written the way its language writes it, into the workspace
/// path to go looking for.
/// <para>
/// The text as written is kept separately as the fact; this is only the search key, and an
/// empty result means "this dependency cannot name a file in the workspace". A C# using
/// names a namespace, and a stylesheet href can be an absolute URL: neither has a file to
/// find, so neither is searched for and neither is ever reported as resolved.
/// </para>
/// </summary>
public static class DependencyProbe
{
    /// <summary>
    /// The relative path a dependency would have, or empty when it cannot name a file.
    /// </summary>
    /// <param name="language">Language of the file holding the dependency.</param>
    /// <param name="fromRelativePath">That file's workspace-relative path, forward-slashed.</param>
    /// <param name="dependency">The include or import exactly as written.</param>
    public static string For(string language, string fromRelativePath, string dependency)
    {
        var text = dependency.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return language switch
        {
            LanguageNames.Python => ForPython(fromRelativePath, text),

            // A using directive names a namespace. Namespaces are not files, and C# does
            // not require the two to line up, so guessing at one would be an invention.
            LanguageNames.CSharp => string.Empty,

            LanguageNames.Html => ForMarkup(fromRelativePath, text),

            // C, C++, Verilog: the dependency already is a path.
            _ => ForPath(fromRelativePath, text),
        };
    }

    /// <summary>Final path segment of a probe, used as the indexed seek key.</summary>
    public static string BaseNameOf(string probe)
    {
        var slash = probe.LastIndexOf('/');
        return slash < 0 ? probe : probe[(slash + 1)..];
    }

    /// <summary>
    /// <c>import pkg.mod</c> becomes <c>pkg/mod.py</c>, matched as a suffix anywhere in the
    /// workspace. A relative import is resolved against the importing file's own directory
    /// instead, because that is what the language means by it — one leading dot is "here",
    /// each further dot climbs a level.
    /// </summary>
    private static string ForPython(string fromRelativePath, string text)
    {
        var dots = 0;
        while (dots < text.Length && text[dots] == '.')
        {
            dots++;
        }

        var moduleParts = text[dots..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (dots == 0)
        {
            return moduleParts.Length == 0 ? string.Empty : string.Join('/', moduleParts) + ".py";
        }

        var directory = DirectoryOf(fromRelativePath);

        // The first dot means the importing file's own package; every dot after it is a
        // step up from there.
        for (var i = 1; i < dots; i++)
        {
            if (directory.Length == 0)
            {
                // Climbed past the workspace root: there is nothing here to point at.
                return string.Empty;
            }

            directory = DirectoryOf(directory);
        }

        // `from . import x` names the package itself, which is a directory, not a file.
        if (moduleParts.Length == 0)
        {
            return string.Empty;
        }

        var relative = string.Join('/', moduleParts) + ".py";
        return directory.Length == 0 ? relative : $"{directory}/{relative}";
    }

    /// <summary>
    /// A path written the way C, C++ and Verilog write one.
    /// <para>
    /// Left as written when it has no <c>..</c> in it, because the language's own rule is
    /// "relative to the including file's directory first" and the resolver applies exactly
    /// that. A <c>..</c> has to be collapsed here instead: <c>app/</c> + <c>../drivers/x.h</c>
    /// is not a path any index can seek on, so it is resolved against the including file's
    /// directory and comes back rooted at the workspace.
    /// </para>
    /// </summary>
    private static string ForPath(string fromRelativePath, string text)
    {
        var normalised = Normalise(text);
        return normalised.Contains("..", StringComparison.Ordinal)
            ? Rebase(DirectoryOf(fromRelativePath), normalised)
            : normalised;
    }

    /// <summary>
    /// Walks <paramref name="relative"/> from <paramref name="directory"/>, collapsing
    /// <c>.</c> and <c>..</c>. Climbing past the workspace root yields empty: the target is
    /// outside anything we indexed, and pretending otherwise would resolve it to whatever
    /// happened to share the filename.
    /// </summary>
    private static string Rebase(string directory, string relative)
    {
        var segments = new List<string>(
            directory.Length == 0 ? [] : directory.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment != "..")
            {
                segments.Add(segment);
                continue;
            }

            if (segments.Count == 0)
            {
                return string.Empty;
            }

            segments.RemoveAt(segments.Count - 1);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Strips the parts of a URL that are not path, and rejects anything pointing outside
    /// the workspace: an absolute URL, a protocol-relative one, or a bare fragment.
    /// </summary>
    private static string ForMarkup(string fromRelativePath, string text)
    {
        if (text.StartsWith('#') ||
            text.StartsWith("//", StringComparison.Ordinal) ||
            text.Contains("://", StringComparison.Ordinal) ||
            text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var cut = text.IndexOfAny(['?', '#']);
        return ForPath(fromRelativePath, cut < 0 ? text : text[..cut]);
    }

    private static string Normalise(string path)
    {
        var normalised = path.Replace('\\', '/').TrimStart('/');

        while (normalised.StartsWith("./", StringComparison.Ordinal))
        {
            normalised = normalised[2..];
        }

        return normalised;
    }

    private static string DirectoryOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }
}
