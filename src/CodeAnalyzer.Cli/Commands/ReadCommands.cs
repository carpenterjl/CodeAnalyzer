using CodeAnalyzer.Cli.Output;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;

namespace CodeAnalyzer.Cli.Commands;

/// <summary>
/// The six read-only subcommands. Each is a thin adapter: parse arguments, locate symbols,
/// call one <see cref="AgentToolset"/> method, hand the result to a formatter.
/// </summary>
internal static class ReadCommands
{
    public static CommandSpec Search { get; } = new(
        "search",
        "search <query> [--exact] [--kinds fn,type,…] [--limit N] [--root path] [--json]",
        "fuzzy symbol search over the index; --exact matches the text verbatim instead",
        (args, ct) => RunSearch(args, ct));

    public static CommandSpec Detail { get; } = new(
        "detail",
        "detail <symbol> [--root path] [--json]",
        "one symbol's fact sheet (symbol = name, path:name, or #id)",
        (args, ct) => RunDetail(args, ct));

    public static CommandSpec Report { get; } = new(
        "report",
        "report <symbol> [--root path]",
        "one symbol's facts as a markdown document, ready for a chat, a PR or a note",
        (args, ct) => RunReport(args, ct));

    public static CommandSpec Callers { get; } = new(
        "callers",
        "callers <symbol> [--sites] [--root path] [--json]",
        "who references this symbol; --sites adds each call site's line and arguments",
        (args, ct) => RunRelated(args, callers: true, ct));

    public static CommandSpec Callees { get; } = new(
        "callees",
        "callees <symbol> [--sites] [--root path] [--json]",
        "what this symbol references",
        (args, ct) => RunRelated(args, callers: false, ct));

    public static CommandSpec Trace { get; } = new(
        "trace",
        "trace <from> <to> [--depth N] [--root path] [--json]",
        "all shortest routes between two symbols through resolved references",
        (args, ct) => RunTrace(args, ct));

    public static CommandSpec Boundaries { get; } = new(
        "boundaries",
        "boundaries [--root path] [--json]",
        "where data leaves and enters the workspace (I/O catalog + your marks)",
        (args, ct) => RunBoundaries(args, ct));

    public static CommandSpec Map { get; } = new(
        "map",
        "map [--budget N] [--root path] [--json]",
        "codebase overview: definitions ranked by distinct incoming references",
        (args, ct) => RunMap(args, ct));

    public static CommandSpec Value { get; } = new(
        "value",
        "value <literal> [--limit N] [--root path] [--json]",
        "definitions whose literal denotes this value, in any language (0xA5 finds 165 and 8'hA5)",
        (args, ct) => RunValue(args, ct));

    public static CommandSpec Constants { get; } = new(
        "constants",
        "constants [--by-dir] [--include-trivial] [--root path] [--json]",
        "values defined in more than one language — the agreements no reference connects",
        (args, ct) => RunConstants(args, ct));

    public static CommandSpec Outline { get; } = new(
        "outline",
        "outline <rel_path> [--root path] [--json]",
        "one file's definitions in source order, indented by containment",
        (args, ct) => RunOutline(args, ct));

    public static CommandSpec Errors { get; } = new(
        "errors",
        "errors [--limit N] [--root path] [--json]",
        "files the parser could not fully read, and the construct it stopped at",
        (args, ct) => RunErrors(args, ct));

    public static CommandSpec Stats { get; } = new(
        "stats",
        "stats [path] [--root path] [--json]",
        "aggregate facts about the index: what is in it, and how well it is resolving; "
        + "a path narrows every count to that file or subtree",
        (args, ct) => RunStats(args, ct));

    // ---- runners ------------------------------------------------------------

    private static Task<int> RunSearch(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root", "kinds", "limit"], ["json", "exact"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            if (args.Positionals.Count != 1)
            {
                Console.Error.WriteLine("usage: codeanalyzer " + Search.Usage);
                return Task.FromResult(ExitCodes.Error);
            }

            IReadOnlySet<SymbolKind>? kinds = null;
            var kindFilter = args.Value("kinds");
            if (kindFilter is not null)
            {
                kinds = KindTokens.Parse(kindFilter, out var kindError);
                if (kindError is not null)
                {
                    Console.Error.WriteLine(kindError);
                    return Task.FromResult(ExitCodes.Error);
                }
            }

            var limit = args.IntValue("limit", 20);
            if (args.Error is not null)
            {
                Console.Error.WriteLine(args.Error);
                return Task.FromResult(ExitCodes.Error);
            }

            var query = args.Positionals[0];
            var exact = args.Switch("exact");
            var hits = toolset.Search(query, kinds, limit, exact, cancellationToken);

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.Search(toolset.Session, query, hits)
                : TerseFormatter.Search(query, hits, kindFilter, exact));

            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunDetail(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root"], ["json"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            if (args.Positionals.Count != 1)
            {
                Console.Error.WriteLine("usage: codeanalyzer " + Detail.Usage);
                return Task.FromResult(ExitCodes.Error);
            }

            if (!TryLocate(toolset, args.Positionals[0], args.Switch("json"), cancellationToken, out var focus))
            {
                return Task.FromResult(ExitCodes.Error);
            }

            var detail = toolset.GetDetail(focus.Id, cancellationToken);
            if (detail is null)
            {
                Console.Error.WriteLine($"symbol #{focus.Id} vanished between locating and reading it — re-run search");
                return Task.FromResult(ExitCodes.Error);
            }

            var sameValue = toolset.SameValue(focus.Id, cancellationToken);

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.Detail(toolset.Session, detail, sameValue)
                : TerseFormatter.Detail(detail, sameValue));

            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunReport(string[] rawArgs, CancellationToken cancellationToken)
    {
        // No --json: the report is itself the document, and wrapping markdown in JSON
        // would only make the paste worse.
        var args = ArgReader.Parse(rawArgs, ["root"], []);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            if (args.Positionals.Count != 1)
            {
                Console.Error.WriteLine("usage: codeanalyzer " + Report.Usage);
                return Task.FromResult(ExitCodes.Error);
            }

            if (!TryLocate(toolset, args.Positionals[0], json: false, cancellationToken, out var focus))
            {
                return Task.FromResult(ExitCodes.Error);
            }

            var report = toolset.Report(focus.Id, cancellationToken);
            if (report is null)
            {
                Console.Error.WriteLine($"symbol #{focus.Id} vanished between locating and reading it — re-run search");
                return Task.FromResult(ExitCodes.Error);
            }

            Console.WriteLine(Core.Export.MarkdownFactWriter.Write(report));
            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunValue(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root", "limit"], ["json"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            if (args.Positionals.Count != 1)
            {
                Console.Error.WriteLine("usage: codeanalyzer " + Value.Usage);
                return Task.FromResult(ExitCodes.Error);
            }

            var limit = args.IntValue("limit", 50);
            if (args.Error is not null)
            {
                Console.Error.WriteLine(args.Error);
                return Task.FromResult(ExitCodes.Error);
            }

            var literal = args.Positionals[0];
            var found = toolset.FindByValue(literal, limit, cancellationToken);

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.Values(toolset.Session, literal, found)
                : TerseFormatter.Values(literal, found));

            // Not a literal at all is a usage error, not an empty result: the difference
            // matters to a script, and to an agent deciding whether to try another form.
            return Task.FromResult(found is null ? ExitCodes.Error : ExitCodes.Ok);
        });
    }

    private static Task<int> RunConstants(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root"], ["json", "by-dir", "include-trivial"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            var byDirectory = args.Switch("by-dir");
            var includeTrivial = args.Switch("include-trivial");
            var groups = toolset.SharedValues(byDirectory, includeTrivial, cancellationToken);

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.SharedValues(toolset.Session, groups, byDirectory, includeTrivial)
                : TerseFormatter.SharedValues(groups, byDirectory, includeTrivial));

            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunRelated(string[] rawArgs, bool callers, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root"], ["json", "sites"]);
        var spec = callers ? Callers : Callees;

        return CommandEnvironment.WithSession(args, toolset =>
        {
            if (args.Positionals.Count != 1)
            {
                Console.Error.WriteLine("usage: codeanalyzer " + spec.Usage);
                return Task.FromResult(ExitCodes.Error);
            }

            if (!TryLocate(toolset, args.Positionals[0], args.Switch("json"), cancellationToken, out var focus))
            {
                return Task.FromResult(ExitCodes.Error);
            }

            var detail = toolset.GetDetail(focus.Id, cancellationToken);
            if (detail is null)
            {
                Console.Error.WriteLine($"symbol #{focus.Id} vanished between locating and reading it — re-run search");
                return Task.FromResult(ExitCodes.Error);
            }

            var related = callers ? detail.Callers : detail.Callees;

            Dictionary<long, List<EdgeCallSite>>? sites = null;
            if (args.Switch("sites"))
            {
                sites = [];
                foreach (var entry in related)
                {
                    // The edge is stored caller → callee, so the pair flips with direction.
                    sites[entry.Id] = callers
                        ? toolset.GetCallSites(entry.Id, focus.Id, entry.ReferenceKind, cancellationToken)
                        : toolset.GetCallSites(focus.Id, entry.Id, entry.ReferenceKind, cancellationToken);
                }
            }

            var direction = callers ? TerseFormatter.Callers : TerseFormatter.Callees;
            var listCap = toolset.Session.Graph.RelatedLimit;
            var total = callers ? detail.CallerTotal : detail.CalleeTotal;

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.RelatedList(toolset.Session, focus, related, direction, listCap, sites, total)
                : TerseFormatter.Related(focus, related, direction, listCap, sites, total));

            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunTrace(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root", "depth"], ["json"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            if (args.Positionals.Count != 2)
            {
                Console.Error.WriteLine("usage: codeanalyzer " + Trace.Usage);
                return Task.FromResult(ExitCodes.Error);
            }

            int? depth = null;
            if (args.Value("depth") is { } depthText)
            {
                if (!int.TryParse(depthText, out var parsed) || parsed < 1)
                {
                    Console.Error.WriteLine($"--depth expects a positive integer, got '{depthText}'");
                    return Task.FromResult(ExitCodes.Error);
                }

                depth = parsed;
            }

            var json = args.Switch("json");
            if (!TryLocate(toolset, args.Positionals[0], json, cancellationToken, out var from)
                || !TryLocate(toolset, args.Positionals[1], json, cancellationToken, out var to))
            {
                return Task.FromResult(ExitCodes.Error);
            }

            var trace = toolset.Trace(from.Id, to.Id, depth, cancellationToken);

            Console.WriteLine(json
                ? JsonFormatter.Trace(toolset.Session, from, to, trace)
                : TerseFormatter.Trace(from, to, trace));

            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunBoundaries(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root"], ["json"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            var sites = toolset.Boundaries(cancellationToken);

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.Boundaries(toolset.Session, sites)
                : TerseFormatter.Boundaries(sites));

            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunMap(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root", "budget"], ["json"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            var budget = args.IntValue("budget", 8000);
            if (args.Error is not null)
            {
                Console.Error.WriteLine(args.Error);
                return Task.FromResult(ExitCodes.Error);
            }

            var map = toolset.Map(cancellationToken);

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.RepoMap(toolset.Session, map, map.Entries.Count)
                : TerseFormatter.RepoMap(map, budget));

            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunOutline(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root"], ["json"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            if (args.Positionals.Count != 1)
            {
                Console.Error.WriteLine("usage: codeanalyzer " + Outline.Usage);
                return Task.FromResult(ExitCodes.Error);
            }

            var outline = toolset.Outline(args.Positionals[0]);
            if (outline is null)
            {
                Console.Error.WriteLine($"no indexed file matches '{args.Positionals[0]}'");
                return Task.FromResult(ExitCodes.Error);
            }

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.Outline(toolset.Session, outline)
                : TerseFormatter.Outline(outline));

            // An ambiguous path listed candidates rather than answering the question.
            return Task.FromResult(outline.CandidatePaths is null ? ExitCodes.Ok : ExitCodes.Error);
        });
    }

    private static Task<int> RunErrors(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root", "limit"], ["json"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            var limit = args.IntValue("limit", 200);
            if (args.Error is not null)
            {
                Console.Error.WriteLine(args.Error);
                return Task.FromResult(ExitCodes.Error);
            }

            var report = toolset.ParseErrors();

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.ParseErrors(toolset.Session, report)
                : TerseFormatter.ParseErrors(report, limit));

            // An imperfect parse is a fact about the workspace, not a failure of the query.
            return Task.FromResult(ExitCodes.Ok);
        });
    }

    private static Task<int> RunStats(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = ArgReader.Parse(rawArgs, ["root"], ["json"]);

        return CommandEnvironment.WithSession(args, toolset =>
        {
            if (args.Positionals.Count > 1)
            {
                Console.Error.WriteLine("usage: codeanalyzer " + Stats.Usage);
                return Task.FromResult(ExitCodes.Error);
            }

            var scope = args.Positionals.Count == 1 ? args.Positionals[0] : null;
            var stats = toolset.Stats(scope);

            Console.WriteLine(args.Switch("json")
                ? JsonFormatter.Stats(toolset.Session, stats)
                : TerseFormatter.Stats(stats));

            return Task.FromResult(ExitCodes.Ok);
        });
    }

    /// <summary>
    /// Resolves a symbol argument, printing the ambiguity or not-found answer itself when
    /// there is no single id to continue with. That answer goes to stdout — it IS the
    /// command's result, and an agent reads the candidate ids from it.
    /// </summary>
    private static bool TryLocate(
        AgentToolset toolset,
        string symbolText,
        bool json,
        CancellationToken cancellationToken,
        out LocatedSymbol symbol)
    {
        var result = toolset.Locate(symbolText, cancellationToken);

        switch (result)
        {
            case LocateResult.Resolved resolved:
                symbol = resolved.Symbol;
                return true;

            case LocateResult.Ambiguous ambiguous:
                Console.WriteLine(json
                    ? JsonFormatter.Locate(toolset.Session, ambiguous)
                    : TerseFormatter.Ambiguous(symbolText, ambiguous));
                break;

            case LocateResult.NotFound notFound:
                Console.WriteLine(json
                    ? JsonFormatter.Locate(toolset.Session, notFound)
                    : TerseFormatter.NotFound(notFound));
                break;
        }

        symbol = null!;
        return false;
    }
}
