using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAnalyzer.Core.Export;

/// <summary>
/// The JSON document the graph page produces for an export — pinned as a type at last,
/// after living since M8 only as the shape graph.js happened to emit.
/// <para>
/// The page is the authority on what is in the document: it answers with exactly the
/// visible elements of the canvas, expansions included and hidden items excluded, which
/// the host deliberately cannot reconstruct. This type only names that shape so writers
/// (Mermaid today) can consume it without re-parsing JSON by hand. Every field is
/// optional-tolerant, because the page omits what a node does not have.
/// </para>
/// </summary>
public sealed record ExportedGraphDocument
{
    [JsonPropertyName("nodes")] public IReadOnlyList<ExportedGraphNode> Nodes { get; init; } = [];
    [JsonPropertyName("edges")] public IReadOnlyList<ExportedGraphEdge> Edges { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        // The page writes camelCase property names; the attributes above pin each one, so
        // no naming policy is needed — but unknown members must never fail the parse,
        // because the page is allowed to grow the document before this type learns of it.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>Parses the page's export JSON. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static ExportedGraphDocument Parse(string json) =>
        JsonSerializer.Deserialize<ExportedGraphDocument>(json, Options)
            ?? new ExportedGraphDocument();
}

/// <summary>One node of the exported picture. For an I/O stub, <see cref="IoBoundary"/> is set.</summary>
public sealed record ExportedGraphNode
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>Human-readable kind label ("function", "interface").</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }

    /// <summary>Visual family ("function", "type", …) — what shapes are chosen by.</summary>
    [JsonPropertyName("group")] public string? Group { get; init; }

    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("line")] public int? Line { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("signature")] public string? Signature { get; init; }
    [JsonPropertyName("params")] public string? Params { get; init; }
    [JsonPropertyName("modifiers")] public string? Modifiers { get; init; }
    [JsonPropertyName("container")] public string? Container { get; init; }
    [JsonPropertyName("isFocus")] public bool IsFocus { get; init; }

    /// <summary>Present only on I/O boundary stubs. Direction names its source, never syntax.</summary>
    [JsonPropertyName("ioBoundary")] public ExportedIoBoundary? IoBoundary { get; init; }

    /// <summary>An I/O stub's verbatim argument slice — what crosses the boundary.</summary>
    [JsonPropertyName("argText")] public string? ArgText { get; init; }
}

/// <summary>The direction of an exported I/O stub and whose assertion it is.</summary>
public sealed record ExportedIoBoundary
{
    [JsonPropertyName("direction")] public string? Direction { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
}

/// <summary>One edge of the exported picture. I/O stub links carry no kind or confidence.</summary>
public sealed record ExportedGraphEdge
{
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("target")] public required string Target { get; init; }

    /// <summary>Human-readable reference kind ("call"), or null for an I/O stub link.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }

    /// <summary>"unique", "ambiguous" or "weak"; null for an I/O stub link.</summary>
    [JsonPropertyName("confidence")] public string? Confidence { get; init; }

    [JsonPropertyName("line")] public int? Line { get; init; }

    /// <summary>How many definitions the name matched; the page omits it when 1.</summary>
    [JsonPropertyName("candidates")] public int Candidates { get; init; }

    /// <summary>How many references the drawn edge merged; the page omits it when 1.</summary>
    [JsonPropertyName("callSites")] public int CallSites { get; init; }
}
