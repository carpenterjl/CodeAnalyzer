using System.Diagnostics;
using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Indexing;
using CodeAnalyzer.Core.Resolution;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;
using CodeAnalyzer.Parsing;

// Measures indexing throughput against a real or generated workspace.
//
//   CodeAnalyzer.Bench <workspace-path>     index an existing tree
//   CodeAnalyzer.Bench --generate <n>       generate and index n synthetic C files

var generateCount = 0;
var useDatabase = false;
string? workspacePath = null;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--generate" or "-g")
    {
        generateCount = i + 1 < args.Length && int.TryParse(args[i + 1], out var n) ? n : 5000;
        i++;
    }
    else if (args[i] is "--db")
    {
        useDatabase = true;
    }
    else
    {
        workspacePath = args[i];
    }
}

if (generateCount > 0)
{
    workspacePath = Path.Combine(Path.GetTempPath(), "codeanalyzer-bench");
    GenerateWorkspace(workspacePath, generateCount);
}

if (workspacePath is null || !Directory.Exists(workspacePath))
{
    Console.Error.WriteLine("Usage: CodeAnalyzer.Bench <workspace-path> | --generate <fileCount>");
    return 1;
}

Console.WriteLine($"Workspace : {workspacePath}");
Console.WriteLine($"Workers   : {Environment.ProcessorCount}");
Console.WriteLine();

var factory = new TreeSitterAnalyzerFactory();
var crawler = new FileCrawler(factory.IsSupportedExtension);
var orchestrator = new IndexOrchestrator(crawler, factory);

SqliteIndexStore? store = null;
var databasePath = string.Empty;
IParseResultSink sink;
var countingSink = new CountingSink();

if (useDatabase)
{
    // Kept out of the workspace: pointing the bench at a real checkout should not leave
    // a database behind in it.
    databasePath = Path.Combine(
        Path.GetTempPath(),
        "codeanalyzer-bench-db",
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(workspacePath))),
        "index.db");

    if (File.Exists(databasePath))
    {
        File.Delete(databasePath);
    }

    store = SqliteIndexStore.Open(databasePath, workspacePath);
    store.BeginRun();
    sink = store;
    Console.WriteLine($"Database  : {databasePath}");
}
else
{
    sink = countingSink;
}

var lastLineLength = 0;
var progress = new Progress<IndexProgress>(p =>
{
    var line = $"  [{p.Phase}] {p.FilesParsed + p.FilesUnchanged}/{p.FilesDiscovered} files, {p.SymbolsFound} symbols";
    Console.Write("\r" + line.PadRight(Math.Max(lastLineLength, line.Length)));
    lastLineLength = line.Length;
});

var before = GC.GetTotalMemory(forceFullCollection: true);
var stopwatch = Stopwatch.StartNew();

var outcome = await orchestrator.IndexAsync(
    WorkspaceSelection.EntireWorkspace(workspacePath),
    sink,
    progress: progress);

stopwatch.Stop();
var after = GC.GetTotalMemory(forceFullCollection: false);

Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"Elapsed          : {stopwatch.Elapsed.TotalSeconds:F2} s");
Console.WriteLine($"Files discovered : {outcome.FilesDiscovered:N0}");
Console.WriteLine($"Files parsed     : {outcome.FilesParsed:N0}");
Console.WriteLine($"Syntax errors    : {outcome.FilesWithSyntaxErrors:N0}");
Console.WriteLine($"Unreadable       : {outcome.FilesFailed:N0}");
Console.WriteLine($"Symbols          : {outcome.SymbolsFound:N0}");

if (!useDatabase)
{
    Console.WriteLine($"References       : {countingSink.ReferenceCount:N0}");
}

if (stopwatch.Elapsed.TotalSeconds > 0)
{
    Console.WriteLine($"Throughput       : {outcome.FilesParsed / stopwatch.Elapsed.TotalSeconds:N0} files/s");
}

// Managed growth during the run. The pipeline is bounded, so this should stay flat
// relative to workspace size rather than scaling with it.
Console.WriteLine($"Managed delta    : {(after - before) / (1024.0 * 1024.0):F1} MB");

if (store is not null)
{
    Console.WriteLine();

    var resolveWatch = Stopwatch.StartNew();
    var resolver = new ReferenceResolver(store.Connection)
    {
        StepTimingCallback = (label, elapsed) =>
            Console.WriteLine($"  resolve/{label}".PadRight(30) + $": {elapsed.TotalSeconds,8:F2} s"),
    };
    var edges = resolver.ResolveAll();
    resolveWatch.Stop();
    Console.WriteLine($"Resolve          : {resolveWatch.Elapsed.TotalSeconds:F2} s ({edges:N0} edges)");

    var search = new SymbolSearchService(store.Connection);
    var reloadWatch = Stopwatch.StartNew();
    search.Reload();
    reloadWatch.Stop();
    Console.WriteLine($"Search index     : {reloadWatch.Elapsed.TotalSeconds:F2} s ({search.IndexedSymbolCount:N0} searchable)");

    // Search latency is the number the user actually feels, so measure it per keystroke.
    foreach (var query in new[] { "u", "fn", "hlp", "config", "fn_12345" })
    {
        var queryWatch = Stopwatch.StartNew();
        var hits = search.Search(query);
        queryWatch.Stop();
        Console.WriteLine($"  search \"{query}\"".PadRight(24) + $": {queryWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({hits.Count} hits)");
    }

    var graph = new GraphQueryService(store.Connection);
    var sample = search.Search("fn_5000").FirstOrDefault();
    if (sample is not null)
    {
        var graphWatch = Stopwatch.StartNew();
        var fragment = graph.GetNeighbourhood(sample.SymbolId);
        graphWatch.Stop();
        Console.WriteLine($"  neighbourhood".PadRight(24) + $": {graphWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({fragment.Nodes.Count} nodes, {fragment.Edges.Count} edges)");

        var detailWatch = Stopwatch.StartNew();
        var detail = graph.GetDetail(sample.SymbolId);
        detailWatch.Stop();
        Console.WriteLine($"  detail".PadRight(24) + $": {detailWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({detail?.Callers.Count} callers, {detail?.Callees.Count} callees)");

        var composition = new CompositionQueryService(store.Connection);
        var compositionWatch = Stopwatch.StartNew();
        var view = composition.GetComposition(sample.SymbolId);
        compositionWatch.Stop();
        Console.WriteLine($"  composition".PadRight(24) + $": {compositionWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({view?.Members.Count} members)");

        // Five hops down the generated call chain: far enough that the search has to walk,
        // close enough that it finds something rather than measuring how fast it gives up.
        var target = search.Search("fn_4995").FirstOrDefault();
        if (target is not null)
        {
            var paths = new PathQueryService(store.Connection);
            var pathWatch = Stopwatch.StartNew();
            var trace = paths.FindPaths(sample.SymbolId, target.SymbolId);
            pathWatch.Stop();
            Console.WriteLine($"  path trace".PadRight(24) +
                $": {pathWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({trace.Routes.Count} routes, {trace.Length} hops)");
        }
    }

    // The two workspace-wide views. Neither is bounded by a focus symbol, so these are the
    // queries most likely to be the first thing that gets slow.
    var structure = new StructureQueryService(store.Connection);

    var treemapWatch = Stopwatch.StartNew();
    var level = structure.GetTreemapLevel(string.Empty);
    treemapWatch.Stop();
    Console.WriteLine($"  treemap root".PadRight(24) + $": {treemapWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({level.Tiles.Count} tiles)");

    if (level.Tiles.Count > 0)
    {
        var drillWatch = Stopwatch.StartNew();
        var drilled = structure.GetTreemapLevel(level.Tiles[0].Path);
        drillWatch.Stop();
        Console.WriteLine($"  treemap drill".PadRight(24) + $": {drillWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({drilled.Tiles.Count} tiles)");
    }

    foreach (var wheelSource in new[] { WheelSource.Includes, WheelSource.References })
    {
        var wheelWatch = Stopwatch.StartNew();
        var wheel = structure.GetDependencyWheel(wheelSource);
        wheelWatch.Stop();
        Console.WriteLine($"  wheel {wheelSource}".PadRight(24) +
            $": {wheelWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({wheel.Groups.Count} groups, {wheel.Links.Count} links)");
    }

    // The whole-workspace boundary scan behind the boundaries view. On the synthetic
    // corpus nothing matches, which is exactly the honest cost to know: every catalog
    // entry runs its index seek and finds nothing.
    var boundaries = new IoBoundaryService(store.Connection);
    var ioWatch = Stopwatch.StartNew();
    var ioSites = boundaries.GetAllSites(IoCatalog.BuiltIn.Entries, []);
    ioWatch.Stop();
    Console.WriteLine($"  io boundaries".PadRight(24) +
        $": {ioWatch.Elapsed.TotalMilliseconds,7:F1} ms  ({ioSites.Count} sites)");

    // What a save costs once the watcher has coalesced it. Parsing one file is a fraction of
    // a millisecond at the throughput above; the part that scales with workspace size is the
    // resolve, so that is what is measured — against the full one printed earlier.
    MeasureLiveUpdate(store);

    var databaseFile = new FileInfo(databasePath);
    if (databaseFile.Exists)
    {
        Console.WriteLine($"Database size    : {databaseFile.Length / (1024.0 * 1024.0):F1} MB");
    }

    store.Dispose();
}

return 0;

/// <summary>
/// Times the incremental resolve for one file, the way a live update runs it.
/// <para>
/// The file is picked from the middle of the workspace rather than the first one, which in a
/// generated corpus is unusually well connected and would flatter the result.
/// </para>
/// </summary>
static void MeasureLiveUpdate(SqliteIndexStore store)
{
    long fileId;
    using (var pick = store.Connection.CreateCommand())
    {
        pick.CommandText = "SELECT id FROM file ORDER BY id LIMIT 1 OFFSET (SELECT COUNT(*) / 2 FROM file)";
        if (pick.ExecuteScalar() is not long id)
        {
            return;
        }

        fileId = id;
    }

    var names = store.ReadDefinedNames([fileId]);

    var resolver = new ReferenceResolver(store.Connection)
    {
        StepTimingCallback = (label, elapsed) =>
            Console.WriteLine($"  live/{label}".PadRight(30) + $": {elapsed.TotalMilliseconds,7:F1} ms"),
    };

    var watch = Stopwatch.StartNew();
    var edges = resolver.ResolveIncremental([fileId], names);
    watch.Stop();

    Console.WriteLine($"  live update".PadRight(24) +
        $": {watch.Elapsed.TotalMilliseconds,7:F1} ms  ({edges:N0} edges, {names.Count} names)");
}

static void GenerateWorkspace(string root, int fileCount)
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }

    Console.WriteLine($"Generating {fileCount:N0} synthetic C files…");

    // Spread files across directories so the crawl resembles a real tree.
    const int filesPerDirectory = 50;

    // A shared header under a common include root, reached from every source file through
    // its own module header. Without this two-hop shape the include-reachability tier in
    // the resolver is never exercised and its cost goes unmeasured.
    var includeDirectory = Path.Combine(root, "include");
    Directory.CreateDirectory(includeDirectory);
    File.WriteAllText(Path.Combine(includeDirectory, "hal.h"), """
        #ifndef HAL_H
        #define HAL_H
        #include <stdint.h>

        #define HAL_TIMEOUT 1000

        struct HalContext {
            uint32_t ticks;
            uint8_t  state;
        };

        static inline uint32_t hal_ticks(struct HalContext *ctx) { return ctx->ticks; }
        #endif
        """);

    var moduleCount = (fileCount + filesPerDirectory - 1) / filesPerDirectory;
    for (var m = 0; m < moduleCount; m++)
    {
        var directory = Path.Combine(root, "src", $"module{m:D4}");
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "common.h"), $$"""
            #ifndef COMMON_{{m}}_H
            #define COMMON_{{m}}_H
            #include "include/hal.h"

            #define MODULE_ID_{{m}} {{m}}

            struct ModuleState{{m}} { int active; };
            #endif
            """);
    }

    for (var i = 0; i < fileCount; i++)
    {
        var directory = Path.Combine(root, "src", $"module{i / filesPerDirectory:D4}");
        Directory.CreateDirectory(directory);

        // Each file calls into the previous one, producing a realistic reference graph.
        var previous = i == 0 ? "start" : $"fn_{i - 1}";

        File.WriteAllText(Path.Combine(directory, $"file{i:D5}.c"), $$"""
            #include <stdint.h>
            #include "common.h"

            #define LIMIT_{{i}} {{i * 7 % 512}}
            #define TIMEOUT_{{i}} HAL_TIMEOUT

            struct Config{{i}} {
                int32_t threshold;
                uint8_t flags;
                char name[32];
            };

            enum Mode{{i}} {
                MODE_IDLE_{{i}} = 0,
                MODE_ACTIVE_{{i}} = 1
            };

            static int helper_{{i}}(int value) {
                return value * 2 + LIMIT_{{i}};
            }

            int fn_{{i}}(struct Config{{i}} *config, int input) {
                int scaled = helper_{{i}}(input);
                if (scaled > config->threshold) {
                    return {{previous}}(config, scaled);
                }
                return MODE_IDLE_{{i}};
            }
            """);
    }
}

/// <summary>Counts what the writer would persist, without the cost of a database.</summary>
internal sealed class CountingSink : IParseResultSink
{
    private int _references;

    public int ReferenceCount => _references;

    public Task WriteAsync(ParseResult result, CancellationToken cancellationToken)
    {
        _references += result.References.Count;
        return Task.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
