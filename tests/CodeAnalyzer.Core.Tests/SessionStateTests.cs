using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// Every test writes to its own temp file. The real store has one fixed location under the
/// user's profile, and a test that wrote there would silently destroy the developer's own
/// saved session.
/// </summary>
public class SessionStateTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"codeanalyzer-session-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }
    }

    [Fact]
    public void ARoundTripKeepsEveryField()
    {
        var saved = new SessionState
        {
            WorkspaceRoot = @"C:\work\project",
            DarkTheme = false,
            ViewMode = "Treemap",
            TreemapPath = "src/core",
            LiveUpdates = false,
            FocusedRelativePath = "src/core/main.c",
            FocusedSymbolName = "main",
            FocusedLine = 42,
            LegendFontSize = 15,
            GraphNodeDetails = false,
        };

        SessionStateStore.Save(saved, _path);

        Assert.Equal(saved, SessionStateStore.Load(_path));
    }

    [Fact]
    public void AMissingFileLoadsAsDefaults()
    {
        var state = SessionStateStore.Load(_path);

        Assert.Null(state.WorkspaceRoot);
        Assert.True(state.DarkTheme);
        Assert.Equal("Graph", state.ViewMode);
        Assert.True(state.LiveUpdates);
        Assert.Equal(LegendFontSizes.Default, state.LegendFontSize);

        // On by default: the parameter and descriptor lines are what tell two overloads
        // apart, so a first run should show them without being asked.
        Assert.True(state.GraphNodeDetails);
    }

    [Theory]
    [InlineData(0, LegendFontSizes.Minimum)]
    [InlineData(1000, LegendFontSizes.Maximum)]
    [InlineData(-4, LegendFontSizes.Minimum)]
    [InlineData(14, 14)]
    public void TheLegendSizeIsClampedToSomethingReadable(double stored, double expected) =>
        Assert.Equal(expected, LegendFontSizes.Clamp(stored));

    [Fact]
    public void ANonFiniteLegendSizeFallsBackToTheDefault()
    {
        // JSON has no NaN literal, but the value also arrives from the page, where any
        // arithmetic slip produces one.
        Assert.Equal(LegendFontSizes.Default, LegendFontSizes.Clamp(double.NaN));
        Assert.Equal(LegendFontSizes.Default, LegendFontSizes.Clamp(double.PositiveInfinity));
    }

    [Fact]
    public void ACorruptFileLoadsAsDefaultsRatherThanThrowing()
    {
        // A session file is a convenience. No state of it is worth refusing to start over.
        File.WriteAllText(_path, "{ this is not json");

        var state = SessionStateStore.Load(_path);

        Assert.Null(state.WorkspaceRoot);
        Assert.Equal("Graph", state.ViewMode);
    }

    [Fact]
    public void AnUnknownViewModeSurvivesTheRoundTripAndIsRejectedByTheCaller()
    {
        // Stored by name, so a build that drops a view reads back something it cannot parse
        // rather than a valid-but-wrong enum value.
        SessionStateStore.Save(new SessionState { ViewMode = "Constellation" }, _path);

        var state = SessionStateStore.Load(_path);

        Assert.Equal("Constellation", state.ViewMode);
        Assert.False(Enum.TryParse<TestViewMode>(state.ViewMode, ignoreCase: true, out _));
    }

    private enum TestViewMode
    {
        Graph,
        Composition,
        Paths,
        Treemap,
        Wheel,
    }
}
