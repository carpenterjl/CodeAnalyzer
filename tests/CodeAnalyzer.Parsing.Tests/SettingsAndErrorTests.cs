using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Storage;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The two M8 additions that live below the UI: per-workspace crawl settings, and the
/// error list read back from <c>file.status</c>. Both run against a real temp workspace
/// and a real database, like the rest of the store tests.
/// </summary>
public class SettingsAndErrorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "codeanalyzer-settings", Guid.NewGuid().ToString("N"));

    private readonly string _databasePath;
    private SqliteIndexStore? _store;

    public SettingsAndErrorTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, ".index", "index.db");
    }

    public void Dispose()
    {
        _store?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<SqliteIndexStore> IndexAsync(WorkspaceSettings? settings = null)
    {
        _store ??= SqliteIndexStore.Open(_databasePath, _root);
        _store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(factory.IsSupportedExtension, settings);
        var orchestrator = new IndexOrchestrator(crawler, factory);

        await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), _store);
        return _store;
    }

    // ---- Settings drive the crawl ------------------------------------------

    [Fact]
    public void TheCrawlerSkipsExtraIgnoredDirectories()
    {
        WriteFile("src/hand.c", "int hand(void) { return 0; }");
        WriteFile("generated/machine.c", "int machine(void) { return 0; }");

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(
            factory.IsSupportedExtension,
            new WorkspaceSettings { ExtraIgnoredDirectories = ["generated"] });

        var found = crawler
            .Crawl(WorkspaceSelection.EntireWorkspace(_root), CancellationToken.None)
            .Select(item => item.RelativePath)
            .ToList();

        Assert.Contains("src/hand.c", found);
        Assert.DoesNotContain("generated/machine.c", found);
    }

    [Fact]
    public void TheCrawlerHonoursTheSizeCap()
    {
        WriteFile("small.c", "int s(void) { return 0; }");
        WriteFile("big.c", "/* " + new string('x', 4096) + " */ int b(void) { return 0; }");

        var factory = new TreeSitterAnalyzerFactory();
        var crawler = new FileCrawler(
            factory.IsSupportedExtension,
            new WorkspaceSettings { MaxFileSizeBytes = 1024 });

        var found = crawler
            .Crawl(WorkspaceSelection.EntireWorkspace(_root), CancellationToken.None)
            .Select(item => item.RelativePath)
            .ToList();

        Assert.Equal(["small.c"], found);
    }

    // ---- Settings persistence ----------------------------------------------

    [Fact]
    public void SettingsRoundTripThroughTheStore()
    {
        var settings = new WorkspaceSettings
        {
            ExtraIgnoredDirectories = ["generated", "ThirdParty"],
            MaxFileSizeBytes = 2 * 1024 * 1024,
        };

        using (var store = SqliteIndexStore.Open(_databasePath, _root))
        {
            store.SaveSettings(settings);
        }

        // A fresh store over the same database, as reopening the workspace would create.
        _store = SqliteIndexStore.Open(_databasePath, _root);
        var loaded = _store.LoadSettings();

        Assert.Equal(settings.ExtraIgnoredDirectories, loaded.ExtraIgnoredDirectories);
        Assert.Equal(settings.MaxFileSizeBytes, loaded.MaxFileSizeBytes);
    }

    [Fact]
    public void CorruptStoredSettingsFallBackToDefaults()
    {
        _store = SqliteIndexStore.Open(_databasePath, _root);
        Schema.WriteMeta(_store.Connection, "settings_json", "{ this is not json");

        var loaded = _store.LoadSettings();

        Assert.Empty(loaded.ExtraIgnoredDirectories);
        Assert.Equal(IgnoreRules.DefaultMaxFileSizeBytes, loaded.MaxFileSizeBytes);
    }

    // ---- Error list ----------------------------------------------------------

    [Fact]
    public async Task ASyntaxErrorAppearsInTheErrorListAndClearsWhenFixed()
    {
        WriteFile("good.c", "int fine(void) { return 0; }");

        // The error sits inside a body: the analyzer tolerates that (M5), so the
        // declarations survive while the file is still honestly marked imperfect.
        WriteFile("broken.c", "int broken(void) { int x = ; return 0; }\nint survivor(void) { return 1; }");

        var store = await IndexAsync();

        var errors = store.ReadFileErrors();
        var entry = Assert.Single(errors);
        Assert.Equal("broken.c", entry.RelativePath);

        // A routine syntax error has no message — tree-sitter recovered — and the
        // surviving partial symbols are counted rather than implied away.
        Assert.Null(entry.Message);
        Assert.True(entry.SymbolCount > 0, "partial symbols should still be indexed");

        // Fixing the file clears its entry on the next pass.
        WriteFile("broken.c", "int broken(void) { int x = 1; return x; }\nint survivor(void) { return 1; }");
        await IndexAsync();

        Assert.Empty(store.ReadFileErrors());
    }
}
