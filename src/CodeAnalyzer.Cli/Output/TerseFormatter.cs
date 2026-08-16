using System.Text;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Search;

namespace CodeAnalyzer.Cli.Output;

/// <summary>
/// The token-compact text every command and MCP tool answers with. One symbol-line grammar
/// everywhere:
/// <code>#412 uart_init(void) fn drivers/uart.c:9</code>
/// Confidence rides as a suffix — <c>~</c> for a name match among several, <c>?</c> for a
/// cross-language match — and any output that used one explains it in a footer, so the
/// reader is never left decoding punctuation.
/// </summary>
/// <summary>
/// Ends a terse block: LF endings regardless of platform (an agent pays a token for every
/// CR AppendLine would add on Windows) and no trailing newline.
/// </summary>
internal static class TerseBuilderExtensions
{
    public static string Finish(this StringBuilder builder) =>
        builder.ToString().Replace("\r\n", "\n").TrimEnd();
}

internal static class TerseFormatter
{
    private const string AmbiguousMark = "~";
    private const string WeakMark = "?";

    private const string ConfidenceFooter =
        $"({AmbiguousMark} = one of several name matches, {WeakMark} = cross-language name match)";

    /// <summary>
    /// Collapses runs of whitespace to one space for one-line contexts. A verbatim
    /// parameter list spanning several source lines would otherwise break the line
    /// grammar; only layout changes, every token survives. (The same rule
    /// <c>SymbolFacts.Describe</c> applies to its descriptor.)
    /// </summary>
    private static string? Flatten(string? text) =>
        text is null || !text.Any(char.IsWhiteSpace)
            ? text
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Cuts a long verbatim slice for a one-line context, with the ellipsis that says a cut
    /// happened — a multi-hundred-character initializer would otherwise drown the line the
    /// reader came for. The full text stays available through <c>--json</c>.
    /// </summary>
    private static string? Clip(string? text, int max = 120) =>
        text is null || text.Length <= max ? text : text[..max] + "…";

    // ---- shared line fragments --------------------------------------------

    public static string SymbolLine(LocatedSymbol symbol) =>
        $"#{symbol.Id} {symbol.Name}{Flatten(symbol.ParameterText)} {KindTokens.For(symbol.Kind)} "
        + $"{symbol.RelativePath}:{symbol.Line}";

    private static string HitLine(SymbolSearchHit hit)
    {
        var name = hit.ContainerName is null ? hit.Name : $"{hit.ContainerName}.{hit.Name}";
        var line = $"#{hit.SymbolId} {name}{Flatten(hit.ParameterText)} {KindTokens.For(hit.Kind)} "
            + $"{hit.RelativePath}:{hit.Line}";

        // The descriptor repeats the kind label for a plain symbol; append it only when
        // it says something more (modifiers, declared type, overload position).
        return hit.Descriptor == KindLabels.For(hit.Kind) ? line : $"{line} · {hit.Descriptor}";
    }

    private static string RelatedLine(RelatedSymbol related) =>
        $"#{related.Id} {related.Name} {KindTokens.For(related.Kind)} "
        + $"{related.RelativePath}:{related.Line} {KindLabels.For(related.ReferenceKind)}"
        + ConfidenceMark(related.Confidence);

    private static string ConfidenceMark(EdgeConfidence confidence) => confidence switch
    {
        EdgeConfidence.Ambiguous => AmbiguousMark,
        EdgeConfidence.Weak => WeakMark,
        _ => string.Empty,
    };

    private static bool Uncertain(EdgeConfidence confidence) => confidence != EdgeConfidence.Unique;

    // ---- locate outcomes ---------------------------------------------------

    /// <summary>The candidate list for an ambiguous name: pick an id and re-run.</summary>
    public static string Ambiguous(string symbolText, LocateResult.Ambiguous result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"'{symbolText}' has {(result.More ? "more than " : string.Empty)}"
            + $"{result.Candidates.Count} definitions — pass an id:");

        foreach (var candidate in result.Candidates)
        {
            builder.AppendLine("  " + SymbolLine(candidate));
        }

        if (result.More)
        {
            builder.AppendLine($"  … list capped at {SymbolLocator.MaxCandidates}; narrow with path:name");
        }

        return builder.Finish();
    }

    public static string NotFound(LocateResult.NotFound result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Message);

        if (result.Suggestions.Count > 0)
        {
            builder.AppendLine("closest names in the index:");
            foreach (var suggestion in result.Suggestions)
            {
                builder.AppendLine("  " + SymbolLine(suggestion));
            }
        }

        return builder.Finish();
    }

    // ---- per-command bodies -------------------------------------------------

    public static string Search(string query, IReadOnlyList<SymbolSearchHit> hits, string? kindFilter)
    {
        if (hits.Count == 0)
        {
            return kindFilter is null
                ? $"no symbols match '{query}'"
                : $"no symbols match '{query}' with kinds {kindFilter}";
        }

        var builder = new StringBuilder();
        foreach (var hit in hits)
        {
            builder.AppendLine(HitLine(hit));
        }

        return builder.Finish();
    }

    public static string Detail(SymbolDetail detail)
    {
        var builder = new StringBuilder();

        var location = detail.EndLine > detail.StartLine
            ? $"{detail.RelativePath}:{detail.StartLine}-{detail.EndLine}"
            : $"{detail.RelativePath}:{detail.StartLine}";

        builder.AppendLine(
            $"#{detail.Id} {detail.Name}{Flatten(detail.ParameterText)} {KindTokens.For(detail.Kind)} "
            + $"{location} [{detail.Language}]");

        AppendFact(builder, "signature", Clip(Flatten(detail.Signature), 200));
        AppendFact(builder, "modifiers", detail.Modifiers);
        AppendFact(builder, "type", detail.TypeText);
        AppendFact(builder, "value", Clip(Flatten(detail.Value)));

        if (detail.Members.Count > 0)
        {
            builder.AppendLine($"members ({detail.Members.Count}):");
            foreach (var member in detail.Members)
            {
                var type = member.TypeText is null ? string.Empty : $" {Flatten(member.TypeText)}";
                var value = member.Value is null ? string.Empty : $" = {Clip(Flatten(member.Value), 80)}";
                builder.AppendLine(
                    $"  #{member.Id} {member.Name}{type}{value} {KindTokens.For(member.Kind)} :{member.Line}");
            }
        }

        if (detail.Overloads.Count > 0)
        {
            builder.AppendLine($"overloads ({detail.Overloads.Count}):");
            foreach (var overload in detail.Overloads)
            {
                var marker = overload.IsCurrent ? "  (this one)" : string.Empty;
                builder.AppendLine(
                    $"  #{overload.Id} {detail.Name}{Flatten(overload.ParameterText)} :{overload.Line}{marker}");
            }
        }

        builder.AppendLine($"callers: {detail.Callers.Count}  callees: {detail.Callees.Count}"
            + "  (list with: callers/callees #" + detail.Id + ")");

        if (detail.UnresolvedReferences.Count > 0)
        {
            builder.AppendLine($"unresolved ({detail.UnresolvedReferences.Count}):");
            foreach (var unresolved in detail.UnresolvedReferences)
            {
                builder.AppendLine(
                    $"  {unresolved.Name} {KindLabels.For(unresolved.Kind)}@{unresolved.Line} — no definition in the index");
            }
        }

        return builder.Finish();
    }

    /// <summary>
    /// Callers or callees of one symbol. <paramref name="sites"/> (keyed by related symbol
    /// id) adds each merged edge's individual call sites when the command asked for them.
    /// </summary>
    public static string Related(
        LocatedSymbol focus,
        IReadOnlyList<RelatedSymbol> related,
        string direction,
        int listCap,
        IReadOnlyDictionary<long, List<EdgeCallSite>>? sites)
    {
        if (related.Count == 0)
        {
            return $"{focus.Name} has no {direction} in the index";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{direction} of {SymbolLine(focus)}:");

        var anyUncertain = false;
        foreach (var entry in related)
        {
            anyUncertain |= Uncertain(entry.Confidence);
            builder.AppendLine("  " + RelatedLine(entry));

            if (sites is not null && sites.TryGetValue(entry.Id, out var entrySites))
            {
                foreach (var site in entrySites)
                {
                    anyUncertain |= Uncertain(site.Confidence);
                    builder.AppendLine($"    :{site.Line} {site.ArgumentText}{ConfidenceMark(site.Confidence)}");
                }
            }
        }

        if (related.Count >= listCap)
        {
            builder.AppendLine($"  … list capped at {listCap} per direction");
        }

        if (anyUncertain)
        {
            builder.AppendLine(ConfidenceFooter);
        }

        return builder.Finish();
    }

    public static string Trace(LocatedSymbol from, LocatedSymbol to, PathTrace trace)
    {
        if (!trace.FromExists || !trace.ToExists)
        {
            return "one of the endpoints is no longer in the index — re-run search";
        }

        var builder = new StringBuilder();

        if (trace.Routes.Count == 0)
        {
            // The difference between these two lines is the whole reason the flags exist.
            builder.AppendLine(trace.SearchExhausted
                ? $"no route from {from.Name} to {to.Name} found within the search budget — "
                    + "absence of a route is NOT proven (raise --depth to look further)"
                : $"no route: {from.Name} does not reach {to.Name} through resolved references");
            return builder.Finish();
        }

        var names = trace.Nodes.ToDictionary(n => n.Id, n => n.Name);
        var linkConfidence = trace.Links
            .GroupBy(l => (l.SourceId, l.TargetId))
            .ToDictionary(g => g.Key, g => g.Max(l => l.Confidence));

        builder.AppendLine($"{trace.Length} hops, {trace.Routes.Count} route(s) from "
            + $"{from.Name} to {to.Name}:");

        var anyUncertain = false;
        foreach (var route in trace.Routes)
        {
            var parts = new List<string> { names.GetValueOrDefault(route[0], $"#{route[0]}") };

            for (var i = 1; i < route.Count; i++)
            {
                var confidence = linkConfidence.GetValueOrDefault(
                    (route[i - 1], route[i]), EdgeConfidence.Unique);
                anyUncertain |= Uncertain(confidence);
                parts.Add($"-{ConfidenceMark(confidence)}> {names.GetValueOrDefault(route[i], $"#{route[i]}")}");
            }

            builder.AppendLine("  " + string.Join(' ', parts));
        }

        if (trace.Truncated)
        {
            builder.AppendLine("  … more routes of this length exist than the cap allows");
        }

        if (anyUncertain)
        {
            builder.AppendLine(ConfidenceFooter);
        }

        return builder.Finish();
    }

    public static string Boundaries(IReadOnlyList<IoBoundarySite> sites)
    {
        if (sites.Count == 0)
        {
            return "no I/O boundaries: no catalog API matched and nothing is marked "
                + "(mark one with the GUI, or extend the catalog)";
        }

        var builder = new StringBuilder();

        foreach (var direction in sites
            .GroupBy(s => s.Direction)
            .OrderBy(g => g.Key))
        {
            builder.AppendLine($"{IoDirectionLabels.For(direction.Key)} ({direction.Count()} sites):");

            // One header per API carrying the shared facts (source, gate note), so a gate
            // stated once covers its sites instead of repeating under every line.
            foreach (var api in direction
                .GroupBy(s => (s.Name, s.Origin, s.Family, s.GateNote))
                .OrderBy(g => g.Key.Name, StringComparer.Ordinal))
            {
                var source = api.Key.Origin == IoMatchOrigin.UserMark
                    ? "your mark"
                    : $"catalog: {api.Key.Family}";
                var gate = api.Key.GateNote is null
                    ? string.Empty
                    : $"  gate: {api.Key.GateNote} — still a name match";

                builder.AppendLine($"  {api.Key.Name} [{source}]{gate}");

                foreach (var site in api.OrderBy(s => s.RelativePath).ThenBy(s => s.Line))
                {
                    var caller = site.CallerName ?? "(file scope)";
                    var arguments = site.ArgumentText is null ? string.Empty : $"  {Flatten(site.ArgumentText)}";
                    builder.AppendLine($"    {site.RelativePath}:{site.Line}  in {caller}{arguments}");
                }
            }
        }

        builder.AppendLine("direction comes from the API's documentation or your mark, never from syntax");
        return builder.Finish();
    }

    public static string RepoMap(RepoMap map, int charBudget)
    {
        var builder = new StringBuilder();

        if (map.Entries.Count == 0)
        {
            builder.AppendLine("no resolved references in the index, so the map cannot rank anything — "
                + "files by definition count instead:");
            foreach (var (path, symbols) in map.FilesBySymbolCount)
            {
                if (builder.Length > charBudget)
                {
                    builder.AppendLine("  …");
                    break;
                }

                builder.AppendLine($"  {path} ({symbols} definitions)");
            }

            return builder.Finish();
        }

        builder.AppendLine("repo map: definitions ranked by distinct incoming references (N< = N referencers)");

        // Walk in rank order, opening a file group the first time its file appears —
        // groups therefore land in max-fan-in order — and stop at the budget, saying so.
        var emittedByFile = new Dictionary<string, List<RepoMapEntry>>();
        var fileOrder = new List<string>();
        var emitted = 0;
        var spent = builder.Length;

        foreach (var entry in map.Entries)
        {
            var line = MapLine(entry);
            var cost = line.Length + 1 + (emittedByFile.ContainsKey(entry.RelativePath)
                ? 0
                : entry.RelativePath.Length + 1);

            if (spent + cost > charBudget && emitted > 0)
            {
                break;
            }

            if (!emittedByFile.TryGetValue(entry.RelativePath, out var group))
            {
                group = [];
                emittedByFile[entry.RelativePath] = group;
                fileOrder.Add(entry.RelativePath);
            }

            group.Add(entry);
            spent += cost;
            emitted++;
        }

        foreach (var path in fileOrder)
        {
            builder.AppendLine(path);
            foreach (var entry in emittedByFile[path])
            {
                builder.AppendLine(MapLine(entry));
            }
        }

        if (emitted < map.Entries.Count)
        {
            var remaining = map.Entries.Skip(emitted).ToList();
            var remainingFiles = remaining.Select(e => e.RelativePath).Distinct().Count();
            builder.AppendLine($"-- truncated at {charBudget} chars: {remaining.Count} more ranked "
                + $"symbols across {remainingFiles} files (raise --budget)");
        }

        if (map.FetchCapHit)
        {
            builder.AppendLine("-- ranking capped: symbols beyond the top 2,000 referenced are not listed");
        }

        return builder.Finish();
    }

    private static string MapLine(RepoMapEntry entry)
    {
        var name = entry.ContainerName is null ? entry.Name : $"{entry.ContainerName}.{entry.Name}";
        var modifiers = entry.Modifiers is null ? string.Empty : $" {entry.Modifiers}";
        return $"  {entry.FanIn}< {name}{Flatten(entry.ParameterText)} {KindTokens.For(entry.Kind)}{modifiers} :{entry.Line}";
    }

    public static string Outline(FileOutline outline)
    {
        if (outline.CandidatePaths is not null)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"'{outline.RelativePath}' matches {outline.CandidatePaths.Count} files — be more specific:");
            foreach (var path in outline.CandidatePaths)
            {
                builder.AppendLine("  " + path);
            }

            return builder.Finish();
        }

        var output = new StringBuilder();
        output.AppendLine($"{outline.RelativePath} [{outline.Language}] ({outline.Entries.Count} definitions)");

        foreach (var entry in outline.Entries)
        {
            var indent = new string(' ', 2 + entry.Depth * 2);
            var type = entry.TypeText is null ? string.Empty : $" {Flatten(entry.TypeText)}";
            var value = entry.Value is null ? string.Empty : $" = {Clip(Flatten(entry.Value), 80)}";
            var modifiers = entry.Modifiers is null ? string.Empty : $" {entry.Modifiers}";

            output.AppendLine($"{indent}#{entry.Id} {entry.Name}{Flatten(entry.ParameterText)}{type}{value} "
                + $"{KindTokens.For(entry.Kind)}{modifiers} :{entry.Line}");
        }

        return output.Finish();
    }

    private static void AppendFact(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            builder.AppendLine($"{label}: {value}");
        }
    }
}
