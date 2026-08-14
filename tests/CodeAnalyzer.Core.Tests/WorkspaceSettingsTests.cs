using CodeAnalyzer.Core.Crawling;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

public class WorkspaceSettingsTests
{
    [Fact]
    public void ExtrasApplyOnTopOfTheBuiltInRules()
    {
        var settings = new WorkspaceSettings { ExtraIgnoredDirectories = ["generated", "ThirdParty"] };

        Assert.True(settings.IsIgnoredDirectoryName("generated"));
        Assert.True(settings.IsIgnoredDirectoryName("GENERATED"));   // names compare case-insensitively
        Assert.True(settings.IsIgnoredDirectoryName("thirdparty"));
        Assert.True(settings.IsIgnoredDirectoryName("node_modules")); // built-ins still hold
        Assert.False(settings.IsIgnoredDirectoryName("src"));
    }

    [Fact]
    public void DefaultsMatchTheBuiltInRulesExactly()
    {
        Assert.True(WorkspaceSettings.Default.IsIgnoredDirectoryName(".git"));
        Assert.False(WorkspaceSettings.Default.IsIgnoredDirectoryName("drivers"));
        Assert.Equal(IgnoreRules.DefaultMaxFileSizeBytes, WorkspaceSettings.Default.MaxFileSizeBytes);
    }

    [Fact]
    public void ParseAcceptsLinesCommasAndSemicolons()
    {
        var names = WorkspaceSettings.ParseDirectoryNames("generated\r\nThirdParty, legacy; temp\n\n");
        Assert.Equal(["generated", "ThirdParty", "legacy", "temp"], names);
    }

    [Fact]
    public void ParseDropsPathsAndDuplicates()
    {
        // Ignore rules match single directory names; an entry with a separator could never
        // match one and is dropped rather than kept broken.
        var names = WorkspaceSettings.ParseDirectoryNames("gen\r\nsrc/gen\r\nsrc\\gen\r\nGEN");
        Assert.Equal(["gen"], names);
    }
}
