using System.Security.Cryptography;
using System.Text;

namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// Where a workspace's index lives. Databases sit under the user's local app data
/// rather than inside the workspace, so indexing never adds files to the user's repo.
/// </summary>
public static class WorkspacePaths
{
    public static string GetRootDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeAnalyzer",
            "workspaces");

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
