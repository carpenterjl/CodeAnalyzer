using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Storage;
using CodeAnalyzer.Core.Workspaces;
using CodeAnalyzer.Parsing;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

/// <summary>
/// The provenance line: how far the index has drifted, and how much of it the parser could
/// not fully read. These tests edit and delete files, so they get their own workspace rather
/// than the shared fixture every other class in the collection reads from.
/// </summary>
public sealed class IndexStalenessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-stale-" + Guid.NewGuid().ToString("N")[..8]);

    public IndexStalenessTests()
    {
        Directory.CreateDirectory(_root);

        Write("a.c", "int a(void) { return 1; }");
        Write("b.c", "int b(void) { return 2; }");

        using var session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
        session.IndexAsync([]).GetAwaiter().GetResult();
    }

    [Fact]
    public void AFreshIndexHasNotDrifted()
    {
        using var session = Open();

        var drift = IndexStalenessProbe.Compare(session.Connection, _root);

        Assert.False(drift.IsStale);
        Assert.Equal(2, drift.Examined);
        Assert.True(drift.Complete);
    }

    [Fact]
    public void AnEditedFileIsCounted()
    {
        // Longer content, so the screen catches it on size alone and the test does not
        // depend on the filesystem's timestamp resolution.
        Write("a.c", "int a(void) { return 11; }");

        using var session = Open();
        var drift = IndexStalenessProbe.Compare(session.Connection, _root);

        Assert.Equal(1, drift.Changed);
        Assert.Equal(0, drift.Removed);
    }

    [Fact]
    public void ADeletedFileIsCounted()
    {
        File.Delete(Path.Combine(_root, "b.c"));

        using var session = Open();
        var drift = IndexStalenessProbe.Compare(session.Connection, _root);

        Assert.Equal(1, drift.Removed);
        Assert.Equal(0, drift.Changed);
    }

    [Fact]
    public void TheProvenanceLineNamesTheDriftAndHowToFixIt()
    {
        Write("a.c", "int a(void) { return 11; }");

        using var session = Open();
        var line = session.DescribeIndex();

        Assert.Contains("1 of 2 indexed files changed on disk", line);
        Assert.Contains("run 'codeanalyzer index' to refresh", line);
    }

    [Fact]
    public void TheProvenanceLineSaysWhenNothingHasMoved()
    {
        using var session = Open();
        var line = session.DescribeIndex();

        // The word "indexed" is the scope of the claim: the probe never goes looking for
        // files the index has not seen, so this must not read as "the workspace is current".
        Assert.Contains("2 indexed files unchanged on disk", line);
    }

    [Fact]
    public void NothingHavingMovedIsNotAReasonToReIndex()
    {
        using var session = Open();

        // M28.1. The advice used to print on every header, so the first read after a
        // successful index said "unchanged on disk — run 'codeanalyzer index' to refresh":
        // the evidence that the index is current, and an instruction to rebuild it, in one
        // sentence. Advice nobody can act on is what teaches a reader to skip the line that
        // carries the drift count.
        Assert.DoesNotContain("to refresh", session.DescribeIndex());
    }

    [Fact]
    public void AWorkspaceTheParserReadWholeSaysNothingAboutImperfectParses()
    {
        using var session = Open();

        // Absence is the point: a clause that prints "0 imperfect parses" on every answer
        // is the same noise as advice nobody can act on.
        Assert.DoesNotContain("imperfect", session.DescribeIndex());
    }

    [Fact]
    public void TheHeaderCarriesTheImperfectParseCountEveryAnswerIsDrawnFrom()
    {
        // M28.4. `stats` and `errors` both reported this number; `get_callers`, whose whole
        // answer is a count, did not — so a reader could hold "136 files truncated" and
        // "22 callers" in the same session with nothing joining them. Every command prints
        // this header, so every count now arrives beside the reason it might be short.
        Write("c.cs", "class A\n{\n    void M() { int x = 1 }\n}\n");
        ReIndex();

        var line = Open().DescribeIndex();

        Assert.Contains("1 imperfect parses (see: errors)", line);
    }

    private void ReIndex()
    {
        using var session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
        session.IndexAsync([]).GetAwaiter().GetResult();
    }

    private ReadOnlyIndexSession Open()
    {
        var opened = ReadOnlyIndexSession.TryOpen(_root);
        return opened.Session
            ?? throw new InvalidOperationException($"index did not reopen: {opened.Problem}");
    }

    private void Write(string relativePath, string content) =>
        File.WriteAllText(Path.Combine(_root, relativePath), content);

    public void Dispose()
    {
        foreach (var directory in new[] { WorkspacePaths.GetWorkspaceDirectory(_root), _root })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e)
                when (e is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                // Temp cleanup failures are not test failures.
            }
        }
    }
}
