using CodeAnalyzer.Core.Indexing;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// The density comparison and what it says. The threshold is a judgement call, so these
/// pin both sides of it — including the run that earned the probe, and the ordinary growth
/// that must stay silent or the warning gets trained away.
/// </summary>
public class IndexCostProbeTests
{
    private static IndexCostReport Report(int filesBefore, long linksBefore, int filesAfter, long linksAfter) =>
        new(new IndexDensity(filesAfter, linksAfter), new IndexDensity(filesBefore, linksBefore), []);

    [Fact]
    public void TheRunThatEarnedThisProbeWouldHaveBeenReported()
    {
        // Round two, the first index after the JavaScript pack landed: three vendored
        // minified bundles took this repo from 11,236 links to 306,922 with the file
        // count barely moving. It passed in complete silence.
        var report = Report(filesBefore: 174, linksBefore: 11_236, filesAfter: 177, linksAfter: 306_922);

        Assert.True(report.IsSurprising);
    }

    [Fact]
    public void AWorkspaceThatHonestlyGrewSaysNothing()
    {
        // Twice the files, twice the links: same shape, nothing to report. This is the
        // case that decides whether the warning is worth having — one false alarm on an
        // ordinary afternoon and it stops being read.
        var report = Report(filesBefore: 100, linksBefore: 5_000, filesAfter: 200, linksAfter: 10_000);

        Assert.False(report.IsSurprising);
    }

    [Fact]
    public void GrowthUnderTheFactorStaysQuiet()
    {
        var report = Report(filesBefore: 100, linksBefore: 5_000, filesAfter: 100, linksAfter: 14_000);

        Assert.False(report.IsSurprising);
    }

    [Fact]
    public void GrowthAtTheFactorIsReported()
    {
        var report = Report(filesBefore: 100, linksBefore: 5_000, filesAfter: 100, linksAfter: 15_000);

        Assert.True(report.IsSurprising);
    }

    [Fact]
    public void AFirstRunHasNothingToCompareAgainstAndSaysSo()
    {
        // A cache written before this probe existed, or a brand new workspace: no
        // baseline, no claim. It records one for next time instead.
        var report = new IndexCostReport(new IndexDensity(177, 306_922), null, []);

        Assert.False(report.IsSurprising);
        Assert.Null(IndexCostProbe.Describe(report));
    }

    [Fact]
    public void AnEmptyPreviousIndexIsNotABaseline()
    {
        // Zero links over zero files is not a density, and treating it as one would make
        // the first real run of every workspace shout.
        var report = Report(filesBefore: 0, linksBefore: 0, filesAfter: 177, linksAfter: 306_922);

        Assert.False(report.IsSurprising);
    }

    [Fact]
    public void TheNoteNamesTheNumbersAndTheFilesAndDecidesNothing()
    {
        var report = Report(174, 11_236, 177, 306_922) with
        {
            Heaviest =
            [
                new HeaviestFile("wwwroot/lib/cytoscape.min.js", 180_000),
                new HeaviestFile("wwwroot/lib/d3.min.js", 90_000),
            ],
        };

        var note = IndexCostProbe.Describe(report);

        Assert.NotNull(note);
        Assert.Contains("1,734 links per file", note);
        Assert.Contains("up from 65", note);
        Assert.Contains("cytoscape.min.js", note);

        // It states what a jump like this usually means, and leaves the verdict alone:
        // the files it names may be exactly what the user wants indexed.
        Assert.Contains("If those files belong, nothing is wrong", note);
    }

    [Fact]
    public void TheShortFormFitsAStatusBarAndStillNamesTheCause()
    {
        var report = Report(174, 11_236, 177, 306_922) with
        {
            Heaviest = [new HeaviestFile("wwwroot/lib/cytoscape.min.js", 180_000)],
        };

        var line = IndexCostProbe.DescribeShort(report);

        Assert.NotNull(line);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("cytoscape.min.js", line);
        Assert.DoesNotContain("wwwroot/lib", line);
    }
}
