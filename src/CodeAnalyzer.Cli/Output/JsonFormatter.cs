using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Cli.Session;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Search;

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

    public static string Search(ReadOnlyIndexSession session, string query, IReadOnlyList<SymbolSearchHit> hits) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            query,
            hits = hits.Select(Hit),
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

    public static string Detail(ReadOnlyIndexSession session, SymbolDetail detail) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            symbol = new
            {
                id = detail.Id,
                name = detail.Name,
                kind = KindTokens.For(detail.Kind),
                path = detail.RelativePath,
                startLine = detail.StartLine,
                endLine = detail.EndLine,
                language = detail.Language,
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
                callers = detail.Callers.Select(Related),
                callees = detail.Callees.Select(Related),
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
        IReadOnlyDictionary<long, List<EdgeCallSite>>? sites) =>
        JsonSerializer.Serialize(new
        {
            index = Index(session),
            focus = Located(focus),
            direction,
            listCap,
            listCapped = related.Count >= listCap,
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
                        arguments = s.ArgumentText,
                        confidence = KindLabels.TokenFor(s.Confidence),
                    })
                    : null,
            }),
        }, Options);

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
