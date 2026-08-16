using System.Text;
using CodeAnalyzer.Core.Graph;

namespace CodeAnalyzer.Core.Export;

/// <summary>
/// Renders the I/O boundaries as markdown tables — outputs then inputs, grouped by
/// top-level directory, the same partition the Boundaries view draws. Built entirely
/// host-side from <see cref="ViewPayloadBuilder.Build(IReadOnlyList{Domain.IoBoundarySite})"/>,
/// so it shares the view's grouping instead of restating it; an inout site appears in
/// both tables because it genuinely does both.
/// </summary>
public static class BoundariesMarkdownWriter
{
    public static string Write(BoundariesPayload payload, string workspaceName)
    {
        var text = new StringBuilder();
        text.Append("# I/O boundaries — ").Append(workspaceName).Append('\n');
        text.Append('\n').Append(payload.TotalSites).Append(" call sites. ")
            .Append("Direction is the API's documented contract or the user's own mark — ")
            .Append("never derived from syntax; each row names its source. ")
            .Append("A gated match is still a name match, and its rule is stated.\n");

        WriteSide(text, "Outputs — data leaving", payload.Outputs);
        WriteSide(text, "Inputs — data arriving", payload.Inputs);

        if (payload.TotalSites == 0)
        {
            text.Append("\nNo catalog API matched and nothing is marked.\n");
        }

        return text.ToString();
    }

    private static void WriteSide(
        StringBuilder text, string heading, IReadOnlyList<BoundaryGroupPayload> groups)
    {
        if (groups.Count == 0)
        {
            return;
        }

        text.Append("\n## ").Append(heading).Append('\n');
        foreach (var group in groups)
        {
            text.Append("\n### ").Append(Cell(group.Label)).Append('\n');
            text.Append('\n');
            text.Append("| API | Caller | Site | Data | Direction per |\n");
            text.Append("|---|---|---|---|---|\n");
            foreach (var site in group.Sites)
            {
                text.Append("| ").Append(Code(site.Name))
                    .Append(" | ").Append(site.Caller is { Length: > 0 } caller
                        ? Code(caller)
                        : "—")
                    .Append(" | ").Append(Code($"{site.Path}:{site.Line}"))
                    .Append(" | ").Append(site.ArgText is { Length: > 0 } args
                        ? Code(Flatten(args))
                        : "—")
                    .Append(" | ").Append(Cell(site.Source));

                if (site.GateNote is { Length: > 0 } gate)
                {
                    text.Append(" — ").Append(Cell(gate));
                }

                text.Append(" |\n");
            }
        }
    }

    /// <summary>A pipe in a table cell ends the cell; verbatim source must not.</summary>
    private static string Cell(string text) => Flatten(text).Replace("|", "\\|");

    private static string Code(string text) => $"`{Cell(text)}`";

    private static string Flatten(string text)
    {
        if (!text.Contains('\n') && !text.Contains('\r') && !text.Contains('\t'))
        {
            return text;
        }

        var parts = text.Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
