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

    public static string Search(
        string query,
        IReadOnlyList<SymbolSearchHit> hits,
        string? kindFilter,
        bool exact = false)
    {
        if (hits.Count == 0)
        {
            // Every narrowing that was applied gets named. An active filter is the likeliest
            // reason a query that ought to match does not, and exact matching is the newest
            // way to get an empty list from a query that fuzzily found plenty.
            var subject = exact
                ? $"no symbols contain '{query}' verbatim (exact match)"
                : $"no symbols match '{query}'";

            return kindFilter is null ? subject : $"{subject} with kinds {kindFilter}";
        }

        var builder = new StringBuilder();
        foreach (var hit in hits)
        {
            builder.AppendLine(HitLine(hit));
        }

        return builder.Finish();
    }

    /// <summary>
    /// Definitions whose literal denotes one value. Each row states the notation it is
    /// written in plus what it denotes, because "0xA5 = 165" is the whole answer — a bare
    /// list of names would leave the reader to trust that the match is real.
    /// </summary>
    public static string Values(string subject, ValueMatchSet? set)
    {
        if (set is null)
        {
            return $"'{subject}' is not a literal value this build can read "
                + "(try 0xA5, 165, 0b1010, 0o755, 8'hA5 or \"COM3\")";
        }

        if (set.Matches.Count == 0)
        {
            return $"no definition carries the value {set.Canonical}";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{set.Canonical} — {set.Matches.Count} "
            + (set.Matches.Count == 1 ? "definition" : "definitions")
            + " in " + string.Join(", ", set.OtherLanguages));

        foreach (var match in set.Matches)
        {
            builder.AppendLine("  " + ValueLine(match));
        }

        if (set.Truncated)
        {
            builder.AppendLine($"  … list capped at {set.Limit}; more definitions carry this value");
        }

        return builder.Finish();
    }

    /// <summary>
    /// Values written in more than one place. The criterion is printed above the list,
    /// because a reader who does not know 0 and 1 were excluded is reading a different
    /// answer from the one produced.
    /// </summary>
    public static string SharedValues(
        IReadOnlyList<ValueGroup> groups,
        bool acrossDirectories,
        bool includeTrivial)
    {
        var criterion = (acrossDirectories
                ? "defined in at least two top-level directories"
                : "defined in at least two languages")
            + (includeTrivial ? ", 0 and 1 included" : ", excluding 0 and 1");

        if (groups.Count == 0)
        {
            return $"no values {criterion}";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{groups.Count} value(s) {criterion}:");

        foreach (var group in groups)
        {
            var spans = acrossDirectories ? group.TopDirectories : group.Languages;
            builder.AppendLine($"  {group.Canonical} — {group.TotalCount} definitions in "
                + string.Join(", ", spans));

            foreach (var member in group.Members)
            {
                builder.AppendLine("    " + ValueLine(member));
            }

            if (group.TotalCount > group.Members.Count)
            {
                builder.AppendLine($"    … {group.TotalCount - group.Members.Count} more not listed");
            }
        }

        return builder.Finish();
    }

    private static string ValueLine(ValueMatch match)
    {
        var name = match.ContainerName is null ? match.Name : $"{match.ContainerName}.{match.Name}";
        var written = Clip(Flatten(match.Verbatim), 60);
        return $"#{match.SymbolId} {name} = {written} {KindTokens.For(match.Kind)} "
            + $"[{match.Language}] {match.RelativePath}:{match.Line}";
    }

    public static string Detail(SymbolDetail detail, ValueMatchSet? sameValue = null)
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
                builder.AppendLine("  " + MemberLine(
                    member.Id,
                    member.Name,
                    member.ParameterText,
                    member.TypeText,
                    member.Value,
                    member.Kind,
                    member.Modifiers,
                    member.Line));
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

        // The one section that can cross a language boundary no reference does. Absent
        // rather than empty when the literal is not certifiable or nothing else shares it.
        if (sameValue is { Matches.Count: > 0 })
        {
            builder.AppendLine($"same value elsewhere ({sameValue.Matches.Count}"
                + (sameValue.Truncated ? "+" : string.Empty) + $", {sameValue.Canonical}):");

            foreach (var match in sameValue.Matches)
            {
                builder.AppendLine("  " + ValueLine(match));
            }

            if (sameValue.Truncated)
            {
                builder.AppendLine($"  … list capped at {sameValue.Limit}; "
                    + $"see all with: value {sameValue.Canonical}");
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
                    // The receiver, where one was written, is the evidence behind the
                    // confidence mark: `orchestrator.(…)~` explains itself.
                    var receiver = site.ReceiverText is null ? string.Empty : site.ReceiverText + ".";
                    builder.AppendLine($"    :{site.Line} {receiver}{site.ArgumentText}{ConfidenceMark(site.Confidence)}");
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
            output.AppendLine(indent + MemberLine(
                entry.Id,
                entry.Name,
                entry.ParameterText,
                entry.TypeText,
                entry.Value,
                entry.Kind,
                entry.Modifiers,
                entry.Line));
        }

        return output.Finish();
    }

    /// <summary>
    /// One definition row, in the single grammar every listing uses: id, name, parameters,
    /// declared type, value, kind, modifiers, line.
    /// <para>
    /// It exists because <c>detail</c> and <c>outline</c> used to print the same member two
    /// different ways — the outline stated modifiers and parameters, the fact sheet stated
    /// the value — so which facts you got depended on which command you happened to ask,
    /// and neither told you it was holding something back.
    /// </para>
    /// </summary>
    private static string MemberLine(
        long id,
        string name,
        string? parameterText,
        string? typeText,
        string? value,
        SymbolKind kind,
        string? modifiers,
        int line)
    {
        var type = typeText is null ? string.Empty : $" {Flatten(typeText)}";
        var literal = value is null ? string.Empty : $" = {Clip(Flatten(value), 80)}";
        var mods = string.IsNullOrEmpty(modifiers) ? string.Empty : $" {modifiers}";

        return $"#{id} {name}{Flatten(parameterText)}{type}{literal} "
            + $"{KindTokens.For(kind)}{mods} :{line}";
    }

    private static void AppendFact(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            builder.AppendLine($"{label}: {value}");
        }
    }
}
