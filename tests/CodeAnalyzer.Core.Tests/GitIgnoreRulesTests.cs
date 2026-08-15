using CodeAnalyzer.Core.Crawling;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// The .gitignore matcher against a real on-disk repository shape. Table cases cover the
/// supported subset; the discovery tests cover finding the repository from a workspace
/// opened below its root, which is exactly the reported situation.
/// </summary>
public sealed class GitIgnoreRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeanalyzer-tests", Guid.NewGuid().ToString("N"));

    public GitIgnoreRulesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void MakeRepo(string gitIgnoreContent, string relativeGitIgnoreDir = "")
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        var dir = relativeGitIgnoreDir.Length == 0
            ? _root
            : Path.Combine(_root, relativeGitIgnoreDir.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), gitIgnoreContent);
    }

    private string Full(string relative) =>
        Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    // ---- Matching ----------------------------------------------------------

    [Theory]
    // Floating name: matches at any depth, files and directories alike.
    [InlineData("Debug", "Debug", true, true)]
    [InlineData("Debug", "Firmware/STM32/Debug", true, true)]
    [InlineData("Debug", "src/Debugger", true, false)]
    // Directory-only rule ignores a directory but not a file of the same name.
    [InlineData("Debug/", "Firmware/Debug", true, true)]
    [InlineData("Debug/", "notes/Debug", false, false)]
    // Anchored: a slash pins the pattern to the .gitignore's own directory.
    [InlineData("/build", "build", true, true)]
    [InlineData("/build", "src/build", true, false)]
    [InlineData("Software/out", "Software/out", true, true)]
    [InlineData("Software/out", "other/Software/out", true, false)]
    // Globs.
    [InlineData("*.log", "trace.log", false, true)]
    [InlineData("*.log", "logs/deep/trace.log", false, true)]
    [InlineData("*.log", "trace.log.txt", false, false)]
    [InlineData("temp?", "temp1", true, true)]
    [InlineData("temp?", "temp12", true, false)]
    [InlineData("**/generated", "a/b/generated", true, true)]
    [InlineData("doc/**", "doc/a/b/c.txt", false, true)]
    [InlineData("a/**/b", "a/b", true, true)]
    [InlineData("a/**/b", "a/x/y/b", true, true)]
    // Comments and blanks are inert.
    [InlineData("# Debug", "Debug", true, false)]
    // Unsupported character classes drop the whole pattern — indexing too much, never
    // silently losing source.
    [InlineData("[Dd]ebug", "Debug", true, false)]
    public void MatchesTheSupportedSubset(string pattern, string path, bool isDirectory, bool expected)
    {
        MakeRepo(pattern + "\n");

        var rules = GitIgnoreRules.TryDiscover(_root);
        Assert.NotNull(rules);

        var actual = isDirectory
            ? rules!.IsDirectoryIgnored(Full(path))
            : rules!.IsFileIgnored(Full(path));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NegationIsLastMatchWins()
    {
        MakeRepo("""
            *.log
            !keep.log
            """);

        var rules = GitIgnoreRules.TryDiscover(_root)!;

        Assert.True(rules.IsFileIgnored(Full("trace.log")));
        Assert.False(rules.IsFileIgnored(Full("keep.log")));

        // The reverse order re-ignores it: later lines win.
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "!keep.log\n*.log\n");
        var reversed = GitIgnoreRules.TryDiscover(_root)!;
        Assert.True(reversed.IsFileIgnored(Full("keep.log")));
    }

    [Fact]
    public void ANestedGitIgnoreScopesToItsOwnDirectoryAndOutranksTheRoot()
    {
        MakeRepo("*.tmp\n");
        MakeRepo("!special.tmp\n", "Software");

        var rules = GitIgnoreRules.TryDiscover(_root)!;

        // The nested negation only applies under its own directory.
        Assert.False(rules.IsFileIgnored(Full("Software/special.tmp")));
        Assert.True(rules.IsFileIgnored(Full("Firmware/special.tmp")));
        Assert.True(rules.IsFileIgnored(Full("Software/other.tmp")));
    }

    [Fact]
    public void AncestorAwareCheckCatchesAFileInsideAnIgnoredDirectory()
    {
        MakeRepo("Debug/\n");

        var rules = GitIgnoreRules.TryDiscover(_root)!;

        // The file itself matches no rule — its parent is what the rules name. The
        // crawler never needs this (it prunes), but a watcher event lands directly here.
        Assert.False(rules.IsFileIgnored(Full("Firmware/Debug/main.o.c")));
        Assert.True(rules.IsPathIgnoredIncludingAncestors(Full("Firmware/Debug/main.o.c"), isDirectory: false));
        Assert.False(rules.IsPathIgnoredIncludingAncestors(Full("Firmware/Src/main.c"), isDirectory: false));
    }

    // ---- Discovery ---------------------------------------------------------

    [Fact]
    public void DiscoversTheRepositoryFromAWorkspaceOpenedBelowItsRoot()
    {
        // The reported shape: the git root two levels above the folder the user opened.
        MakeRepo("Environment/\n");
        var workspace = Full("Software/Power_Supply_Control");
        Directory.CreateDirectory(workspace);

        var rules = GitIgnoreRules.TryDiscover(workspace);

        Assert.NotNull(rules);
        Assert.Equal(_root, rules!.GitRootPath);
        Assert.True(rules.HasAnyRules);
        Assert.True(rules.IsDirectoryIgnored(Full("Software/Power_Supply_Control/Environment")));
    }

    [Fact]
    public void NoRepositoryMeansNoRules()
    {
        var plain = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(plain);

        // The temp tree has no .git anywhere above it either — but guard the assumption
        // by checking from a directory we know is repo-free only when that holds.
        var rules = GitIgnoreRules.TryDiscover(plain);
        if (rules is not null)
        {
            // Some machines keep a repository above %TEMP%; the discovery finding it is
            // then correct behaviour, not a failure of this test.
            Assert.NotEqual(plain, rules.GitRootPath);
            return;
        }

        Assert.Null(rules);
    }

    [Fact]
    public void ARepositoryWithoutAnyGitIgnoreReportsNothingToAsk()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        var rules = GitIgnoreRules.TryDiscover(_root);

        Assert.NotNull(rules);

        // HasAnyRules gates the ask-once prompt: no rules, nothing to ask about.
        Assert.False(rules!.HasAnyRules);
        Assert.False(rules.IsFileIgnored(Full("anything.c")));
    }

    [Fact]
    public void AGitFileMarksAWorktreeRoot()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: ../somewhere/.git/worktrees/x");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "obj-cache/\n");

        var rules = GitIgnoreRules.TryDiscover(_root);

        Assert.NotNull(rules);
        Assert.Equal(_root, rules!.GitRootPath);
        Assert.True(rules.IsDirectoryIgnored(Full("obj-cache")));
    }
}
