using System.ComponentModel;
using CodeAnalyzer.Cli.Output;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Export;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Workspaces;
using CodeAnalyzer.Parsing;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace CodeAnalyzer.Cli.Mcp;

/// <summary>
/// The MCP tool surface: every method is a thin adapter over <see cref="AgentToolset"/>,
/// answering in the same terse text the CLI prints — one implementation, two transports.
/// Failures come back as readable sentences rather than protocol errors, because the
/// caller is an agent that can act on "no index — call reindex" but not on a stack trace.
/// </summary>
[McpServerToolType]
internal sealed class CodeAnalyzerTools(McpSessionHolder holder)
{
    [McpServerTool(Name = "search_symbols")]
    [Description("Fuzzy search over the workspace's symbol definitions ('uwr' finds uart_write). "
        + "Returns one line per hit: #id, name, kind, file:line.")]
    public string SearchSymbols(
        [Description("Query text; word-hump aware, like an IDE symbol search")] string query,
        [Description("Optional comma-separated kind filter: families (function, type, interface, "
            + "constant, macro, module, variable) or kind tokens (fn, method, class, struct, …)")]
        string? kinds = null,
        [Description("Max hits, default 20")] int limit = 20,
        [Description("Match the query verbatim instead of fuzzily. Use it when a short or common "
            + "word returns hits that merely contain its letters in order — 'export' fuzzily "
            + "matches 'AnExtraIgnoredDirectoryIsNotReported'.")]
        bool exact = false,
        CancellationToken cancellationToken = default) =>
        WithToolset(toolset =>
        {
            IReadOnlySet<SymbolKind>? kindSet = null;
            if (kinds is not null)
            {
                kindSet = KindTokens.Parse(kinds, out var error);
                if (error is not null)
                {
                    return error;
                }
            }

            var hits = toolset.Search(
                query, kindSet, Math.Clamp(limit, 1, 200), exact, cancellationToken);
            return TerseFormatter.Search(query, hits, kinds, exact);
        });

    [McpServerTool(Name = "get_symbol")]
    [Description("One symbol's fact sheet: signature, modifiers, value, base and derived "
        + "types, members, overloads, caller/callee counts, unresolved references.")]
    public string GetSymbol(
        [Description("Symbol name, path/to/file.c:name, Container.Name, or #id from a previous result")]
        string symbol,
        CancellationToken cancellationToken = default) =>
        WithLocated(symbol, cancellationToken, (toolset, focus) =>
        {
            var detail = toolset.GetDetail(focus.Id, cancellationToken);
            return detail is null
                ? $"symbol #{focus.Id} vanished between locating and reading it — re-run search_symbols"
                : TerseFormatter.Detail(detail, toolset.SameValue(focus.Id, cancellationToken));
        });

    [McpServerTool(Name = "get_context")]
    [Description("One symbol's full context as a markdown document: signature, members, "
        + "callers and callees with per-site verbatim arguments, I/O boundaries, same-value "
        + "matches and a capped source excerpt. Richer than get_symbol and costs more "
        + "tokens — reach for it when you are about to work on the symbol, not merely "
        + "checking a fact.")]
    public string GetContext(
        [Description("Symbol name, path/to/file.c:name, Container.Name, or #id from a previous result")]
        string symbol,
        CancellationToken cancellationToken = default) =>
        WithLocated(symbol, cancellationToken, (toolset, focus) =>
        {
            var report = toolset.Report(focus.Id, cancellationToken);
            return report is null
                ? $"symbol #{focus.Id} vanished between locating and reading it — re-run search_symbols"
                : MarkdownFactWriter.Write(report);
        });

    [McpServerTool(Name = "find_by_value")]
    [Description("Definitions whose literal denotes a given value, in any language and any "
        + "notation: 0xA5 also finds 165 in C, 0xA5 in Python and 8'hA5 in Verilog. This is the "
        + "one query that crosses a language boundary no call or import connects — use it to "
        + "trace a protocol command byte, a baud rate or a port name between a sender and a "
        + "receiver. A shared value is evidence of an agreement, not proof of one.")]
    public string FindByValue(
        [Description("A literal: 165, 0xA5, 0b1010, 0o755, 8'hA5, or a quoted string like \"COM3\"")]
        string literal,
        [Description("Max definitions, default 50")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        WithToolset(toolset => TerseFormatter.Values(
            literal, toolset.FindByValue(literal, Math.Clamp(limit, 1, 200), cancellationToken)));

    [McpServerTool(Name = "shared_constants")]
    [Description("Values defined in more than one language (or, with by_directory, in more than "
        + "one top-level directory), ranked by how many languages agree on them. The overview "
        + "form of find_by_value: use it to see a protocol's command bytes, baud rates and port "
        + "names before reading either side's source.")]
    public string SharedConstants(
        [Description("Compare top-level directories instead of languages")] bool by_directory = false,
        [Description("Include 0 and 1, which almost everything carries")] bool include_trivial = false,
        CancellationToken cancellationToken = default) =>
        WithToolset(toolset => TerseFormatter.SharedValues(
            toolset.SharedValues(by_directory, include_trivial, cancellationToken),
            by_directory,
            include_trivial));

    [McpServerTool(Name = "get_callers")]
    [Description("Who references this symbol, with resolution confidence. "
        + "include_sites adds each call site's line and verbatim arguments.")]
    public string GetCallers(
        [Description("Symbol name, path:name, Container.Name, or #id")] string symbol,
        [Description("Also list each individual call site")] bool include_sites = false,
        CancellationToken cancellationToken = default) =>
        RelatedFor(symbol, callers: true, include_sites, cancellationToken);

    [McpServerTool(Name = "get_callees")]
    [Description("What this symbol references, with resolution confidence.")]
    public string GetCallees(
        [Description("Symbol name, path:name, Container.Name, or #id")] string symbol,
        [Description("Also list each individual call site")] bool include_sites = false,
        CancellationToken cancellationToken = default) =>
        RelatedFor(symbol, callers: false, include_sites, cancellationToken);

    [McpServerTool(Name = "trace_paths")]
    [Description("All shortest routes between two symbols through resolved references. "
        + "Says explicitly when the search budget, not the graph, is what stopped it.")]
    public string TracePaths(
        [Description("Start symbol: name, path:name, or #id")] string from,
        [Description("Destination symbol: name, path:name, or #id")] string to,
        [Description("Max hops to search, default 8")] int? max_depth = null,
        CancellationToken cancellationToken = default) =>
        WithLocated(from, cancellationToken, (toolset, fromSymbol) =>
        {
            var located = toolset.Locate(to, cancellationToken);
            if (located is not LocateResult.Resolved resolvedTo)
            {
                return DescribeLocateFailure(to, located);
            }

            var trace = toolset.Trace(fromSymbol.Id, resolvedTo.Symbol.Id, max_depth, cancellationToken);
            return TerseFormatter.Trace(fromSymbol, resolvedTo.Symbol, trace);
        });

    [McpServerTool(Name = "repo_map")]
    [Description("Codebase overview to prime context with: definitions ranked by how many "
        + "distinct symbols reference them, grouped by file, cut to a character budget.")]
    public string RepoMap(
        [Description("Character budget for the map text, default 8000")] int char_budget = 8000,
        CancellationToken cancellationToken = default) =>
        WithToolset(toolset => TerseFormatter.RepoMap(
            toolset.Map(cancellationToken), Math.Clamp(char_budget, 500, 200_000)));

    [McpServerTool(Name = "io_boundaries")]
    [Description("Where data leaves and enters the workspace: serial/USB/socket/file I/O call "
        + "sites from a built-in API catalog plus the user's own marks. Direction is the API's "
        + "documented contract or the user's assertion, never derived from syntax.")]
    public string IoBoundaries(CancellationToken cancellationToken = default) =>
        WithToolset(toolset => TerseFormatter.Boundaries(toolset.Boundaries(cancellationToken)));

    [McpServerTool(Name = "file_outline")]
    [Description("One file's definitions in source order, indented by containment.")]
    public string FileOutline(
        [Description("Relative path, or any unambiguous suffix of it (uart.c, drivers/uart.c)")]
        string rel_path) =>
        WithToolset(toolset =>
        {
            var outline = toolset.Outline(rel_path);
            return outline is null
                ? $"no indexed file matches '{rel_path}'"
                : TerseFormatter.Outline(outline);
        });

    [McpServerTool(Name = "parse_errors")]
    [Description("Files the parser could not fully read, with the construct it stopped at and a "
        + "tally of the most common ones. Use it to judge how much of the workspace every other "
        + "answer is drawn from. A file listed here was still indexed — it lands here when the "
        + "bundled grammar cannot read one construct, which is more often a language feature "
        + "newer than the grammar than a mistake in the file.")]
    public string ParseErrors(
        [Description("Max files listed, default 200. The tally always covers all of them.")]
        int limit = 200) =>
        WithToolset(toolset =>
            TerseFormatter.ParseErrors(toolset.ParseErrors(), Math.Clamp(limit, 1, 5000)));

    [McpServerTool(Name = "stats")]
    [Description("Aggregate facts about the index itself: files by language, symbols by kind, "
        + "and — the part no other tool states — how well resolution is doing, as a per-reference "
        + "breakdown into resolved uniquely / ambiguous / unresolved. Use it to judge the index "
        + "before trusting any per-symbol answer, or to measure a change. Pass a path to narrow "
        + "every count to one file or subtree.")]
    public string Stats(
        [Description("Optional workspace-relative file or directory; limits every count to it. "
            + "Omit for the whole workspace.")]
        string? path = null) =>
        WithToolset(toolset => TerseFormatter.Stats(toolset.Stats(path)));

    [McpServerTool(Name = "reindex")]
    [Description("Rebuild or refresh the workspace's index through the full pipeline. "
        + "Run this when the index is missing or the build date in results looks stale.")]
    public async Task<string> Reindex(
        [Description("Re-parse every file instead of skipping unchanged ones. Needed only "
            + "after a query pack changed, since the incremental gate reads the file and "
            + "cannot see that the analyzer now reads it differently.")]
        bool full = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var session = WorkspaceSession.Open(holder.RootPath, new TreeSitterAnalyzerFactory());
            var result = await session
                .IndexAsync(session.LoadSelectedDirectories(), progress: null, cancellationToken, full)
                .ConfigureAwait(false);

            if (result.Outcome.WasCancelled)
            {
                return "reindex cancelled — the index keeps what was already written";
            }

            var outcome = result.Outcome;
            return $"indexed {holder.RootPath}: {outcome.FilesParsed} parsed, "
                + $"{outcome.FilesUnchanged} unchanged, {outcome.FilesFailed} failed, "
                + $"{outcome.FilesWithSyntaxErrors} with syntax errors, {result.FilesRemoved} removed · "
                + $"{outcome.SymbolsFound:N0} symbols, {result.EdgesCreated:N0} links "
                + $"in {result.Elapsed.TotalSeconds:0.0}s";
        }
        catch (SqliteException e)
        {
            return $"the index is locked or unreadable — is the GUI mid-index? ({e.Message})";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Anything else still has to come back as a sentence. Letting it escape makes
            // the protocol answer "An error occurred invoking 'reindex'", which is the least
            // actionable string this tool can produce and hid a real bug in the full-reparse
            // path for a whole round.
            return $"reindex failed: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            // Whatever happened, the next read should reopen: a completed run has a new
            // stamp, and a schema rebuild invalidates the old connection entirely.
            holder.Invalidate();
        }
    }

    // ---- shared plumbing ----------------------------------------------------

    private string RelatedFor(
        string symbol, bool callers, bool includeSites, CancellationToken cancellationToken) =>
        WithLocated(symbol, cancellationToken, (toolset, focus) =>
        {
            var detail = toolset.GetDetail(focus.Id, cancellationToken);
            if (detail is null)
            {
                return $"symbol #{focus.Id} vanished between locating and reading it — re-run search_symbols";
            }

            var related = callers ? detail.Callers : detail.Callees;

            Dictionary<long, List<EdgeCallSite>>? sites = null;
            if (includeSites)
            {
                sites = [];
                foreach (var entry in related)
                {
                    sites[entry.Id] = callers
                        ? toolset.GetCallSites(entry.Id, focus.Id, entry.ReferenceKind, cancellationToken)
                        : toolset.GetCallSites(focus.Id, entry.Id, entry.ReferenceKind, cancellationToken);
                }
            }

            return TerseFormatter.Related(
                focus,
                related,
                callers ? TerseFormatter.Callers : TerseFormatter.Callees,
                toolset.Session.Graph.RelatedLimit,
                sites);
        });

    /// <summary>Session + provenance + busy handling around every read tool.</summary>
    private string WithToolset(Func<AgentToolset, string> body)
    {
        var (toolset, problem) = holder.Get();
        if (toolset is null)
        {
            return problem!;
        }

        try
        {
            // Every answer names its vintage: an agent must be able to tell fresh facts
            // from facts about a tree that has moved on.
            return $"[{toolset.Session.DescribeIndex().Replace("run 'codeanalyzer index'", "call reindex")}]\n"
                + body(toolset);
        }
        catch (IndexUnavailableException e)
        {
            holder.Invalidate();
            return e.Message + " — retry shortly";
        }
    }

    private string WithLocated(
        string symbolText,
        CancellationToken cancellationToken,
        Func<AgentToolset, LocatedSymbol, string> body) =>
        WithToolset(toolset =>
        {
            var located = toolset.Locate(symbolText, cancellationToken);
            return located is LocateResult.Resolved resolved
                ? body(toolset, resolved.Symbol)
                : DescribeLocateFailure(symbolText, located);
        });

    private static string DescribeLocateFailure(string symbolText, LocateResult result) => result switch
    {
        LocateResult.Ambiguous ambiguous => TerseFormatter.Ambiguous(symbolText, ambiguous),
        LocateResult.NotFound notFound => TerseFormatter.NotFound(notFound),
        _ => $"could not resolve '{symbolText}'",
    };
}
