using System.Runtime.CompilerServices;
using CodeAnalyzer.Core.Storage;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// Points this assembly's index caches at a temp directory it wipes on the way in. See the
/// copy in the parsing tests for why this is a redirect at the root rather than another
/// cleanup call in another fixture. Separate per assembly so two test projects running at
/// once cannot wipe each other's caches mid-run.
/// </summary>
internal static class TestCacheRedirect
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var root = Path.Combine(Path.GetTempPath(), "codeanalyzer-test-cache", "cli");

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }

        Environment.SetEnvironmentVariable(WorkspacePaths.RootDirectoryVariable, root);
    }
}
