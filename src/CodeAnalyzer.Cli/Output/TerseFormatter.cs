using System.Text;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;

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

    /// <summary>
    /// The two directions <see cref="Related"/> understands. Named rather than spelled at
    /// each call site because the string is load-bearing twice over — it is printed, and it
    /// says which end of an edge a call site's text belongs to.
    /// </summary>
    public const string Callers = "callers";

    /// <inheritdoc cref="Callers"/>
    public const string Callees = "callees";

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

        // Loose hits are the tail of the list by construction — one query, so one floor,
        // and the list is already sorted by score. Finding where they start is therefore a
        // count, not a partition, and the reader gets one boundary line instead of a mark
        // on every row.
        var strong = hits.Count(hit => !hit.LooseMatch);

        var builder = new StringBuilder();

        if (strong == 0)
        {
            // The whole answer is that there isn't one. Saying it first is the difference
            // between a reader treating the list as results and treating it as a shrug.
            builder.AppendLine($"no symbol matches '{query}' well — in these the letters "
                + "merely appear in order:");
        }

        for (var i = 0; i < hits.Count; i++)
        {
            if (i == strong && strong > 0)
            {
                builder.AppendLine($"… and {hits.Count - strong} where the letters merely "
                    + "appear in order:");
            }

            builder.AppendLine(HitLine(hits[i]));
        }

        if (strong == 0 && !exact)
        {
            // Named by what it is rather than by how to spell it: the CLI switch is
            // --exact and the MCP parameter is exact:true, and advice that names the
            // wrong one is worse than advice that names neither.
            builder.AppendLine("(asking for an exact match instead answers whether any "
                + $"name contains '{query}' verbatim)");
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

        // The base list as written, resolved half located, unresolved half named — hiding
        // the latter would misreport `class Foo : IDisposable` as deriving from nothing.
        if (detail.BaseTypes.Count > 0)
        {
            builder.AppendLine("derives from: " + string.Join(" · ", detail.BaseTypes.Select(b =>
                b.TargetId is { } id
                    ? $"#{id} {b.Name} {b.TargetPath}:{b.TargetLine}"
                    : $"{b.Name} (not in workspace)")));
        }

        if (detail.DerivedTypes.Count > 0)
        {
            builder.AppendLine($"derived by ({detail.DerivedTypes.Count}): " + string.Join(" · ",
                detail.DerivedTypes.Take(12).Select(d => $"#{d.Id} {d.Name}"))
                + (detail.DerivedTypes.Count > 12 ? " · …" : string.Empty));
        }

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

        // The listing marks a cross-language name match '?', and the fact sheet's headline
        // count did not — so `callers: 24` read as twenty-four call sites when eight of them
        // were a minified JavaScript identifier that happens to spell the same word. The
        // count is not wrong, but a number that needs the list open to be read correctly is
        // the same defect the unresolved column had before its external share was split out.
        builder.AppendLine(
            $"callers: {detail.Callers.Count}{CrossLanguageNote(detail.Callers)}  "
            + $"callees: {detail.Callees.Count}{CrossLanguageNote(detail.Callees)}"
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
        IReadOnlyDictionary<long, List<EdgeCallSite>>? sites,
        int total = 0)
    {
        // Which end of the edge the site text is written against: asking for callers, every
        // site names the focus; asking for callees, each site names that entry.
        var siteNamesFocus = direction == Callers;

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
                    // The site rebuilt as the source wrote it. The receiver is the evidence
                    // behind the confidence mark — `orchestrator.IndexAsync(…)~` explains
                    // itself — and the name is what keeps its dot attached to something:
                    // a use carries no arguments, so a site used to read `:632 SymbolKind.`
                    // with the dot dangling, or `:454` with nothing after it at all.
                    var fallback = siteNamesFocus ? focus.Name : entry.Name;
                    builder.AppendLine(
                        $"    :{site.Line} {site.SourceText(entry.ReferenceKind, fallback)}"
                        + ConfidenceMark(site.Confidence));
                }
            }
        }

        // The cap used to be announced without a size, and tested by comparing the list
        // length against it. Both were wrong in the same direction. A reader could not tell
        // 101 from 1,010 without leaving the tool, and the test misses a truncation whenever
        // the cut rows would have collapsed into entries already listed — the LIMIT applies
        // to rows, the list holds one entry per caller and reference kind. The total is
        // counted in SQL over the same predicate, so it answers both.
        if (total > related.Count)
        {
            builder.AppendLine(
                $"  … showing {related.Count} of {total:n0}, capped at {listCap} per direction");
        }
        else if (related.Count >= listCap)
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

    /// <summary>
    /// The imperfect-parse list, led by a tally of what the parser actually stopped at.
    /// <para>
    /// The tally comes first because it is usually the whole answer. A list of fifty file
    /// names invites the reader to conclude their workspace is in bad shape; the same fifty
    /// files rolled up to one line saying every one of them stopped at <c>[]</c> says the
    /// true thing instead, which is that the bundled grammar is older than the code.
    /// </para>
    /// </summary>
    public static string ParseErrors(ParseErrorReport report, int limit)
    {
        if (report.Files.Count == 0)
        {
            return $"all {report.TotalFiles} indexed files parsed cleanly";
        }

        var builder = new StringBuilder();
        builder.AppendLine(
            $"{report.Files.Count} of {report.TotalFiles} indexed files hold something the parser could not read");

        builder.AppendLine();
        builder.AppendLine("what it stopped at:");
        foreach (var group in report.Files
            .GroupBy(f => (f.Language, Text: f.Text ?? "(a token the grammar expected and did not find)"))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Text, StringComparer.Ordinal))
        {
            builder.AppendLine($"  {group.Count(),4} × {group.Key.Language,-6} {Flatten(group.Key.Text)}");
        }

        var shown = 0;
        foreach (var language in report.Files
            .GroupBy(f => f.Language)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            builder.AppendLine();
            builder.AppendLine($"{language.Key} ({language.Count()} files)");

            foreach (var file in language)
            {
                if (shown++ == limit)
                {
                    builder.AppendLine($"  … {report.Files.Count - limit} more files not shown (--limit)");
                    break;
                }

                // A hard failure indexed nothing and has a message; a recovered parse
                // indexed what it could and has a position. Never both.
                var where = file.Line is { } line ? $":{line}" : string.Empty;
                var what = file.Message is { } message
                    ? $"  {Flatten(message)}"
                    : $"  {file.SymbolCount} indexed";

                // "N indexed" alone is the gap that hid a swallow for nine rounds: the
                // count says what survived and nothing about reach. The construct's extent
                // is the honest proxy for what may not have — a span to the last line
                // means everything after the error was consumed as its body.
                var reach = file is { Line: { } from, EndLine: { } to } && to > from
                    ? $" — the construct it stopped in runs to line {to}"
                    : string.Empty;

                builder.AppendLine($"  {file.RelativePath}{where}{what}{reach}");
            }
        }

        builder.AppendLine();
        builder.AppendLine(
            "every file above was still indexed — the count is what survived. A file lands here when "
            + "the grammar cannot read one construct, which is more often a language feature newer "
            + "than the bundled grammar than a mistake in the file.");

        return builder.Finish();
    }

    /// <summary>
    /// The index's aggregate self-portrait. The resolution triple is the reason the block
    /// exists — the tool could always say who calls one symbol and could not say how well
    /// it was resolving anything — and the closing note keeps the biggest number honest:
    /// an unresolved reference is usually a name defined outside the workspace, not a miss.
    /// </summary>
    public static string Stats(IndexStats stats)
    {
        var builder = new StringBuilder();

        if (stats.ScopePath is not null)
        {
            builder.AppendLine($"scope: {stats.ScopePath} "
                + "(every count below is limited to this file or subtree)");
        }

        builder.AppendLine($"files: {stats.TotalFiles} ({Tally(stats.FilesByLanguage)})"
            + (stats.ImperfectFiles == 0
                ? string.Empty
                : $" · {stats.ImperfectFiles} imperfect parses (see: errors)"));

        builder.AppendLine($"symbols: {stats.TotalSymbols:n0} ({Tally(stats.SymbolsByKind)})");

        builder.AppendLine($"references: {stats.TotalRefs:n0} · "
            + $"{stats.RefsWithReceiver:n0} carry a receiver ({Percent(stats.RefsWithReceiver, stats.TotalRefs)}) · "
            + $"{stats.RefsWithArgs:n0} carry arguments ({Percent(stats.RefsWithArgs, stats.TotalRefs)})");

        builder.AppendLine("resolution, per reference:");
        builder.AppendLine($"  resolved uniquely  {stats.RefsResolvedUniquely,9:n0}  {Percent(stats.RefsResolvedUniquely, stats.TotalRefs),6}");
        builder.AppendLine($"  ambiguous          {stats.RefsAmbiguous,9:n0}  {Percent(stats.RefsAmbiguous, stats.TotalRefs),6}"
            + (stats.RefsAmbiguous == 0 ? string.Empty : $"  ({stats.MeanCandidatesWhenAmbiguous:0.0} candidates each)"));
        builder.AppendLine($"  unresolved         {stats.RefsUnresolved,9:n0}  {Percent(stats.RefsUnresolved, stats.TotalRefs),6}");

        AppendSplits(builder, "by reference kind", stats.RefsByKind);
        AppendSplits(builder, "by language", stats.RefsByLanguage, stats.UnresolvedByRulePerLanguage);
        AppendRefusals(builder, stats);

        builder.AppendLine($"edges: {stats.TotalEdges:n0} ({Tally(stats.EdgesByConfidence)})");
        if (stats.RefsOnlyCrossLanguage > 0)
        {
            builder.AppendLine(
                $"  {stats.RefsOnlyCrossLanguage:n0} references resolve only by a cross-language "
                + "name match — listings mark those '?'");
        }

        // "file dependencies", not "imports": the rows behind this line include C's
        // #include lines, which the old label miscounted as imports. And the count is of
        // *dependencies*, which a pack may deduplicate (JavaScript records one row for a
        // module required twice), so when it differs from the Include+Import reference
        // total the line says how, rather than leaving the two tables to disagree quietly.
        var dependencyRefs = stats.RefsByKind
            .Where(s => s.Name is nameof(Core.Domain.ReferenceKind.Include)
                or nameof(Core.Domain.ReferenceKind.Import))
            .Sum(s => s.Total);
        var reconciliation = dependencyRefs == stats.TotalDeps || dependencyRefs == 0
            ? string.Empty
            : $" ({dependencyRefs:n0} include/import references, deduplicated)";
        builder.AppendLine(
            $"file dependencies: {stats.ResolvedDeps:n0} of {stats.TotalDeps:n0} "
            + $"name a workspace file{reconciliation}");
        builder.AppendLine($"database: {stats.DatabaseBytes / 1024.0 / 1024.0:0.0} MB");

        builder.AppendLine();
        builder.AppendLine("an unresolved reference is one no workspace definition matched — for a workspace "
            + "that leans on external libraries that is the normal shape, not a defect count. "
            + "the external share says how much of a row's unresolved names nothing any workspace "
            + "definition of a compatible kind carries — those are correct, not gaps. a lower bound: "
            + "the residue is where a real gap would hide, not proof of one. each language row also "
            + "names whichever rule below takes most of what external leaves; --json carries that "
            + "split in full.");

        return builder.Finish();
    }

    /// <summary>
    /// The unresolved column, split by the rule that refused each reference. The wording is
    /// the rule, not the symptom, because an unresolved reference looks identical whether it
    /// was refused correctly or by a rule that has quietly stopped working — naming the rule
    /// is what makes the second case checkable.
    /// </summary>
    private static void AppendRefusals(StringBuilder builder, IndexStats stats)
    {
        var rows = stats.UnresolvedByRule;
        if (rows.Count == 0 || rows.Sum(r => r.Count) == 0)
        {
            return;
        }

        // The partition covers what the resolver *attempted*, which is fewer references than
        // the unresolved headline: an include or an import is settled against file_dep and
        // never offered a symbol at all. Saying so on the heading keeps two numbers that are
        // meant to differ from reading as a discrepancy — the same reconciliation the file
        // dependencies line carries.
        var total = rows.Sum(r => r.Count);
        var fileScoped = stats.RefsUnresolved - total;
        var reconciliation = fileScoped <= 0
            ? string.Empty
            : $" (the other {fileScoped:n0} are include/import, settled as file dependencies)";
        builder.AppendLine($"why the {total:n0} unresolved were refused{reconciliation}:");
        foreach (var row in rows)
        {
            // Every rule prints, including the zeroes: a rule showing 0 is the answer to a
            // question that was asked, and dropping it would read as never having asked.
            builder.AppendLine(
                $"  {RefusalText(row.Rule).PadRight(52)}  {row.Count,8:n0}  {Percent(row.Count, total),6}");
        }
    }

    private static string RefusalText(UnresolvedRule rule) => rule switch
    {
        UnresolvedRule.External => "no workspace definition of a compatible kind",
        UnresolvedRule.ReceiverUnknown => "receiver names nothing this workspace declares",
        UnresolvedRule.TooCommon => "name too common to guess, and no receiver given",
        UnresolvedRule.ReceiverNotTyped => "receiver named no type holding that member",
        UnresolvedRule.OutOfScope => "a local, or a member of a scope not written in",
        UnresolvedRule.Unexplained => "refused by no rule above — a gap in this partition",
        _ => rule.ToString(),
    };

    /// <summary>
    /// One resolution table. The columns repeat the whole-index triple for a subset, which
    /// is the point: a kind or a language that resolves far below the index average is the
    /// shape of a resolver gap, and reading it off a table beats guessing at it.
    /// </summary>
    private static void AppendSplits(
        StringBuilder builder,
        string heading,
        IReadOnlyList<ResolutionSplit> splits,
        IReadOnlyList<LanguageRefusals>? refusals = null)
    {
        if (splits.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{heading}:");
        var width = splits.Max(s => s.Name.Length);
        foreach (var split in splits)
        {
            // The trailing share reads the unresolved column so "unres 70.0% · 100% external"
            // says, in one line, that the row is describing the workspace's dependence on
            // outside code rather than naming a resolver gap.
            var external = split.Unresolved == 0
                ? string.Empty
                : $"  · {Percent(split.External, split.Unresolved),6} external";

            // And the second share names whichever rule takes most of what is left, which is
            // where the languages stop looking alike: measured here, JavaScript's residue is
            // 39.7% the container rule against C#'s 10.9%. Sharing the unresolved denominator
            // with the external clause is deliberate — two shares on one line reading against
            // two different totals is a trap, and the partition's own total differs from it by
            // the include/import references it does not cover.
            var largest = refusals
                ?.FirstOrDefault(r => r.Language == split.Name)
                ?.LargestBesidesExternal;
            var runnerUp = largest is null || split.Unresolved == 0
                ? string.Empty
                : $"  · {Percent(largest.Count, split.Unresolved),6} {RefusalToken(largest.Rule)}";

            builder.AppendLine(
                $"  {split.Name.PadRight(width)}  {split.Total,8:n0}  "
                + $"uniq {Percent(split.Unique, split.Total),6}  "
                + $"amb {Percent(split.Ambiguous, split.Total),6}  "
                + $"unres {Percent(split.Unresolved, split.Total),6}{external}{runnerUp}");
        }
    }

    /// <summary>
    /// A rule in two or three words, for the one place it has to share a line. The full
    /// sentence stays on the partition block, where there is room to say what the rule is
    /// rather than merely name it.
    /// </summary>
    private static string RefusalToken(UnresolvedRule rule) => rule switch
    {
        UnresolvedRule.External => "external",
        UnresolvedRule.ReceiverUnknown => "unknown receiver",
        UnresolvedRule.TooCommon => "too common",
        UnresolvedRule.ReceiverNotTyped => "untyped receiver",
        UnresolvedRule.OutOfScope => "out of scope",
        UnresolvedRule.Unexplained => "unexplained",
        _ => rule.ToString(),
    };

    /// <summary>
    /// How many of a caller or callee list are held together by nothing but a name matching
    /// across a language boundary — the edges the listing marks '?'. Silent when there are
    /// none, so the ordinary case reads exactly as it did.
    /// </summary>
    private static string CrossLanguageNote(IReadOnlyList<RelatedSymbol> related)
    {
        var weak = related.Count(r => r.Confidence == EdgeConfidence.Weak);
        return weak == 0 ? string.Empty : $" ({weak} cross-language name match{(weak == 1 ? "" : "es")})";
    }

    private static string Tally(IReadOnlyList<NamedCount> counts) =>
        string.Join(" · ", counts.Select(c => $"{c.Name} {c.Count:n0}"));

    private static string Percent(int part, int whole) =>
        whole == 0 ? "0%" : $"{100.0 * part / whole:0.0}%";

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
