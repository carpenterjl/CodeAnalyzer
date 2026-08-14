using System.Text.Json.Serialization;
using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

// Wire shapes for the four M6 views. Same rules as GraphPayload: ids are strings because
// JavaScript numbers cannot hold the 64-bit range, kind labels come from KindLabels so the
// XAML pane and the page cannot drift apart, and anything the resolver was unsure about
// keeps its uncertainty all the way to the screen.

// ---- Composition inspector -------------------------------------------------

public sealed record CompositionMemberPayload
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("group")] public required string Group { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("line")] public int Line { get; init; }

    /// <summary>Verbatim modifier keywords, so the card can badge visibility/override.</summary>
    [JsonPropertyName("modifiers")] public string? Modifiers { get; init; }

    /// <summary>Members of its own, so the card can offer to drill in.</summary>
    [JsonPropertyName("members")] public int Members { get; init; }

    [JsonPropertyName("references")] public int References { get; init; }
}

public sealed record CompositionLinkPayload
{
    /// <summary>Null when the name resolved to nothing indexed here.</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("line")] public int Line { get; init; }
    [JsonPropertyName("confidence")] public string? Confidence { get; init; }
    [JsonPropertyName("confidenceLabel")] public string? ConfidenceLabel { get; init; }
}

public sealed record CompositionPayload
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("group")] public required string Group { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("line")] public int Line { get; init; }
    [JsonPropertyName("endLine")] public int EndLine { get; init; }
    [JsonPropertyName("language")] public required string Language { get; init; }
    [JsonPropertyName("signature")] public string? Signature { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("modifiers")] public string? Modifiers { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("containerId")] public string? ContainerId { get; init; }
    [JsonPropertyName("containerName")] public string? ContainerName { get; init; }

    [JsonPropertyName("members")] public IReadOnlyList<CompositionMemberPayload> Members { get; init; } = [];
    [JsonPropertyName("instantiates")] public IReadOnlyList<CompositionLinkPayload> Instantiates { get; init; } = [];
    [JsonPropertyName("instantiatedBy")] public IReadOnlyList<CompositionLinkPayload> InstantiatedBy { get; init; } = [];
    [JsonPropertyName("baseTypes")] public IReadOnlyList<CompositionLinkPayload> BaseTypes { get; init; } = [];
    [JsonPropertyName("derivedTypes")] public IReadOnlyList<CompositionLinkPayload> DerivedTypes { get; init; } = [];
}

// ---- Path tracer -----------------------------------------------------------

public sealed record PathNodePayload
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("group")] public required string Group { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("line")] public int Line { get; init; }

    /// <summary>0 for the start, Length for the destination: the dagre rank.</summary>
    [JsonPropertyName("step")] public int Step { get; init; }
}

public sealed record PathLinkPayload
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("target")] public required string Target { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }
    [JsonPropertyName("confidenceLabel")] public required string ConfidenceLabel { get; init; }
    [JsonPropertyName("line")] public int Line { get; init; }
}

public sealed record PathRoutePayload
{
    [JsonPropertyName("nodes")] public required IReadOnlyList<string> Nodes { get; init; }

    /// <summary>Weakest confidence anywhere on the route. A chain is no stronger than that.</summary>
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }
}

public sealed record PathsPayload
{
    [JsonPropertyName("fromId")] public required string FromId { get; init; }
    [JsonPropertyName("toId")] public required string ToId { get; init; }
    [JsonPropertyName("fromName")] public string? FromName { get; init; }
    [JsonPropertyName("toName")] public string? ToName { get; init; }
    [JsonPropertyName("nodes")] public IReadOnlyList<PathNodePayload> Nodes { get; init; } = [];
    [JsonPropertyName("links")] public IReadOnlyList<PathLinkPayload> Links { get; init; } = [];
    [JsonPropertyName("routes")] public IReadOnlyList<PathRoutePayload> Routes { get; init; } = [];
    [JsonPropertyName("length")] public int Length { get; init; }

    /// <summary>True when a budget stopped the search, so "no route" is not claimed.</summary>
    [JsonPropertyName("exhausted")] public bool Exhausted { get; init; }

    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("missingEndpoint")] public bool MissingEndpoint { get; init; }
}

// ---- Treemap ---------------------------------------------------------------

public sealed record TreemapTilePayload
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }

    /// <summary>"dir", "file" or "symbol".</summary>
    [JsonPropertyName("type")] public required string Type { get; init; }

    [JsonPropertyName("value")] public long Value { get; init; }
    [JsonPropertyName("files")] public int Files { get; init; }
    [JsonPropertyName("symbols")] public int Symbols { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("internalLinks")] public int InternalLinks { get; init; }
    [JsonPropertyName("outgoingLinks")] public int OutgoingLinks { get; init; }

    /// <summary>Set only on symbol tiles, so clicking one can focus the graph.</summary>
    [JsonPropertyName("symbolId")] public string? SymbolId { get; init; }

    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("group")] public string? Group { get; init; }
}

public sealed record TreemapPayload
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("childType")] public required string ChildType { get; init; }
    [JsonPropertyName("tiles")] public IReadOnlyList<TreemapTilePayload> Tiles { get; init; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
}

// ---- Dependency wheel ------------------------------------------------------

public sealed record WheelGroupPayload
{
    /// <summary>The stored top-level directory, empty for files at the workspace root.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("files")] public int Files { get; init; }
    [JsonPropertyName("links")] public int Links { get; init; }
    [JsonPropertyName("unresolved")] public int Unresolved { get; init; }
}

public sealed record WheelLinkPayload
{
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("target")] public required string Target { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
}

public sealed record WheelPayload
{
    /// <summary>"includes" or "references".</summary>
    [JsonPropertyName("source")] public required string Source { get; init; }

    /// <summary>Plain-language statement of what a ribbon counts.</summary>
    [JsonPropertyName("sourceLabel")] public required string SourceLabel { get; init; }

    [JsonPropertyName("groups")] public IReadOnlyList<WheelGroupPayload> Groups { get; init; } = [];
    [JsonPropertyName("links")] public IReadOnlyList<WheelLinkPayload> Links { get; init; } = [];
    [JsonPropertyName("omittedGroups")] public int OmittedGroups { get; init; }
}

// ---- Builders --------------------------------------------------------------

/// <summary>
/// Projects the M6 query results onto their wire shapes. Kept out of the WPF layer so the
/// projection can be tested directly, like <see cref="GraphPayloadBuilder"/>.
/// </summary>
public static class ViewPayloadBuilder
{
    /// <summary>Label for the workspace root, which is stored as an empty directory name.</summary>
    public const string RootGroupLabel = "(root)";

    public static CompositionPayload Build(CompositionView view) => new()
    {
        Id = view.Id.ToString(),
        Name = view.Name,
        Kind = KindLabels.For(view.Kind),
        Group = KindLabels.GroupFor(view.Kind),
        Path = view.RelativePath,
        Line = view.StartLine,
        EndLine = view.EndLine,
        Language = view.Language,
        Signature = view.Signature,
        Value = view.Value,
        Type = view.TypeText,
        Modifiers = view.Modifiers,
        Scope = string.IsNullOrEmpty(view.ScopePath) ? null : view.ScopePath,
        ContainerId = view.ContainerId?.ToString(),
        ContainerName = view.ContainerName,
        Members = view.Members.Select(member => new CompositionMemberPayload
        {
            Id = member.Id.ToString(),
            Name = member.Name,
            Kind = KindLabels.For(member.Kind),
            Group = KindLabels.GroupFor(member.Kind),
            Type = member.TypeText,
            Value = member.Value,
            Line = member.Line,
            Modifiers = member.Modifiers,
            Members = member.MemberCount,
            References = member.ReferenceCount,
        }).ToList(),
        Instantiates = BuildLinks(view.Instantiates),
        InstantiatedBy = BuildLinks(view.InstantiatedBy),
        BaseTypes = BuildLinks(view.BaseTypes),
        DerivedTypes = BuildLinks(view.DerivedTypes),
    };

    private static List<CompositionLinkPayload> BuildLinks(IReadOnlyList<CompositionLink> links) =>
        links.Select(link => new CompositionLinkPayload
        {
            Id = link.TargetId?.ToString(),
            Name = link.Name,
            Kind = link.Kind is null ? null : KindLabels.For(link.Kind.Value),
            Path = link.RelativePath,
            Line = link.Line,
            Confidence = link.Confidence is null ? null : KindLabels.TokenFor(link.Confidence.Value),
            ConfidenceLabel = link.Confidence is null ? null : KindLabels.For(link.Confidence.Value),
        }).ToList();

    public static PathsPayload Build(PathTrace trace)
    {
        var step = new Dictionary<long, int>();
        foreach (var route in trace.Routes)
        {
            for (var i = 0; i < route.Count; i++)
            {
                // A node can sit at different depths on different routes. Keep the earliest
                // so the layered layout never draws an edge pointing backwards.
                if (!step.TryGetValue(route[i], out var existing) || i < existing)
                {
                    step[route[i]] = i;
                }
            }
        }

        var confidenceByPair = trace.Links.ToDictionary(
            link => (link.SourceId, link.TargetId),
            link => link.Confidence);

        var routes = new List<PathRoutePayload>(trace.Routes.Count);
        foreach (var route in trace.Routes)
        {
            var weakest = EdgeConfidence.Unique;
            for (var i = 0; i + 1 < route.Count; i++)
            {
                if (confidenceByPair.TryGetValue((route[i], route[i + 1]), out var confidence)
                    && confidence > weakest)
                {
                    weakest = confidence;
                }
            }

            routes.Add(new PathRoutePayload
            {
                Nodes = route.Select(id => id.ToString()).ToList(),
                Confidence = KindLabels.TokenFor(weakest),
            });
        }

        var names = trace.Nodes.ToDictionary(node => node.Id, node => node.Name);

        return new PathsPayload
        {
            FromId = trace.FromId.ToString(),
            ToId = trace.ToId.ToString(),
            FromName = names.GetValueOrDefault(trace.FromId),
            ToName = names.GetValueOrDefault(trace.ToId),
            Nodes = trace.Nodes.Select(node => new PathNodePayload
            {
                Id = node.Id.ToString(),
                Name = node.Name,
                Kind = KindLabels.For(node.Kind),
                Group = KindLabels.GroupFor(node.Kind),
                Path = node.RelativePath,
                Line = node.Line,
                Step = step.GetValueOrDefault(node.Id),
            }).ToList(),
            Links = trace.Links.Select(link => new PathLinkPayload
            {
                Id = $"{link.SourceId}-{link.TargetId}",
                Source = link.SourceId.ToString(),
                Target = link.TargetId.ToString(),
                Kind = KindLabels.For(link.Kind),
                Confidence = KindLabels.TokenFor(link.Confidence),
                ConfidenceLabel = KindLabels.For(link.Confidence),
                Line = link.Line,
            }).ToList(),
            Routes = routes,
            Length = trace.Length,
            Exhausted = trace.SearchExhausted,
            Truncated = trace.Truncated,
            MissingEndpoint = !trace.FromExists || !trace.ToExists,
        };
    }

    public static TreemapPayload Build(TreemapLevel level) => new()
    {
        Path = level.Path,
        ChildType = TileTypeToken(level.ChildType),
        Truncated = level.Truncated,
        Tiles = level.Tiles.Select(tile => new TreemapTilePayload
        {
            Name = tile.Name,
            Path = tile.Path,
            Type = TileTypeToken(tile.Type),
            Value = tile.Value,
            Files = tile.Files,
            Symbols = tile.Symbols,
            Bytes = tile.Bytes,
            InternalLinks = tile.InternalLinks,
            OutgoingLinks = tile.OutgoingLinks,
            SymbolId = tile.SymbolId?.ToString(),
            Kind = tile.Kind is null ? null : KindLabels.For(tile.Kind.Value),
            Group = tile.Kind is null ? null : KindLabels.GroupFor(tile.Kind.Value),
        }).ToList(),
    };

    private static string TileTypeToken(TreemapTileType type) => type switch
    {
        TreemapTileType.Directory => "dir",
        TreemapTileType.File => "file",
        _ => "symbol",
    };

    public static WheelPayload Build(DependencyWheel wheel) => new()
    {
        Source = wheel.Source == WheelSource.Includes ? "includes" : "references",
        SourceLabel = wheel.Source == WheelSource.Includes
            ? "each ribbon counts resolved include and import lines"
            : "each ribbon counts resolved symbol references",
        OmittedGroups = wheel.OmittedGroups,
        Groups = wheel.Groups.Select(group => new WheelGroupPayload
        {
            Id = group.Name,
            Label = group.Name.Length == 0 ? RootGroupLabel : group.Name,
            Files = group.Files,
            Links = group.Links,
            Unresolved = group.Unresolved,
        }).ToList(),
        Links = wheel.Links.Select(link => new WheelLinkPayload
        {
            Source = link.Source,
            Target = link.Target,
            Count = link.Count,
        }).ToList(),
    };
}
