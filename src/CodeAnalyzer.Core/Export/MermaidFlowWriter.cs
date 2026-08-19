using System.Text;

namespace CodeAnalyzer.Core.Export;

/// <summary>
/// Renders an exported call flow as a Mermaid flowchart.
/// <para>
/// Same conventions as <see cref="MermaidGraphWriter"/> — solid arrows are exact
/// resolutions, dashed arrows carry their doubt in the label, I/O steps are the classic
/// input/output parallelogram — but its own writer, because a trace is not a canvas: a
/// node here is a call <em>site</em> keyed by ordinal, repeats are distinct boxes by
/// construction, and the return of a value is an edge the graph export has no concept of.
/// </para>
/// <para>
/// A return edge is drawn only where it says something: a step whose result is consumed
/// (assigned, returned onward, passed on, tested) sends a dashed labelled arrow back to
/// its caller. A discarded result sends none — the absence is the statement.
/// </para>
/// </summary>
public static class MermaidFlowWriter
{
    public static string Write(ExportedFlowDocument document)
    {
        var text = new StringBuilder();
        text.Append("%% CodeAnalyzer call flow — call sites in the order they are written; not an execution claim.\n");
        text.Append("%% Solid arrows are exact resolutions; dashed arrows carry their doubt or their meaning in the label.\n");
        if (document.Truncated)
        {
            text.Append("%% This flow was cut by a depth or budget cap — steps marked truncated hold more than is drawn.\n");
        }

        text.Append("flowchart TD\n");

        var root = document.Root;
        var rootLabel = root is null
            ? "flow"
            : MermaidText.Escape($"{root.Name} · {root.Kind}");
        text.Append("    root([\"").Append(rootLabel).Append("\"])\n");

        foreach (var step in document.Steps)
        {
            AppendNode(text, step);
        }

        foreach (var step in document.Steps)
        {
            AppendEdges(text, step);
        }

        return text.ToString();
    }

    private static void AppendNode(StringBuilder text, ExportedFlowStep step)
    {
        var call = (step.Receiver is { Length: > 0 } r ? r + "." : string.Empty)
            + step.Name + step.Args;
        var label = $"{step.Ordinal} {call}";

        if (step.Unresolved)
        {
            label += " · unresolved";
        }

        if (step.IoDirection is not null)
        {
            label += $" · {step.IoDirection}";
            if (step.IoFamily is not null)
            {
                label += $" — {step.IoFamily}";
            }
        }

        text.Append("    ").Append(NodeId(step.Ordinal));
        text.Append(step.IoDirection is not null ? "[/\"" : "[\"");
        text.Append(MermaidText.Escape(label));
        text.Append(step.IoDirection is not null ? "\"/]" : "\"]");
        text.Append('\n');

        if (step.Truncated && step.CallSites > 0)
        {
            text.Append("    ").Append(NodeId(step.Ordinal)).Append("_cut([\"")
                .Append(MermaidText.Escape($"… {step.CallSites} call(s) not expanded"))
                .Append("\"])\n");
        }
    }

    private static void AppendEdges(StringBuilder text, ExportedFlowStep step)
    {
        var id = NodeId(step.Ordinal);
        var parent = ParentId(step.Ordinal);

        // The call arrow, with the doubt worded on it when the resolution has any.
        text.Append("    ").Append(parent);
        if (step.Unresolved)
        {
            text.Append(" -.->|\"unresolved\"| ");
        }
        else if (step.Confidence == "ambiguous")
        {
            var others = step.Candidates + 1;
            text.Append(" -.->|\"").Append(MermaidText.Escape($"one of {others} name matches"))
                .Append("\"| ");
        }
        else if (step.Confidence == "weak")
        {
            text.Append(" -.->|\"cross-language name match\"| ");
        }
        else
        {
            text.Append(" --> ");
        }

        text.Append(id).Append('\n');

        // The value's way back, only where it says something.
        var returnLabel = step.Fate switch
        {
            "assigned" => step.FateName is { Length: > 0 } name ? $"→ {name}" : "assigned",
            "returned" => "returned onward",
            "arg" => "into another call",
            "tested" => "tested",
            _ => null,
        };
        if (returnLabel is not null)
        {
            text.Append("    ").Append(id).Append(" -.->|\"")
                .Append(MermaidText.Escape(returnLabel)).Append("\"| ").Append(parent).Append('\n');
        }

        if (step.Cycle)
        {
            text.Append("    ").Append(id).Append(" -.->|\"recursion\"| ")
                .Append(NodeId(step.CycleOf ?? "root")).Append('\n');
        }

        if (step.CollapsedAt is { } drawing)
        {
            text.Append("    ").Append(id).Append(" -.->|\"")
                .Append(MermaidText.Escape($"= subtree at {drawing}")).Append("\"| ")
                .Append(NodeId(drawing)).Append('\n');
        }

        if (step.Truncated && step.CallSites > 0)
        {
            text.Append("    ").Append(id).Append(" -.-> ").Append(id).Append("_cut\n");
        }
    }

    private static string NodeId(string ordinal) =>
        ordinal == "root" ? "root" : "s" + ordinal.Replace('.', '_');

    private static string ParentId(string ordinal)
    {
        var lastDot = ordinal.LastIndexOf('.');
        return lastDot < 0 ? "root" : NodeId(ordinal[..lastDot]);
    }
}
