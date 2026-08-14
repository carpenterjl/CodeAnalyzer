using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Search restricted to kind families, out of a real index. The families come from
/// <see cref="SymbolKindGroups"/>, which is the same vocabulary the graph colours by.
/// </summary>
public class SearchKindFilterTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-filter", Guid.NewGuid().ToString("N"));

    private WorkspaceSession _session = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        // Every name shares the "sig" hump so one query reaches all of them and the only
        // thing separating the results is the filter.
        WriteFile("src/Signal.cs", """
            namespace Radio
            {
                public interface ISignalSink
                {
                    void Accept(int level);
                }

                public class SignalReader : ISignalSink
                {
                    public const int SignalFloor = -120;

                    public int SignalLevel;

                    public void Accept(int level) { }
                }
            }
            """);

        _session = WorkspaceSession.Open(_root, new TreeSitterAnalyzerFactory());
        await _session.IndexAsync([string.Empty]);
    }

    public Task DisposeAsync()
    {
        _session.Dispose();
        WorkspaceCacheCleanup.Delete(_root);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }

        return Task.CompletedTask;
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private List<SymbolSearchHit> Search(params string[] groups) =>
        _session.Search.Search(
            "signal",
            groups.Length == 0
                ? null
                : new SymbolSearchOptions { Kinds = SymbolKindGroups.KindsIn(groups) });

    [Fact]
    public void NoFilterListsEveryFamily()
    {
        var kinds = Search().Select(h => h.Kind).ToHashSet();

        Assert.Contains(SymbolKind.Interface, kinds);
        Assert.Contains(SymbolKind.Class, kinds);
        Assert.Contains(SymbolKind.Constant, kinds);
    }

    [Fact]
    public void AFilterKeepsOnlyTheChosenFamily()
    {
        var constants = Search("constant");

        Assert.NotEmpty(constants);
        Assert.All(constants, hit => Assert.Equal("constant", SymbolKindGroups.For(hit.Kind)));
        Assert.Contains(constants, hit => hit.Name == "SignalFloor");
    }

    [Fact]
    public void FilteringOnTypeLeavesInterfacesOut()
    {
        // An interface draws with its own colour and shape, so it filters as its own
        // family too — picking "type" must not quietly bring it back.
        Assert.DoesNotContain(Search("type"), hit => hit.Name == "ISignalSink");
        Assert.Contains(Search("type"), hit => hit.Name == "SignalReader");
        Assert.Contains(Search("interface"), hit => hit.Name == "ISignalSink");
    }

    [Fact]
    public void SeveralFamiliesAreAUnion()
    {
        var names = Search("interface", "constant").Select(h => h.Name).ToList();

        Assert.Contains("ISignalSink", names);
        Assert.Contains("SignalFloor", names);
        Assert.DoesNotContain("SignalReader", names);
    }

    [Fact]
    public void SelectingEveryFamilyMatchesNoFilterAtAll()
    {
        // What makes "nothing selected means no filter" safe to state in the UI.
        Assert.Equal(
            Search().Select(h => h.SymbolId).ToList(),
            Search([.. SymbolKindGroups.All]).Select(h => h.SymbolId).ToList());
    }
}
