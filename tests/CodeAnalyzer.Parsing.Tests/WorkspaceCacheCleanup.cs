using CodeAnalyzer.Core.Storage;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Deletes the per-user cache directory a <c>WorkspaceSession</c> created for a test
/// workspace.
/// <para>
/// Session-level tests open real workspaces, and <c>WorkspacePaths</c> puts each one's
/// index under the user's local app data — which is right for the app and wrong for a
/// test, where the GUID-named roots mean every run leaks a fresh cache directory into the
/// user's profile forever. Every fixture that calls <c>WorkspaceSession.Open</c> must call
/// this from its dispose path, after disposing the session (SQLite holds the file open).
/// </para>
/// </summary>
internal static class WorkspaceCacheCleanup
{
    public static void Delete(string workspaceRoot)
    {
        try
        {
            Directory.Delete(WorkspacePaths.GetWorkspaceDirectory(workspaceRoot), recursive: true);
        }
        catch (Exception e) when (e is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Cache cleanup failures are not test failures.
        }
    }
}
