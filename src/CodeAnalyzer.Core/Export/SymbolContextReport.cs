using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;

namespace CodeAnalyzer.Core.Export;

/// <summary>
/// Everything the index can say about one symbol, gathered for a fact report — the
/// aggregate <see cref="MarkdownFactWriter"/> renders. Building and rendering are
/// separate so the writer stays pure and table-testable.
/// </summary>
public sealed record SymbolContextReport
{
    public required SymbolDetail Detail { get; init; }

    /// <summary>Where the facts came from, e.g. the index provenance line. Stated in the header.</summary>
    public string? Provenance { get; init; }

    /// <summary>Call sites for the first few callees, each list capped by the underlying query.</summary>
    public IReadOnlyList<CalleeCallSites> CalleeSites { get; init; } = [];

    /// <summary>I/O boundary sites whose caller is this symbol.</summary>
    public IReadOnlyList<IoBoundarySite> IoSites { get; init; } = [];

    /// <summary>Definitions elsewhere whose literal denotes the same value, when any.</summary>
    public ValueMatchSet? SameValue { get; init; }

    /// <summary>The symbol's own source lines, when the file could be read.</summary>
    public string? SourceExcerpt { get; init; }

    /// <summary>Last line included in the excerpt — below <c>Detail.EndLine</c> when cut.</summary>
    public int SourceExcerptEndLine { get; init; }

    /// <summary>True when the excerpt stops short of the symbol's own end.</summary>
    public bool SourceTruncated { get; init; }

    /// <summary>The query cap on the caller/callee lists, so the report can word its own limit.</summary>
    public required int RelatedLimit { get; init; }
}

/// <summary>One callee with the individual call sites behind the merged edge.</summary>
public sealed record CalleeCallSites(RelatedSymbol Callee, IReadOnlyList<EdgeCallSite> Sites);

/// <summary>
/// Assembles a <see cref="SymbolContextReport"/> from the query services. Lives in Core so
/// the GUI's clipboard command and the CLI's report command cannot drift apart.
/// </summary>
public static class SymbolContextReportBuilder
{
    /// <summary>Callees whose per-site lists are fetched; past this the merged line stands alone.</summary>
    public const int MaxCalleesWithSites = 20;

    /// <summary>Source lines included before the excerpt is cut, with the cut stated.</summary>
    public const int MaxSourceLines = 120;

    /// <summary>
    /// Builds the report, or returns null when the symbol is gone (its file may have been
    /// re-indexed since the id was obtained). The caller owns gating — every read here
    /// goes straight at the services.
    /// </summary>
    public static SymbolContextReport? Build(
        GraphQueryService graph,
        ValueQueryService values,
        IReadOnlyList<IoBoundarySite> ioSites,
        string rootPath,
        long symbolId,
        string? provenance = null,
        CancellationToken cancellationToken = default)
    {
        var detail = graph.GetDetail(symbolId, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var calleeSites = new List<CalleeCallSites>();
        foreach (var callee in detail.Callees.Take(MaxCalleesWithSites))
        {
            var sites = graph.GetEdgeCallSites(
                symbolId, callee.Id, callee.ReferenceKind, cancellationToken);
            if (sites.Count > 0)
            {
                calleeSites.Add(new CalleeCallSites(callee, sites));
            }
        }

        var (excerpt, excerptEnd, truncated) = ReadExcerpt(rootPath, detail);

        return new SymbolContextReport
        {
            Detail = detail,
            Provenance = provenance,
            CalleeSites = calleeSites,
            IoSites = ioSites,
            SameValue = values.GetSameValue(symbolId, cancellationToken),
            SourceExcerpt = excerpt,
            SourceExcerptEndLine = excerptEnd,
            SourceTruncated = truncated,
            RelatedLimit = graph.RelatedLimit,
        };
    }

    private static (string? Excerpt, int EndLine, bool Truncated) ReadExcerpt(
        string rootPath, SymbolDetail detail)
    {
        string[] lines;
        try
        {
            var fullPath = Path.Combine(
                rootPath, detail.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            lines = File.ReadAllLines(fullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // No excerpt is a statement the report makes visibly; a stale or unreadable
            // file must not fail the rest of the facts.
            return (null, 0, false);
        }

        var start = detail.StartLine;
        var end = Math.Max(start, detail.EndLine);
        if (start < 1 || start > lines.Length)
        {
            // The index and the file disagree — the file has changed since indexing.
            // An excerpt of the wrong lines would be worse than none.
            return (null, 0, false);
        }

        end = Math.Min(end, lines.Length);
        var cappedEnd = Math.Min(end, start + MaxSourceLines - 1);
        var excerpt = string.Join('\n', lines[(start - 1)..cappedEnd]);
        return (excerpt, cappedEnd, cappedEnd < end);
    }
}
