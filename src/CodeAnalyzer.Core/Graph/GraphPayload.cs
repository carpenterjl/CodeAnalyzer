using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// One node as the graph page sees it.
/// <para>
/// Ids are strings because the renderer keys elements by string. Every other field is a
/// fact read out of the index; nothing here is inferred or prettified beyond the kind label.
/// </para>
/// </summary>
public sealed record GraphNodePayload
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>Human-readable kind, e.g. "function".</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    /// <summary>Visual family used for colour and shape.</summary>
    [JsonPropertyName("group")] public required string Group { get; init; }

    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("line")] public required int Line { get; init; }

    /// <summary>Literal initializer, shown under the name for constants and macros.</summary>
    [JsonPropertyName("value")] public string? Value { get; init; }

    [JsonPropertyName("signature")] public string? Signature { get; init; }

    /// <summary>
    /// The parameter list as written. Drawn on the node's first line so two overloads of
    /// one method read differently without a click.
    /// </summary>
    [JsonPropertyName("params")] public string? ParameterText { get; init; }

    /// <summary>
    /// What this symbol is, in one line: "public method · overload 2 of 3". Composed here
    /// rather than on the page so the canvas, the detail pane and the search list cannot
    /// word the same facts three different ways.
    /// </summary>
    [JsonPropertyName("descriptor")] public string? Descriptor { get; init; }

    /// <summary>Verbatim modifier keywords in source order, e.g. "public override".</summary>
    [JsonPropertyName("modifiers")] public string? Modifiers { get; init; }

    /// <summary>Containing symbol's name, so the label can read "Device.Send".</summary>
    [JsonPropertyName("container")] public string? Container { get; init; }

    /// <summary>True for the symbol the fragment was built around.</summary>
    [JsonPropertyName("isFocus")] public bool IsFocus { get; init; }

    /// <summary>
    /// Total incoming links in the whole index, not just this fragment.
    /// <para>
    /// The page subtracts what it is currently drawing to decide whether to offer an
    /// "expand" button. Sending the total rather than a pre-computed difference is what
    /// makes that correct after a merge: only the page knows what is already on screen,
    /// and a fragment-relative number would count neighbours the user can already see.
    /// </para>
    /// </summary>
    [JsonPropertyName("totalCallers")] public int TotalCallers { get; init; }

    /// <summary>Total outgoing links in the whole index. See <see cref="TotalCallers"/>.</summary>
    [JsonPropertyName("totalCallees")] public int TotalCallees { get; init; }
}

/// <summary>One edge, carrying the resolver's confidence so the page can draw it honestly.</summary>
public sealed record GraphEdgePayload
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("target")] public required string Target { get; init; }

    /// <summary>Human-readable reference kind, e.g. "call".</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    /// <summary>"unique", "ambiguous" or "weak".</summary>
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }

    /// <summary>Wording shown in the tooltip. Never claims more than the resolver knows.</summary>
    [JsonPropertyName("confidenceLabel")] public required string ConfidenceLabel { get; init; }

    /// <summary>Line of the first merged reference, so the page can offer a jump.</summary>
    [JsonPropertyName("line")] public int Line { get; init; }

    /// <summary>How many definitions the name matched.</summary>
    [JsonPropertyName("candidates")] public int Candidates { get; init; }

    /// <summary>How many distinct references this drawn edge merged.</summary>
    [JsonPropertyName("callSites")] public int CallSites { get; init; }

    /// <summary>
    /// The reference kind as its stored integer, so the page can hand an edge back to the
    /// host (edge details, jump) without a reverse label lookup.
    /// </summary>
    [JsonPropertyName("kindId")] public int KindId { get; init; }
}

/// <summary>A whole fragment, ready to be serialised to the graph page.</summary>
public sealed record GraphPayload
{
    [JsonPropertyName("focusId")] public string? FocusId { get; init; }
    [JsonPropertyName("nodes")] public IReadOnlyList<GraphNodePayload> Nodes { get; init; } = [];
    [JsonPropertyName("edges")] public IReadOnlyList<GraphEdgePayload> Edges { get; init; } = [];

    /// <summary>True when the node cap cut the result, so the page can say so out loud.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
}

/// <summary>
/// Projects a <see cref="GraphFragment"/> onto the wire format.
/// <para>
/// Kept free of any UI type so it can be tested directly, and so the rule that hidden-link
/// badges are computed from real counts stays verifiable.
/// </para>
/// </summary>
public static class GraphPayloadBuilder
{
    /// <summary>Serialiser settings shared by every message sent to the page.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static GraphPayload Build(GraphFragment fragment)
    {
        var nodes = new List<GraphNodePayload>(fragment.Nodes.Count);
        foreach (var node in fragment.Nodes)
        {
            nodes.Add(new GraphNodePayload
            {
                Id = node.Id.ToString(),
                Name = node.Name,
                Kind = KindLabels.For(node.Kind),
                Group = KindLabels.GroupFor(node.Kind),
                Path = node.RelativePath,
                Line = node.Line,
                Value = node.Value,
                Signature = node.Signature,
                ParameterText = node.ParameterText,
                Descriptor = SymbolFacts.Describe(
                    node.Kind,
                    node.Modifiers,
                    node.TypeText,
                    hasParameterList: node.ParameterText is not null,
                    node.OverloadCount,
                    node.OverloadOrdinal),
                Modifiers = node.Modifiers,
                Container = node.ContainerName,
                IsFocus = fragment.FocusId == node.Id,
                TotalCallers = node.CallerCount,
                TotalCallees = node.CalleeCount,
            });
        }

        var edges = new List<GraphEdgePayload>(fragment.Edges.Count);
        foreach (var edge in fragment.Edges)
        {
            edges.Add(new GraphEdgePayload
            {
                // The renderer needs a stable id; kind is part of it because two symbols can
                // be linked more than once, for instance a call and a type use.
                Id = $"{edge.SourceId}-{edge.TargetId}-{(int)edge.Kind}",
                Source = edge.SourceId.ToString(),
                Target = edge.TargetId.ToString(),
                Kind = KindLabels.For(edge.Kind),
                Confidence = KindLabels.TokenFor(edge.Confidence),
                ConfidenceLabel = KindLabels.For(edge.Confidence),
                Line = edge.Line,
                Candidates = edge.CandidateCount,
                CallSites = edge.CallSiteCount,
                KindId = (int)edge.Kind,
            });
        }

        return new GraphPayload
        {
            FocusId = fragment.FocusId?.ToString(),
            Nodes = nodes,
            Edges = edges,
            Truncated = fragment.WasTruncated,
        };
    }
}
