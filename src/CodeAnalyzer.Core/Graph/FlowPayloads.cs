using System.Text.Json.Serialization;
using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

// Wire shapes for the flow view. Same rules as GraphPayload and ViewPayloads: ids are
// strings because JavaScript numbers cannot hold the 64-bit range, kind labels come from
// KindLabels so the XAML pane and the page cannot drift apart, and anything the resolver
// was unsure about keeps its uncertainty all the way to the screen.

public sealed record FlowTargetPayload
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("group")] public string? Group { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("line")] public int Line { get; init; }
}

public sealed record FlowCandidatePayload
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("line")] public int Line { get; init; }
}

public sealed record FlowIoPayload
{
    [JsonPropertyName("direction")] public required string Direction { get; init; }
    [JsonPropertyName("family")] public string? Family { get; init; }
}

public sealed record FlowTruncationPayload
{
    [JsonPropertyName("shown")] public int Shown { get; init; }
    [JsonPropertyName("total")] public int Total { get; init; }
}

public sealed record FlowStepPayload
{
    [JsonPropertyName("ordinal")] public required string Ordinal { get; init; }
    [JsonPropertyName("refId")] public required string RefId { get; init; }

    /// <summary>
    /// The symbol whose body writes this call — what <c>edgeActivated</c> needs to open
    /// the call site in the editor. The engine model implies it by nesting; the wire
    /// carries it so no renderer has to re-derive it.
    /// </summary>
    [JsonPropertyName("callerId")] public required string CallerId { get; init; }

    [JsonPropertyName("line")] public int Line { get; init; }
    [JsonPropertyName("col")] public int Col { get; init; }
    [JsonPropertyName("receiver")] public string? Receiver { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("args")] public string? Args { get; init; }

    /// <summary>True for a construction — the page writes the <c>new</c>.</summary>
    [JsonPropertyName("isNew")] public bool IsNew { get; init; }

    /// <summary>Fate wire token; null where the walker made no claim.</summary>
    [JsonPropertyName("fate")] public string? Fate { get; init; }

    [JsonPropertyName("fateName")] public string? FateName { get; init; }
    [JsonPropertyName("target")] public FlowTargetPayload? Target { get; init; }
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }
    [JsonPropertyName("confidenceLabel")] public string? ConfidenceLabel { get; init; }
    [JsonPropertyName("candidates")] public IReadOnlyList<FlowCandidatePayload> Candidates { get; init; } = [];
    [JsonPropertyName("cycle")] public bool Cycle { get; init; }

    /// <summary>Ordinal of the enclosing occurrence recursion re-enters ("root" included).</summary>
    [JsonPropertyName("cycleOf")] public string? CycleOf { get; init; }

    [JsonPropertyName("unresolved")] public bool Unresolved { get; init; }

    /// <summary>Ordinal of the step where this target's subtree is fully drawn.</summary>
    [JsonPropertyName("collapsedAt")] public string? CollapsedAt { get; init; }

    [JsonPropertyName("io")] public FlowIoPayload? Io { get; init; }

    /// <summary>Non-null when expansion below this step was cut.</summary>
    [JsonPropertyName("truncated")] public FlowTruncationPayload? Truncated { get; init; }

    [JsonPropertyName("steps")] public IReadOnlyList<FlowStepPayload> Steps { get; init; } = [];
}

public sealed record FlowPayload
{
    [JsonPropertyName("root")] public required FlowTargetPayload Root { get; init; }
    [JsonPropertyName("depth")] public int Depth { get; init; }
    [JsonPropertyName("totalSteps")] public int TotalSteps { get; init; }
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }

    /// <summary>Non-null when the root's own listing was cut.</summary>
    [JsonPropertyName("rootTruncated")] public FlowTruncationPayload? RootTruncated { get; init; }

    [JsonPropertyName("steps")] public IReadOnlyList<FlowStepPayload> Steps { get; init; } = [];
}

/// <summary>A subtree answered for one "+N calls" request, grafted under a drawn step.</summary>
public sealed record FlowBranchPayload
{
    /// <summary>The ordinal of the step the branch grows under.</summary>
    [JsonPropertyName("at")] public required string At { get; init; }

    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("steps")] public IReadOnlyList<FlowStepPayload> Steps { get; init; } = [];
}

/// <summary>
/// Maps the engine's trace onto the wire. Host-side and testable, like
/// <see cref="GraphPayloadBuilder"/>; the two facts the engine model implies rather than
/// states — each step's caller, and the occurrence recursion re-enters — are computed here.
/// </summary>
public static class FlowPayloadBuilder
{
    public static FlowPayload Build(CallFlow flow) => new()
    {
        Root = new FlowTargetPayload
        {
            Id = flow.RootId.ToString(),
            Name = flow.RootName,
            Kind = KindLabels.For(flow.RootKind),
            Group = KindLabels.GroupFor(flow.RootKind),
            Path = flow.RootPath,
            Line = flow.RootLine,
        },
        Depth = flow.DepthUsed,
        TotalSteps = flow.TotalSteps,
        Truncated = flow.WasTruncated,
        RootTruncated = flow.RootTruncated
            ? new FlowTruncationPayload { Shown = flow.Steps.Count, Total = flow.RootCallSites }
            : null,
        Steps = BuildSteps(flow.Steps, flow.RootId.ToString(), [("root", flow.RootId)]),
    };

    /// <summary>Builds a deepened subtree. The ancestor chain is the page's, echoed back.</summary>
    public static FlowBranchPayload BuildBranch(CallFlow flow, string atOrdinal) => new()
    {
        At = atOrdinal,
        Truncated = flow.WasTruncated,
        Steps = BuildSteps(flow.Steps, flow.RootId.ToString(), [(atOrdinal, flow.RootId)]),
    };

    private static List<FlowStepPayload> BuildSteps(
        IReadOnlyList<CallFlowStep> steps,
        string callerId,
        List<(string Ordinal, long TargetId)> ancestors)
    {
        var result = new List<FlowStepPayload>(steps.Count);
        foreach (var step in steps)
        {
            string? cycleOf = null;
            if (step.IsCycle && step.TargetId is { } cycleTarget)
            {
                for (var i = ancestors.Count - 1; i >= 0; i--)
                {
                    if (ancestors[i].TargetId == cycleTarget)
                    {
                        cycleOf = ancestors[i].Ordinal;
                        break;
                    }
                }
            }

            var children = (IReadOnlyList<FlowStepPayload>)[];
            if (step.Children.Count > 0 && step.TargetId is { } expandedTarget)
            {
                ancestors.Add((step.Ordinal, expandedTarget));
                children = BuildSteps(step.Children, expandedTarget.ToString(), ancestors);
                ancestors.RemoveAt(ancestors.Count - 1);
            }

            result.Add(new FlowStepPayload
            {
                Ordinal = step.Ordinal,
                RefId = step.RefId.ToString(),
                CallerId = callerId,
                Line = step.Line,
                Col = step.Col,
                Receiver = step.ReceiverText,
                Name = step.Name,
                Args = step.ArgumentText,
                IsNew = step.Kind is ReferenceKind.TypeUse or ReferenceKind.Instantiate,
                Fate = step.Fate is { } fate ? KindLabels.TokenFor(fate) : null,
                FateName = step.FateName,
                Target = step.TargetId is { } targetId
                    ? new FlowTargetPayload
                    {
                        Id = targetId.ToString(),
                        Name = step.TargetName,
                        Kind = KindLabels.For(step.TargetKind),
                        Group = KindLabels.GroupFor(step.TargetKind),
                        Path = step.TargetPath,
                        Line = step.TargetLine,
                    }
                    : null,
                Confidence = KindLabels.TokenFor(step.Confidence),
                ConfidenceLabel = KindLabels.For(step.Confidence),
                Candidates = step.OtherCandidates.Select(candidate => new FlowCandidatePayload
                {
                    Id = candidate.Id.ToString(),
                    Name = candidate.Name,
                    Kind = KindLabels.For(candidate.Kind),
                    Path = candidate.RelativePath,
                    Line = candidate.Line,
                }).ToList(),
                Cycle = step.IsCycle,
                CycleOf = cycleOf,
                Unresolved = step.IsUnresolved,
                CollapsedAt = step.CollapsedAt,
                Io = step.IsIoBoundary
                    ? new FlowIoPayload
                    {
                        Direction = IoDirectionLabels.For(step.IoDirection),
                        Family = step.IoFamily,
                    }
                    : null,
                Truncated = step.ChildrenTruncated
                    ? new FlowTruncationPayload
                    {
                        Shown = step.Children.Count,
                        Total = step.CallSitesInBody,
                    }
                    : null,
                Steps = children,
            });
        }

        return result;
    }
}
