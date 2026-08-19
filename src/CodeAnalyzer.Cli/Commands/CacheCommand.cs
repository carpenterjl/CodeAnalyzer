using System.Text;
using System.Text.Json;
using CodeAnalyzer.Cli.Output;
using CodeAnalyzer.Core.Storage;

namespace CodeAnalyzer.Cli.Commands;

/// <summary>
/// <c>codeanalyzer cache</c> — every workspace cache on this machine, judged live or stale
/// by the origin each database records about itself, and <c>--prune</c> to delete the stale
/// ones. This is the round-seven answer to a debt round six could only measure: 692 orphaned
/// directories the tool had written and could not see.
/// </summary>
internal static class CacheCommand
{
    public static CommandSpec Spec { get; } = new(
        "cache",
        "cache [--prune] [--json]",
        "every workspace cache on this machine, live or stale; --prune deletes the stale ones",
        Run);

    private static Task<int> Run(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, [], ["prune", "json"]);

        // `cache` takes no positional, and one handed to it was read by nothing and
        // reported by nothing — the machine-wide listing came back looking like an answer
        // to whatever was typed.
        if (args.Positionals.Count > 0)
        {
            Console.Error.WriteLine("usage: codeanalyzer " + Spec.Usage);
            return Task.FromResult(ExitCodes.Error);
        }

        if (args.Error is not null)
        {
            Console.Error.WriteLine(args.Error);
            return Task.FromResult(ExitCodes.Error);
        }

        var caches = CacheInventory.Read();

        if (args.Switch("prune"))
        {
            var (deleted, bytes, failures) = CacheInventory.Prune(caches);
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"could not delete {failure}");
            }

            Console.WriteLine($"pruned {deleted} stale caches, {Mb(bytes)} reclaimed"
                + (failures.Count == 0 ? string.Empty : $" · {failures.Count} failed"));
            return Task.FromResult(failures.Count == 0 ? ExitCodes.Ok : ExitCodes.Error);
        }

        Console.WriteLine(args.Switch("json") ? Json(caches) : Terse(caches));
        return Task.FromResult(ExitCodes.Ok);
    }

    private static string Terse(IReadOnlyList<CachedWorkspace> caches)
    {
        if (caches.Count == 0)
        {
            return $"no caches under {WorkspacePaths.GetRootDirectory()}";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{caches.Count} caches under {WorkspacePaths.GetRootDirectory()}");

        foreach (var group in caches.GroupBy(c => c.State).OrderBy(g => g.Key))
        {
            builder.AppendLine();
            builder.AppendLine($"{Label(group.Key)} ({group.Count()}, {Mb(group.Sum(c => c.Bytes))}):");
            foreach (var cache in group)
            {
                var origin = cache.RootPath ?? "(origin unknown)";
                var indexed = cache.LastIndexUtc is null ? string.Empty : $"  indexed {cache.LastIndexUtc}";
                builder.AppendLine($"  {origin}  {Mb(cache.Bytes)}{indexed}");
            }
        }

        var stale = caches.Where(c => c.State == CacheState.Stale).ToList();
        if (stale.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"--prune deletes the {stale.Count} stale caches "
                + $"({Mb(stale.Sum(c => c.Bytes))}) — a stale cache's workspace root no longer "
                + "exists, so it can serve nobody. Live and unreadable caches are never touched.");
        }

        return builder.Finish();
    }

    private static string Json(IReadOnlyList<CachedWorkspace> caches) =>
        JsonSerializer.Serialize(new
        {
            cacheRoot = WorkspacePaths.GetRootDirectory(),
            total = caches.Count,
            staleBytes = caches.Where(c => c.State == CacheState.Stale).Sum(c => c.Bytes),
            caches = caches.Select(c => new
            {
                state = Label(c.State),
                directory = c.Directory,
                rootPath = c.RootPath,
                lastIndexUtc = c.LastIndexUtc,
                bytes = c.Bytes,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });

    private static string Label(CacheState state) => state switch
    {
        CacheState.Live => "live",
        CacheState.Stale => "stale",
        _ => "unreadable",
    };

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.0} MB";
}
