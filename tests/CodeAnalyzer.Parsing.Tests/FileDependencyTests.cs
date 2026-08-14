using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The include and import graph, end to end: what each language's dependencies resolve
/// to, and how that changes which definition a name binds to.
/// </summary>
public class FileDependencyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "codeanalyzer-deps", Guid.NewGuid().ToString("N"));

    private SqliteIndexStore? _store;

    public FileDependencyTests() => Directory.CreateDirectory(_root);

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

    private async Task<SqliteIndexStore> IndexAsync()
    {
        _store ??= SqliteIndexStore.Open(Path.Combine(_root, ".index", "index.db"), _root);
        _store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        var orchestrator = new IndexOrchestrator(new FileCrawler(factory.IsSupportedExtension), factory);

        await orchestrator.IndexAsync(WorkspaceSelection.EntireWorkspace(_root), _store);
        new ReferenceResolver(_store.Connection).ResolveAll();

        return _store;
    }

    /// <summary>Every dependency of a file, as (written text, resolved target or null).</summary>
    private static List<(string Written, string? Target)> DependenciesOf(
        SqliteConnection connection, string relativePath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.dep_path, t.rel_path
            FROM file_dep d
            JOIN file f ON f.id = d.file_id
            LEFT JOIN file t ON t.id = d.dep_file_id
            WHERE f.rel_path = $path
            ORDER BY d.dep_path
            """;
        command.Parameters.AddWithValue("$path", relativePath);

        var results = new List<(string, string?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return results;
    }

    [Fact]
    public async Task CIncludesResolveByPathSuffixAndSystemHeadersStayUnresolved()
    {
        WriteFile("app/main.c", """
            #include <stdio.h>
            #include "drivers/uart.h"

            int main(void) { return 0; }
            """);
        WriteFile("drivers/uart.h", "int uart_write(char c);\n");

        var store = await IndexAsync();

        Assert.Equal(
            [("drivers/uart.h", "drivers/uart.h"), ("stdio.h", null)],
            DependenciesOf(store.Connection, "app/main.c"));
    }

    [Fact]
    public async Task ABareIncludeMeansTheOneNextToTheIncludingFile()
    {
        // Every module having its own common.h is ordinary in C, and `#include "common.h"`
        // means this module's. Matching on the filename alone would pick one of them at
        // random and quietly wire the graph to the wrong module.
        WriteFile("mod_a/worker.c", "#include \"common.h\"\n\nint a_work(void) { return 0; }\n");
        WriteFile("mod_a/common.h", "#define MOD_A 1\n");
        WriteFile("mod_b/worker.c", "#include \"common.h\"\n\nint b_work(void) { return 0; }\n");
        WriteFile("mod_b/common.h", "#define MOD_B 1\n");

        var store = await IndexAsync();

        Assert.Equal(
            [("common.h", "mod_a/common.h")],
            DependenciesOf(store.Connection, "mod_a/worker.c"));

        Assert.Equal(
            [("common.h", "mod_b/common.h")],
            DependenciesOf(store.Connection, "mod_b/worker.c"));
    }

    [Fact]
    public async Task AnIncludeThatCouldMeanSeveralFilesIsLeftUnresolved()
    {
        // No config.h next to the including file, and two elsewhere. Nothing in the source
        // says which, so the honest answer is to say nothing.
        WriteFile("app/main.c", "#include \"config.h\"\n\nint main(void) { return 0; }\n");
        WriteFile("mod_a/config.h", "#define A 1\n");
        WriteFile("mod_b/config.h", "#define B 1\n");

        var store = await IndexAsync();

        Assert.Equal([("config.h", null)], DependenciesOf(store.Connection, "app/main.c"));
    }

    [Fact]
    public async Task AnIncludeWithADirectoryStillResolvesFromAnywhereInTheTree()
    {
        // The suffix is what disambiguates here, so this must keep working even though a
        // bare filename would not.
        WriteFile("app/main.c", "#include \"hw/config.h\"\n\nint main(void) { return 0; }\n");
        WriteFile("platform/hw/config.h", "#define HW 1\n");
        WriteFile("mod_b/config.h", "#define B 1\n");

        var store = await IndexAsync();

        Assert.Equal(
            [("hw/config.h", "platform/hw/config.h")],
            DependenciesOf(store.Connection, "app/main.c"));
    }

    [Fact]
    public async Task PythonImportsResolveToModulesAndPackages()
    {
        WriteFile("app/main.py", """
            import pkg.util
            from .helpers import assist
            import nowhere
            """);
        WriteFile("app/helpers.py", "def assist():\n    return 1\n");
        WriteFile("pkg/util/__init__.py", "def load():\n    return 2\n");

        var store = await IndexAsync();

        Assert.Equal(
            [
                (".helpers", "app/helpers.py"),
                ("nowhere", null),

                // `import pkg.util` finds the package's __init__.py when there is no
                // pkg/util.py, which is how most packages are laid out.
                ("pkg.util", "pkg/util/__init__.py"),
            ],
            DependenciesOf(store.Connection, "app/main.py"));
    }

    [Fact]
    public async Task ACSharpUsingIsRecordedButNeverResolvedToAFile()
    {
        WriteFile("src/Device.cs", """
            using System;
            using Hardware.Drivers;

            public class Device { }
            """);

        // A file whose path looks like the namespace, to show that the match is refused
        // on principle rather than merely failing to find anything.
        WriteFile("Hardware/Drivers.cs", "public class Drivers { }\n");

        var store = await IndexAsync();

        Assert.Equal(
            [("Hardware.Drivers", null), ("System", null)],
            DependenciesOf(store.Connection, "src/Device.cs"));
    }

    [Fact]
    public async Task MarkupResourcesResolveWhileAbsoluteUrlsDoNot()
    {
        WriteFile("site/index.html", """
            <html><body>
            <script src="js/app.js"></script>
            <script src="https://cdn.example.com/lib.js"></script>
            </body></html>
            """);
        WriteFile("site/js/app.js", "// not indexed, but it is a file\n");
        WriteFile("site/js/app.html", "<p id=\"stand-in\">so the crawler sees the folder</p>\n");

        var store = await IndexAsync();

        var dependencies = DependenciesOf(store.Connection, "site/index.html");

        // .js is not an indexed language, so the target is not in the file table and the
        // dependency stays unresolved rather than pointing at something else.
        Assert.Contains(("https://cdn.example.com/lib.js", (string?)null), dependencies);
        Assert.Contains(("js/app.js", (string?)null), dependencies);
    }

    [Fact]
    public async Task ADefinitionReachedThroughIncludesBeatsOneThatMerelySharesTheName()
    {
        // Two definitions of send_byte. Nothing but the include graph says which one
        // app/main.c means, and it says so two hops away.
        WriteFile("app/main.c", """
            #include "hw/uart.h"

            int main(void) { return send_byte(1); }
            """);
        WriteFile("hw/uart.h", "#include \"hw/impl.h\"\n");
        WriteFile("hw/impl.h", "static int send_byte(int b) { return b; }\n");
        WriteFile("other/misc.c", "int send_byte(int b) { return -b; }\n");

        var store = await IndexAsync();

        var edges = EdgesFor(store.Connection, "send_byte");

        var edge = Assert.Single(edges);
        Assert.Equal("hw/impl.h", edge.TargetFile);
        Assert.Equal(EdgeConfidence.Unique, edge.Confidence);
    }

    [Fact]
    public async Task ADefinitionFurtherAwayThanTheHopBudgetIsNotTreatedAsIncluded()
    {
        // Same shape, one hop deeper. Neither candidate is reachable within the budget, so
        // both stay in play and the pair is reported ambiguous rather than one being picked.
        WriteFile("app/main.c", """
            #include "hw/uart.h"

            int main(void) { return send_byte(1); }
            """);
        WriteFile("hw/uart.h", "#include \"hw/mid.h\"\n");
        WriteFile("hw/mid.h", "#include \"hw/impl.h\"\n");
        WriteFile("hw/impl.h", "static int send_byte(int b) { return b; }\n");
        WriteFile("other/misc.c", "int send_byte(int b) { return -b; }\n");

        var store = await IndexAsync();

        var edges = EdgesFor(store.Connection, "send_byte");

        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(EdgeConfidence.Ambiguous, e.Confidence));
    }

    [Fact]
    public async Task ReindexingDoesNotLeaveStaleDependencyTargets()
    {
        WriteFile("app/main.c", "#include \"drivers/uart.h\"\n");
        WriteFile("drivers/uart.h", "int uart_write(char c);\n");

        var store = await IndexAsync();
        Assert.Equal(
            [("drivers/uart.h", "drivers/uart.h")],
            DependenciesOf(store.Connection, "app/main.c"));

        // The header is gone, and so must be the claim that the include points at it.
        File.Delete(Path.Combine(_root, "drivers", "uart.h"));
        store.BeginRun();

        var factory = new TreeSitterAnalyzerFactory();
        await new IndexOrchestrator(new FileCrawler(factory.IsSupportedExtension), factory)
            .IndexAsync(WorkspaceSelection.EntireWorkspace(_root), store);

        store.RemoveFilesNotSeenThisRun();
        new ReferenceResolver(store.Connection).ResolveAll();

        Assert.Equal(
            [("drivers/uart.h", null)],
            DependenciesOf(store.Connection, "app/main.c"));
    }

    /// <summary>Every edge produced by references to a name, with the file it lands in.</summary>
    private static List<(string TargetFile, EdgeConfidence Confidence)> EdgesFor(
        SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.rel_path, e.confidence
            FROM ref r
            JOIN edge e ON e.ref_id = r.id
            JOIN symbol s ON s.id = e.target_symbol_id
            JOIN file f ON f.id = s.file_id
            WHERE r.name = $name AND r.kind = $call
            ORDER BY f.rel_path
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$call", (int)ReferenceKind.Call);

        var results = new List<(string, EdgeConfidence)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), (EdgeConfidence)reader.GetInt32(1)));
        }

        return results;
    }
}
