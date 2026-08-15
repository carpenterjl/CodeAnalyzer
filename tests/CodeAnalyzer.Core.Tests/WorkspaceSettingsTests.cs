using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
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

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void HonorGitIgnoreSurvivesTheJsonRoundTrip(bool? honor)
    {
        // Three states on purpose: null is "never asked", and it must come back as null —
        // collapsing it to false would silence the ask-once prompt forever.
        var settings = new WorkspaceSettings { HonorGitIgnore = honor };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceSettings>(json);

        Assert.Equal(honor, restored!.HonorGitIgnore);
    }

    [Fact]
    public void AnOldSettingsBlobDeserializesToNeverAsked()
    {
        // Settings saved before this field existed must map to "ask once", not to an
        // answer the user never gave.
        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceSettings>(
            """{"ExtraIgnoredDirectories":["gen"],"MaxFileSizeBytes":5242880}""");

        Assert.Null(restored!.HonorGitIgnore);
    }

    [Fact]
    public void IoMarksSurviveTheJsonRoundTrip()
    {
        var settings = new WorkspaceSettings
        {
            IoMarks =
            [
                new IoMark { Name = "frame_send", Direction = IoDirection.Output },
                new IoMark { Name = "write", Direction = IoDirection.None, Scope = "tools" },
            ],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceSettings>(json);

        Assert.Equal(2, restored!.IoMarks.Count);
        Assert.Equal("frame_send", restored.IoMarks[0].Name);
        Assert.Equal(IoDirection.Output, restored.IoMarks[0].Direction);
        Assert.Null(restored.IoMarks[0].Scope);
        Assert.Equal(IoDirection.None, restored.IoMarks[1].Direction);
        Assert.Equal("tools", restored.IoMarks[1].Scope);
    }

    [Fact]
    public void AnOldSettingsBlobDeserializesToNoMarks()
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceSettings>(
            """{"ExtraIgnoredDirectories":[],"MaxFileSizeBytes":5242880}""");

        Assert.Empty(restored!.IoMarks);
    }
}
