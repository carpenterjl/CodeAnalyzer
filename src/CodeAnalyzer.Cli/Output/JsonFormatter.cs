using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Search;
using CodeAnalyzer.Core.Storage;

namespace CodeAnalyzer.Cli.Output;

/// <summary>
/// The <c>--json</c> mirror of every terse result, for scripts. Enums travel as the same
/// stable tokens the GUI's payloads use (<see cref="KindTokens"/>,
/// <see cref="KindLabels.TokenFor"/>), never as raw integers, and every document carries
/// the index's provenance so a consumer always knows how old its facts are.
/// </summary>
internal static class JsonFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static object Index(ReadOnlyIndexSession session) => new
    {
        root = session.RootPath,
        symbols = session.DefinitionCount,
        builtUtc = session.LastIndexUtc,
    };

    private static object Hit(SymbolSearchHit hit) => new
    {
        id = hit.SymbolId,
        name = hit.Name,
        container = hit.ContainerName,
        kind = KindTokens.For(hit.Kind),
        parameters = hit.ParameterText,
        path = hit.RelativePath,
        line = hit.Line,
        descriptor = hit.Descriptor,
        looseMatch = hit.LooseMatch,
        // Null unless it was asked for, or unless the comment is why this hit is here.
        comment = hit.DocComment,
    };

    private static object Located(LocatedSymbol symbol) => new
    {
        id = symbol.Id,
        name = symbol.Name,
        kind = KindTokens.For(symbol.Kind),
        parameters = symbol.ParameterText,
        path = symbol.RelativePath,
        line = symbol.Line,
    };

    private static object Related(RelatedSymbol related) => new
    {
        id = related.Id,
        name = related.Name,
        kind = KindTokens.For(related.Kind),
        path = related.RelativePath,
        line = related.Line,
        reference = KindLabels.For(related.ReferenceKind),
        confidence = KindLabels.TokenFor(related.Confidence),
    };

    public static string Search(
        ReadOnlyIndexSession session,
        string query,
        IReadOnlyList<SymbolSearchHit> hits,
        IReadOnlyList<SymbolSearchHit>? commentRescue = null,
        IReadOnlyList<SymbolKind>? kindsThisIndexHasNoneOf = null) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            query,
            hits = hits.Select(Hit),
            // Always present, so a consumer can tell "the second search found nothing" from
            // "the second search was not run" without inspecting the first list.
            commentRescue = (commentRescue ?? []).Select(Hit),
            // Non-empty only when the kind filter, not the query, is what emptied the list.
            kindsThisIndexHasNoneOf =
                (kindsThisIndexHasNoneOf ?? []).Select(KindTokens.For),
        }, Options);

    public static string Locate(ReadOnlyIndexSession session, LocateResult result) => result switch
    {
        LocateResult.Resolved resolved => JsonSerializer.Serialize(new
        {
            index = Index(session),
            resolved = Located(resolved.Symbol),
        }, Options),
        LocateResult.Ambiguous ambiguous => JsonSerializer.Serialize(new
        {
            index = Index(session),
            ambiguous = true,
            candidates = ambiguous.Candidates.Select(Located),
            candidateListCapped = ambiguous.More,
        }, Options),
        LocateResult.NotFound notFound => JsonSerializer.Serialize(new
        {
            index = Index(session),
            notFound = notFound.Message,
            suggestions = notFound.Suggestions.Select(Located),
        }, Options),
        _ => throw new InvalidOperationException(),
    };

    public static string Detail(
        ReadOnlyIndexSession session,
        SymbolDetail detail,
        ValueMatchSet? sameValue = null) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            sameValue = sameValue is null ? null : ValueSet(sameValue),
            symbol = new
            {
                id = detail.Id,
                name = detail.Name,
                kind = KindTokens.For(detail.Kind),
                path = detail.RelativePath,
                startLine = detail.StartLine,
                endLine = detail.EndLine,
                language = detail.Language,
                comment = detail.DocComment,
                signature = detail.Signature,
                modifiers = detail.Modifiers,
                type = detail.TypeText,
                value = detail.Value,
                parameters = detail.ParameterText,
                members = detail.Members.Select(m => new
                {
                    id = m.Id,
                    name = m.Name,
                    kind = KindTokens.For(m.Kind),
                    type = m.TypeText,
                    value = m.Value,
                    line = m.Line,
                    modifiers = m.Modifiers,
                }),
                overloads = detail.Overloads.Select(o => new
                {
                    id = o.Id,
                    parameters = o.ParameterText,
                    line = o.Line,
                    isCurrent = o.IsCurrent,
                }),
                derivesFrom = detail.BaseTypes.Select(b => new
                {
                    name = b.Name,
                    id = b.TargetId,
                    path = b.TargetPath,
                    line = b.TargetLine,
                    inWorkspace = b.TargetId is not null,
                }),
                derivedBy = detail.DerivedTypes.Select(Related),
                callers = detail.Callers.Select(Related),
                callees = detail.Callees.Select(Related),

                // Always emitted, 0 included, so a consumer can tell "its members are
                // reached from nowhere either" from "this build did not look".
                memberCallers = detail.MemberCallerTotal,
                unresolved = detail.UnresolvedReferences.Select(u => new
                {
                    name = u.Name,
                    reference = KindLabels.For(u.Kind),
                    line = u.Line,
                }),
            },
        }, Options);

    public static string RelatedList(
        ReadOnlyIndexSession session,
        LocatedSymbol focus,
        IReadOnlyList<RelatedSymbol> related,
        string direction,
        int listCap,
        IReadOnlyDictionary<long, List<EdgeCallSite>>? sites,
        int total = 0) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            focus = Located(focus),
            direction,
            listCap,
            // listCapped was computed from the list's own length, which cannot see a
            // truncation whose cut rows would have collapsed into entries already listed.
            // relatedTotal is counted over the same predicate as the listing.
            relatedTotal = total,
            listCapped = total > related.Count || related.Count >= listCap,
            related = related.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                kind = KindTokens.For(r.Kind),
                path = r.RelativePath,
                line = r.Line,
                reference = KindLabels.For(r.ReferenceKind),
                confidence = KindLabels.TokenFor(r.Confidence),
                sites = sites is not null && sites.TryGetValue(r.Id, out var entrySites)
                    ? entrySites.Select(s => new
                    {
                        line = s.Line,
                        receiver = s.ReceiverText,
                        // The name as written at the site, which a x:Class reference
                        // spells shorter than the declaration it resolves to.
                        name = s.Name,
                        arguments = s.ArgumentText,
                        confidence = KindLabels.TokenFor(s.Confidence),
                    })
                    : null,
            }),
        }, Options);

    public static string Flow(ReadOnlyIndexSession session, LocatedSymbol root, CallFlow flow)
    {
        object StepJson(CallFlowStep step) => new
        {
            ordinal = step.Ordinal,
            refId = step.RefId,
            name = step.Name,
            receiver = step.ReceiverText,
            args = step.ArgumentText,
            reference = KindLabels.For(step.Kind),
            line = step.Line,
            col = step.Col,
            fate = step.Fate is { } f ? KindLabels.TokenFor(f) : null,
            fateName = step.FateName,
            target = step.TargetId is { } id
                ? new
                {
                    id,
                    name = step.TargetName,
                    kind = KindTokens.For(step.TargetKind),
                    path = step.TargetPath,
                    line = step.TargetLine,
                }
                : null,
            confidence = KindLabels.TokenFor(step.Confidence),
            candidates = step.OtherCandidates.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                kind = KindTokens.For(c.Kind),
                path = c.RelativePath,
                line = c.Line,
            }),
            cycle = step.IsCycle,
            unresolved = step.IsUnresolved,
            collapsedAt = step.CollapsedAt,
            io = step.IsIoBoundary
                ? new { direction = IoDirectionLabels.For(step.IoDirection), family = step.IoFamily }
                : null,
            childrenTruncated = step.ChildrenTruncated,
            callSitesInBody = step.CallSitesInBody,
            steps = step.Children.Select(StepJson),
        };

        return JsonSerializer.Serialize(new
        {
            index = Index(session),
            root = Located(root),
            depth = flow.DepthUsed,
            totalSteps = flow.TotalSteps,
            rootTruncated = flow.RootTruncated,
            rootCallSites = flow.RootCallSites,
            truncated = flow.WasTruncated,
            steps = flow.Steps.Select(StepJson),
        }, Options);
    }

    public static string Trace(
        ReadOnlyIndexSession session, LocatedSymbol from, LocatedSymbol to, PathTrace trace) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            from = Located(from),
            to = Located(to),
            hops = trace.Length,
            routes = trace.Routes,
            nodes = trace.Nodes.Select(n => new
            {
                id = n.Id,
                name = n.Name,
                kind = KindTokens.For(n.Kind),
                path = n.RelativePath,
                line = n.Line,
            }),
            links = trace.Links.Select(l => new
            {
                source = l.SourceId,
                target = l.TargetId,
                reference = KindLabels.For(l.Kind),
                confidence = KindLabels.TokenFor(l.Confidence),
                line = l.Line,
            }),
            searchExhausted = trace.SearchExhausted,
            routesTruncated = trace.Truncated,
        }, Options);

    public static string RepoMap(ReadOnlyIndexSession session, RepoMap map, int emitted) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            ranking = "distinct incoming references over resolved edges",
            entries = map.Entries.Take(emitted).Select(e => new
            {
                id = e.Id,
                name = e.Name,
                container = e.ContainerName,
                kind = KindTokens.For(e.Kind),
                parameters = e.ParameterText,
                path = e.RelativePath,
                line = e.Line,
                fanIn = e.FanIn,
            }),
            rankedTotal = map.Entries.Count,
            truncated = emitted < map.Entries.Count,
            rankingCapHit = map.FetchCapHit,
            filesBySymbolCount = map.Entries.Count == 0
                ? map.FilesBySymbolCount.Select(f => new { path = f.RelativePath, symbols = f.Symbols })
                : null,
        }, Options);

    public static string Outline(ReadOnlyIndexSession session, FileOutline outline) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            path = outline.RelativePath,
            language = outline.CandidatePaths is null ? outline.Language : null,
            ambiguousMatches = outline.CandidatePaths,
            definitions = outline.CandidatePaths is null
                ? outline.Entries.Select(e => new
                {
                    id = e.Id,
                    name = e.Name,
                    kind = KindTokens.For(e.Kind),
                    parameters = e.ParameterText,
                    type = e.TypeText,
                    value = e.Value,
                    modifiers = e.Modifiers,
                    line = e.Line,
                    depth = e.Depth,
                })
                : null,
        }, Options);

    /// <summary>
    /// A value query's answer. <c>matched: false</c> is the "that was not a literal" case —
    /// a different fact from an empty match list, and a script has to tell them apart.
    /// </summary>
    public static string Values(ReadOnlyIndexSession session, string literal, ValueMatchSet? set) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            query = literal,
            matched = set is not null,
            values = set is null ? null : ValueSet(set),
        }, Options);

    public static string SharedValues(
        ReadOnlyIndexSession session,
        IReadOnlyList<ValueGroup> groups,
        bool acrossDirectories,
        bool includeTrivial) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            acrossDirectories,
            includeTrivial,
            note = "a shared value is evidence of an agreement between two definitions, not proof of one",
            values = groups.Select(g => new
            {
                value = g.Canonical,
                total = g.TotalCount,
                languages = g.Languages,
                directories = g.TopDirectories,
                members = g.Members.Select(m => new
                {
                    id = m.SymbolId,
                    name = m.Name,
                    container = m.ContainerName,
                    kind = KindTokens.For(m.Kind),
                    language = m.Language,
                    verbatim = m.Verbatim,
                    path = m.RelativePath,
                    line = m.Line,
                }),
            }),
        }, Options);

    private static object ValueSet(ValueMatchSet set) => new
    {
        value = set.Canonical,
        truncated = set.Truncated,
        limit = set.Limit,
        languages = set.OtherLanguages,
        matches = set.Matches.Select(m => new
        {
            id = m.SymbolId,
            name = m.Name,
            container = m.ContainerName,
            kind = KindTokens.For(m.Kind),
            language = m.Language,
            verbatim = m.Verbatim,
            note = m.EqualityNote,
            path = m.RelativePath,
            line = m.Line,
        }),
        argumentSitesTruncated = set.ArgumentSitesTruncated,
        argumentSites = set.ArgumentSites.Select(a => new
        {
            callee = a.CalleeName,
            arguments = a.ArgumentText,
            owner = a.OwnerName,
            path = a.RelativePath,
            line = a.Line,
        }),
    };

    public static string ParseErrors(ReadOnlyIndexSession session, ParseErrorReport report) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            totalFiles = report.TotalFiles,
            imperfectFiles = report.Files.Count,
            swallowedFiles = report.Files.Count(f => f.ConsumedTheRestOfTheFile),
            note = "every file listed was still indexed; symbols is what survived. A null message "
                + "means the parser recovered, and stoppedAt is where it lost its footing. "
                + "endLine is the last line of the construct it stopped in, against lineCount "
                + "for the file. consumedRestOfFile is the case worth acting on: the construct "
                + "never ended, so the lines after the error were read as its body and whatever "
                + "they declared is absent from the index rather than merely flagged.",
            files = report.Files.Select(f => new
            {
                path = f.RelativePath,
                language = f.Language,
                line = f.Line,
                endLine = f.EndLine,
                lineCount = f.LineCount,
                consumedRestOfFile = f.ConsumedTheRestOfTheFile,
                stoppedAt = f.Text,
                symbols = f.SymbolCount,
                message = f.Message,
            }),
        }, Options);

    private static object Split(ResolutionSplit split) => new
    {
        name = split.Name,
        total = split.Total,
        resolvedUniquely = split.Unique,
        ambiguous = split.Ambiguous,
        unresolved = split.Unresolved,
        // Of the unresolved, how many name nothing any compatible workspace definition
        // carries — the correctly-external share, as a lower bound.
        unresolvedExternal = split.External,
    };

    public static string Stats(ReadOnlyIndexSession session, IndexStats stats) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            scope = stats.ScopePath,
            files = new
            {
                total = stats.TotalFiles,
                byLanguage = stats.FilesByLanguage.ToDictionary(c => c.Name, c => c.Count),
                imperfectParses = stats.ImperfectFiles,
            },
            symbols = new
            {
                total = stats.TotalSymbols,
                byKind = stats.SymbolsByKind.ToDictionary(c => c.Name, c => c.Count),
            },
            references = new
            {
                total = stats.TotalRefs,
                withReceiver = stats.RefsWithReceiver,
                withArguments = stats.RefsWithArgs,
                resolvedUniquely = stats.RefsResolvedUniquely,
                ambiguous = stats.RefsAmbiguous,
                unresolved = stats.RefsUnresolved,
                meanCandidatesWhenAmbiguous = Math.Round(stats.MeanCandidatesWhenAmbiguous, 2),
                note = "unresolved counts references no workspace definition matched, which "
                    + "includes every call into an external library",
                byKind = stats.RefsByKind.Select(Split),
                byLanguage = stats.RefsByLanguage.Select(Split),
                // The unresolved column split by the rule that refused each reference. The
                // four rules are exhaustive by construction, so `unexplained` is expected to
                // read 0 — a non-zero value there is a gap in the partition itself, which is
                // why it is emitted rather than filtered out for being empty.
                unresolvedByRule = stats.UnresolvedByRule
                    .ToDictionary(r => r.Rule.ToString(), r => r.Count),
                // And the same partition per language. The terse block prints one figure per
                // language because it has a line budget; this has none, so the whole matrix
                // goes here — the two are folded from one query, so they agree by
                // construction rather than by both being correct.
                unresolvedByRulePerLanguage = stats.UnresolvedByRulePerLanguage
                    .ToDictionary(
                        l => l.Language,
                        l => l.ByRule.ToDictionary(r => r.Rule.ToString(), r => r.Count)),
            },
            edges = new
            {
                total = stats.TotalEdges,
                byConfidence = stats.EdgesByConfidence.ToDictionary(c => c.Name, c => c.Count),
                referencesRestingOnlyOnACrossLanguageNameMatch = stats.RefsOnlyCrossLanguage,
            },
            // Renamed from "imports" in M25.3: these are file dependencies — C includes
            // as much as imports — and a pack may deduplicate them, so the count is of
            // distinct dependencies rather than of include/import references.
            fileDependencies = new { total = stats.TotalDeps, resolvedToWorkspaceFile = stats.ResolvedDeps },
            databaseBytes = stats.DatabaseBytes,
        }, Options);

    public static string Boundaries(ReadOnlyIndexSession session, IReadOnlyList<IoBoundarySite> sites) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            directionNote = "direction comes from the API's documentation or a user mark, never from syntax",
            sites = sites.Select(s => new
            {
                name = s.Name,
                direction = IoDirectionLabels.TokenFor(s.Direction),
                path = s.RelativePath,
                line = s.Line,
                caller = s.CallerName,
                callerId = s.CallerSymbolId,
                source = s.Origin == IoMatchOrigin.UserMark ? "your mark" : $"catalog: {s.Family}",
                gate = s.GateNote,
                arguments = s.ArgumentText,
            }),
        }, Options);
}
