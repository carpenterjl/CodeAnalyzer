using System.Text;

namespace CodeAnalyzer.Core.Export;

/// <summary>
/// The escaping rules a verbatim source slice must pass through before it becomes a
/// Mermaid label. Shared between the graph writer and the flow writer so the two can
/// never drift on the injection guard — a hostile argument string escapes one way, or
/// the export is the bug.
/// </summary>
internal static class MermaidText
{
    internal static string Escape(string text) => CollapseWhitespace(text)
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
