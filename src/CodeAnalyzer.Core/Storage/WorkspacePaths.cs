using System.Security.Cryptography;
using System.Text;

namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// Where a workspace's index lives. Databases sit under the user's local app data
/// rather than inside the workspace, so indexing never adds files to the user's repo.
/// </summary>
public static class WorkspacePaths
{
    /// <summary>
    /// Relocates the whole cache, replacing the <c>CodeAnalyzer</c> folder under local app
    /// data. Set it to keep several gigabytes of derived index off a small system drive.
    /// <para>
    /// It exists because of what the default costs a test suite. Every test that opens a
    /// workspace writes a database keyed by a hash of its temp root, and a temp root is
    /// unique per run — so a fixture that forgets to delete its cache leaks one directory
    /// per run, forever, into a folder nobody looks at. The machine this was written on had
    /// 692 such directories and 215 MB of them.
    /// </para>
    /// <para>
    /// Every fixture that opens a session does now clean up after itself, and a full run
    /// measurably leaks nothing without this. So the redirect is not the fix for that
    /// residue — it is what stops the same debt accruing again from the next fixture, which
    /// has to remember a rule it cannot see. A workspace root is also the one thing a test
    /// controls and a cache path is derived from, so redirecting the derivation is the only
    /// place the guarantee can be made once.
    /// </para>
    /// </summary>
    public const string RootDirectoryVariable = "CODEANALYZER_CACHE_ROOT";

    public static string GetRootDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(RootDirectoryVariable);

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeAnalyzer",
                "workspaces")
            : Path.Combine(Path.GetFullPath(configured), "workspaces");
    }

    /// <summary>
    /// A stable per-workspace directory keyed by a hash of the absolute root path, with a
    /// readable prefix so the folder is identifiable when browsing on disk.
    /// </summary>
    public static string GetWorkspaceDirectory(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant())))[..16];

        var label = Sanitize(Path.GetFileName(normalized));
        if (label.Length == 0)
        {
            label = "workspace";
        }

        return Path.Combine(GetRootDirectory(), $"{label}-{hash}");
    }

    public static string GetDatabasePath(string workspaceRoot) =>
        Path.Combine(GetWorkspaceDirectory(workspaceRoot), "index.db");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.ToString();
    }
}
