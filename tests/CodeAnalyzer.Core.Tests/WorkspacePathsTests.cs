using CodeAnalyzer.Core.Storage;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// Where index databases land, and the override that keeps a test suite from filling up
/// local app data one forgotten fixture at a time.
/// </summary>
public class WorkspacePathsTests
{
    /// <summary>
    /// Restores whatever the variable was, so these tests do not redirect the caches of
    /// every test that runs after them.
    /// </summary>
    private sealed class Override : IDisposable
    {
        private readonly string? _previous =
            Environment.GetEnvironmentVariable(WorkspacePaths.RootDirectoryVariable);

        public Override(string? value) =>
            Environment.SetEnvironmentVariable(WorkspacePaths.RootDirectoryVariable, value);

        public void Dispose() =>
            Environment.SetEnvironmentVariable(WorkspacePaths.RootDirectoryVariable, _previous);
    }

    [Fact]
    public void TheCacheRootFollowsTheEnvironmentVariable()
    {
        using var _ = new Override(Path.Combine(Path.GetTempPath(), "ca-root-test"));

        var root = WorkspacePaths.GetRootDirectory();

        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "ca-root-test"), root, StringComparison.Ordinal);
        Assert.EndsWith("workspaces", root, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsetOrBlankVariableFallsBackToLocalAppData()
    {
        // Blank as well as unset: an empty variable is how a shell "clears" one, and taking
        // it literally would put every index in a directory named after the drive root.
        foreach (var value in new[] { null, "", "   " })
        {
            using var _ = new Override(value);

            Assert.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                WorkspacePaths.GetRootDirectory(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TwoWorkspacesNeverShareADirectoryAndOneIsStable()
    {
        using var _ = new Override(Path.Combine(Path.GetTempPath(), "ca-root-test"));

        var first = WorkspacePaths.GetWorkspaceDirectory(Path.Combine(Path.GetTempPath(), "alpha"));
        var second = WorkspacePaths.GetWorkspaceDirectory(Path.Combine(Path.GetTempPath(), "beta"));

        Assert.NotEqual(first, second);
        Assert.Equal(first, WorkspacePaths.GetWorkspaceDirectory(Path.Combine(Path.GetTempPath(), "alpha")));

        // The readable prefix is the point of the naming scheme — a hash alone would make
        // the folder that is filling the drive unidentifiable.
        Assert.Contains("alpha", Path.GetFileName(first), StringComparison.Ordinal);
    }
}
