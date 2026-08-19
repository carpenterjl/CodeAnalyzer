using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;

namespace CodeAnalyzer.Core.Export;

/// <summary>
/// The flat document a call flow exports as — one shape whether it came from the flow
/// page's canvas or straight from a <see cref="CallFlow"/> on the CLI, so
/// <see cref="MermaidFlowWriter"/> has exactly one input to be correct about.
/// <para>
/// Steps are flattened; nesting is recoverable from the ordinal ("2.1" sits under "2",
/// a root-level step under the root). <see cref="ExportedFlowStep.CycleOf"/> and
/// <see cref="ExportedFlowStep.CollapsedAt"/> name ordinals for the same reason the
/// trace computes ordinals at all: a drawn reference must be able to cite its target.
/// </para>
/// </summary>
public sealed record ExportedFlowDocument
{
    [JsonPropertyName("root")] public ExportedFlowRoot? Root { get; init; }
    [JsonPropertyName("steps")] public IReadOnlyList<ExportedFlowStep> Steps { get; init; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        // Same tolerance as the graph document: the page may grow the shape first.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>Parses the page's export JSON. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static ExportedFlowDocument Parse(string json) =>
        JsonSerializer.Deserialize<ExportedFlowDocument>(json, Options)
            ?? new ExportedFlowDocument();

    /// <summary>
    /// Flattens a trace into the document — the CLI's path to the same writer the GUI
    /// export feeds. Cycle steps here name the nearest enclosing occurrence of their
    /// target, or the root when the recursion reaches all the way back up.
    /// </summary>
    public static ExportedFlowDocument From(CallFlow flow)
    {
        var steps = new List<ExportedFlowStep>();
        var ancestors = new Stack<(string Ordinal, long? TargetId)>();
        ancestors.Push(("root", flow.RootId));

        void Walk(IReadOnlyList<CallFlowStep> level)
        {
            foreach (var step in level)
            {
                string? cycleOf = null;
                if (step.IsCycle && step.TargetId is { } target)
                {
                    foreach (var (ordinal, ancestorTarget) in ancestors)
                    {
                        if (ancestorTarget == target)
                        {
                            cycleOf = ordinal;
                            break;
                        }
                    }
                }

                steps.Add(new ExportedFlowStep
                {
                    Ordinal = step.Ordinal,
                    Name = step.Name,
                    Receiver = step.ReceiverText,
                    Args = step.ArgumentText,
                    Fate = step.Fate is { } fate ? KindLabels.TokenFor(fate) : null,
                    FateName = step.FateName,
                    Confidence = KindLabels.TokenFor(step.Confidence),
                    Candidates = step.OtherCandidates.Count,
                    TargetName = step.TargetName,
                    TargetPath = step.TargetPath,
                    Cycle = step.IsCycle,
                    CycleOf = cycleOf,
                    Unresolved = step.IsUnresolved,
                    CollapsedAt = step.CollapsedAt,
                    IoDirection = step.IsIoBoundary
                        ? IoDirectionLabels.For(step.IoDirection)
                        : null,
                    IoFamily = step.IoFamily,
                    Truncated = step.ChildrenTruncated,
                    CallSites = step.CallSitesInBody,
                    Line = step.Line,
                });

                if (step.Children.Count > 0)
                {
                    ancestors.Push((step.Ordinal, step.TargetId));
                    Walk(step.Children);
                    ancestors.Pop();
                }
            }
        }

        Walk(flow.Steps);

        return new ExportedFlowDocument
        {
            Root = new ExportedFlowRoot
            {
                Id = flow.RootId.ToString(),
                Name = flow.RootName ?? $"#{flow.RootId}",
                Kind = KindLabels.For(flow.RootKind),
                Path = flow.RootPath,
                Line = flow.RootLine,
            },
            Steps = steps,
            Truncated = flow.WasTruncated,
        };
    }
}

public sealed record ExportedFlowRoot
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("line")] public int? Line { get; init; }
}

/// <summary>One call site of the flattened trace.</summary>
public sealed record ExportedFlowStep
{
    [JsonPropertyName("ordinal")] public required string Ordinal { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("receiver")] public string? Receiver { get; init; }
    [JsonPropertyName("args")] public string? Args { get; init; }

    /// <summary>Fate wire token ("assigned", "discarded", …); null when no claim exists.</summary>
    [JsonPropertyName("fate")] public string? Fate { get; init; }

    [JsonPropertyName("fateName")] public string? FateName { get; init; }

    /// <summary>Confidence wire token ("unique", "ambiguous", "weak").</summary>
    [JsonPropertyName("confidence")] public string? Confidence { get; init; }

    /// <summary>How many candidates the step did not follow.</summary>
    [JsonPropertyName("candidates")] public int Candidates { get; init; }

    [JsonPropertyName("targetName")] public string? TargetName { get; init; }
    [JsonPropertyName("targetPath")] public string? TargetPath { get; init; }
    [JsonPropertyName("cycle")] public bool Cycle { get; init; }

    /// <summary>Ordinal of the enclosing occurrence recursion loops back to ("root" included).</summary>
    [JsonPropertyName("cycleOf")] public string? CycleOf { get; init; }

    [JsonPropertyName("unresolved")] public bool Unresolved { get; init; }

    /// <summary>Ordinal of the step where this target's subtree is fully drawn.</summary>
    [JsonPropertyName("collapsedAt")] public string? CollapsedAt { get; init; }

    /// <summary>Boundary direction wording ("out"), present only on I/O steps.</summary>
    [JsonPropertyName("ioDirection")] public string? IoDirection { get; init; }

    [JsonPropertyName("ioFamily")] public string? IoFamily { get; init; }

    [JsonPropertyName("stepTruncated")] public bool Truncated { get; init; }

    /// <summary>Call sites in the target's body when expansion was cut.</summary>
    [JsonPropertyName("callSites")] public int CallSites { get; init; }

    [JsonPropertyName("line")] public int Line { get; init; }
}
