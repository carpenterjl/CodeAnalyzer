using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The bar, applied through a real index rather than to the scorer alone: a query that
/// names something here comes back as an answer, and a query that names nothing comes
/// back marked as the coincidence it is.
/// </summary>
public class SearchQualityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-quality", Guid.NewGuid().ToString("N"));

    private WorkspaceSession _session = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        // A long test-style name is what makes the coincidence possible in the first
        // place: the more letters a name has, the more queries fit inside it in order.
        var full = Path.Combine(_root, "Workspace.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, """
            namespace Probe
            {
                public class WorkspaceSettingsTests
                {
                    public void ABindingInsideATypedTemplateCarriesTheTemplatesTypeAsItsReceiver() { }
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

    private List<SymbolSearchHit> Search(string query, bool exact = false) =>
        _session.Search.Search(query, new SymbolSearchOptions
        {
            Match = exact ? SymbolMatchMode.Substring : SymbolMatchMode.Fuzzy,
        });

    [Fact]
    public void AQueryNamingNothingHereComesBackLoose()
    {
        var hits = Search("McpServer");

        // It still comes back — hiding the only thing found is the same sin the other way
        // round — but nothing in the list is offered as an answer.
        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.True(hit.LooseMatch));
    }

    [Fact]
    public void InitialsNamingTheHumpsOfARealNameAreAnAnswer()
    {
        var hits = Search("WST");

        var match = Assert.Single(hits, hit => hit.Name == "WorkspaceSettingsTests");
        Assert.False(match.LooseMatch);
    }

    [Fact]
    public void AVerbatimMatchIsNeverCalledLoose()
    {
        // Containing the query is the whole question exact matching asks, so its answers
        // have no weaker tier to fall into.
        var hits = Search("Binding", exact: true);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.False(hit.LooseMatch));
    }
}
