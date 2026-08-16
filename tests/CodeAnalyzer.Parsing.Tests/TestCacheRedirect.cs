using System.Runtime.CompilerServices;
using CodeAnalyzer.Core.Storage;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Points this assembly's index caches at a temp directory it wipes on the way in, before a
/// single test runs.
/// <para>
/// Every test that opens a workspace writes a database keyed by a hash of its root, and a
/// test root is unique per run — so a fixture that forgets to delete its cache leaks a
/// directory per run into local app data, permanently.
/// <para>
/// Prevention, not a cure: measured before this was added, a full run already leaked
/// nothing, because every fixture that opens a session does call
/// <see cref="WorkspaceCacheCleanup"/>. Those calls stay. What they cannot do is bind a
/// fixture nobody has written yet, and the 692 orphaned directories that prompted this were
/// left by the era before they existed.
/// </para>
/// <para>
/// Wiping at module load rather than at process exit is deliberate: an exit hook does not
/// reliably run under a test host, and a directory reused each run cannot grow without
/// bound whether anything cleans it or not.
/// </para>
/// </para>
/// </summary>
internal static class TestCacheRedirect
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "codeanalyzer-test-cache", "parsing");

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // A previous run's file still being held is not a reason to fail every test.
        }

        Environment.SetEnvironmentVariable(WorkspacePaths.RootDirectoryVariable, root);
    }
}
