using System.Text;

namespace CodeAnalyzer.Core.Export;

/// <summary>
/// Renders an exported graph document as a Mermaid flowchart — the export whose
/// destination is a pull-request comment or a markdown document, not a file dialog.
/// <para>
/// The honesty rules survive the format change: solid arrows are exact resolutions,
/// and everything less certain is dashed with the doubt spelled out in the edge label
/// ("call · one of 3 name matches"), because Mermaid has only two line styles where the
/// canvas has three. A header comment states that this is the visible canvas, not the
/// codebase. Direction on an I/O stub is the catalog's documented contract or the
/// user's assertion, and the stub's label says which, exactly as the canvas does.
/// </para>
/// </summary>
public static class MermaidGraphWriter
{
    public static string Write(ExportedGraphDocument document)
    {
        var text = new StringBuilder();
        text.Append("%% CodeAnalyzer export — the visible elements of the current graph, not the codebase.\n");
        text.Append("%% Solid arrows are exact resolutions; dashed arrows carry their doubt in the label.\n");
        text.Append("flowchart LR\n");

        // Page ids are numeric strings or io:-prefixed composites; Mermaid ids must be
        // plain tokens. The mapping is positional, so the same document always renders
        // the same text — diffs of two exports stay readable.
        var ids = new Dictionary<string, string>(document.Nodes.Count);
        foreach (var node in document.Nodes)
        {
            var id = $"n{ids.Count}";
            ids[node.Id] = id;
            text.Append("    ").Append(id).Append(Shape(node)).Append('\n');
        }

        var anyFocus = false;
        foreach (var node in document.Nodes)
        {
            if (node.IsFocus && ids.TryGetValue(node.Id, out var id))
            {
                text.Append("    class ").Append(id).Append(" focus\n");
                anyFocus = true;
            }
        }

        foreach (var edge in document.Edges)
        {
            // An edge whose endpoint is not in the node list cannot be drawn; skipping it
            // silently would misstate the picture, but the page only exports visible
            // edges between visible nodes, so this is a malformed document, not a policy.
            if (!ids.TryGetValue(edge.Source, out var source)
                || !ids.TryGetValue(edge.Target, out var target))
            {
                continue;
            }

            var label = EdgeLabel(edge);
            var arrow = IsCertain(edge) ? "-->" : "-.->";
            text.Append("    ").Append(source).Append(' ').Append(arrow);
            if (label.Length > 0)
            {
                text.Append("|\"").Append(Escape(label)).Append("\"|");
            }

            text.Append(' ').Append(target).Append('\n');
        }

        if (anyFocus)
        {
            text.Append("    classDef focus stroke-width:3px\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Shape by visual family — the same partition the canvas colours by, so the Mermaid
    /// picture and the app agree on what kind of thing a node is. I/O stubs take the
    /// classic flowchart input/output parallelogram.
    /// </summary>
    private static string Shape(ExportedGraphNode node)
    {
        var label = Escape(Label(node));
        if (node.IoBoundary is not null)
        {
            return $"[/\"{label}\"/]";
        }

        return node.Group switch
        {
            "function" => $"(\"{label}\")",
            "type" => $"{{{{\"{label}\"}}}}",
            "constant" => $"{{\"{label}\"}}",
            "macro" => $">\"{label}\"]",
            "module" => $"[\"{label}\"]",
            _ => $"([\"{label}\"])",
        };
    }

    private static string Label(ExportedGraphNode node)
    {
        var name = node.Container is { Length: > 0 } container
            ? $"{container}.{node.Name}"
            : node.Name;

        if (node.IoBoundary is not null)
        {
            // The direction is not a source fact, so its source rides on the label —
            // "HAL_UART_Transmit — output (catalog: STM32 HAL)".
            var direction = node.IoBoundary.Direction;
            var source = node.IoBoundary.Source;
            return (direction, source) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{name} — {direction} ({source})",
                ({ Length: > 0 }, _) => $"{name} — {direction}",
                _ => name,
            };
        }

        // A constant's literal is the fact worth a second glance in a diagram.
        return node.Value is { Length: > 0 } value ? $"{name} = {value}" : name;
    }

    private static bool IsCertain(ExportedGraphEdge edge) =>
        // An I/O stub link has no confidence — it is not a resolution at all, just the
        // line hanging a stub off its caller — and drawing it dashed would read as doubt.
        edge.Confidence is null or "unique";

    private static string EdgeLabel(ExportedGraphEdge edge)
    {
        // An I/O stub's connector is not a reference; the stub node already states the
        // direction and its source, so a label here would only repeat it as noise.
        if (edge.Kind is not { Length: > 0 } kind || kind == "io")
        {
            return string.Empty;
        }

        // Mermaid has solid and dashed, nothing else — so the difference between
        // "several name matches" and "cross-language match" must live in the words.
        var doubt = edge.Confidence switch
        {
            "ambiguous" when edge.Candidates > 1 => $"one of {edge.Candidates} name matches",
            "ambiguous" => "one of several name matches",
            "weak" => "cross-language name match",
            _ => null,
        };

        return doubt is null ? kind : $"{kind} · {doubt}";
    }

    /// <summary>
    /// Mermaid entity escapes for text inside a quoted label. '#' first, because every
    /// escape introduces one. Covers operator&lt;&lt; and blocks a source string from
    /// smuggling in an %%{init}%% directive or closing the quote early.
    /// </summary>
    private static string Escape(string text) => CollapseWhitespace(text)
        .Replace("#", "#35;")
        .Replace("\"", "#quot;")
        .Replace("<", "#lt;")
        .Replace(">", "#gt;")
        .Replace("`", "#96;")
        .Replace("|", "#124;");

    /// <summary>A label is one line; a newline in a verbatim slice must not end it early.</summary>
    private static string CollapseWhitespace(string text)
    {
        var result = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}
