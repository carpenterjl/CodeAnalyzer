using System.Text;
using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;

namespace CodeAnalyzer.Core.Export;

/// <summary>
/// Renders a <see cref="SymbolContextReport"/> as markdown — the "copy as LLM context"
/// format, equally at home pasted into a chat, a PR description or a design note.
/// <para>
/// Everything rendered is an indexed fact, worded by the same vocabularies as every
/// other surface (<see cref="KindLabels"/>, <see cref="SymbolFacts"/>,
/// <see cref="ValueFacts"/>, <see cref="IoDirectionLabels"/>). Confidence rides every
/// related line, caps state themselves, empty sections are omitted rather than rendered
/// hollow, and the header claims exactly one symbol's neighbourhood — never the codebase.
/// </para>
/// </summary>
public static class MarkdownFactWriter
{
    public static string Write(SymbolContextReport report)
    {
        var detail = report.Detail;
        var text = new StringBuilder();

        text.Append("# ").Append(Code(detail.Name)).Append(" — ")
            .Append(SymbolFacts.Describe(
                detail.Kind,
                detail.Modifiers,
                detail.TypeText,
                hasParameterList: detail.ParameterText is not null,
                detail.OverloadCount,
                detail.OverloadOrdinal))
            .Append('\n');

        text.Append('\n').Append(Code($"{detail.RelativePath}:{detail.StartLine}"))
            .Append(" · ").Append(detail.Language);
        if (report.Provenance is { Length: > 0 } provenance)
        {
            text.Append(" · ").Append(provenance);
        }

        text.Append('\n');
        text.Append("\nFacts from the CodeAnalyzer index: this symbol and its resolved")
            .Append(" neighbourhood, nothing more. `~` marks a name match among several")
            .Append(" candidates, `?` a cross-language match.\n");

        if (detail.Signature is { Length: > 0 } signature)
        {
            text.Append('\n').Append("**Signature:** ").Append(Code(Flatten(signature))).Append('\n');
        }

        if (detail.Value is { Length: > 0 } value)
        {
            text.Append('\n').Append("**Value:** ").Append(Code(Flatten(value))).Append('\n');
        }

        WriteMembers(text, detail);
        WriteInheritance(text, detail);
        WriteOverloads(text, detail);
        WriteRelated(text, "Callers", detail.Callers, report.RelatedLimit, incoming: true);

        // A container with no callers of its own but reachable members: without this the
        // sheet has no Callers section at all, which reads as "nothing uses this".
        if (detail.MemberCallerTotal > 0)
        {
            text.Append("\n## Callers (0 — but its members have ")
                .Append(detail.MemberCallerTotal)
                .Append(")\n\nNothing references `")
                .Append(detail.Name)
                .Append("` by name. Its ")
                .Append(detail.Members.Count)
                .Append(" members are reached from ")
                .Append(detail.MemberCallerTotal)
                .Append(" place")
                .Append(detail.MemberCallerTotal == 1 ? string.Empty : "s")
                .Append(", so the zero above is about the name, not about the code.\n");
        }

        WriteRelated(text, "Callees", detail.Callees, report.RelatedLimit, incoming: false,
            report.CalleeSites);
        WriteIoSites(text, report.IoSites);
        WriteSameValue(text, report.SameValue);
        WriteUnresolved(text, detail);
        WriteSource(text, report);

        return text.ToString();
    }

    private static void WriteMembers(StringBuilder text, SymbolDetail detail)
    {
        if (detail.Members.Count == 0)
        {
            return;
        }

        text.Append("\n## Members (").Append(detail.Members.Count).Append(")\n\n");
        foreach (var member in detail.Members)
        {
            text.Append("- ").Append(Code(member.Name))
                .Append(" — ").Append(KindLabels.For(member.Kind));
            if (member.TypeText is { Length: > 0 } type)
            {
                text.Append(' ').Append(Code(Flatten(type)));
            }

            if (member.Value is { Length: > 0 } value)
            {
                text.Append(" = ").Append(Code(Flatten(value)));
            }

            if (member.Modifiers is { Length: > 0 } modifiers)
            {
                text.Append(" · ").Append(modifiers);
            }

            text.Append(" (line ").Append(member.Line).Append(")\n");
        }
    }

    /// <summary>
    /// What the type derives from and what derives from it. The reason this section is
    /// worth its own heading rather than being left to Callers: a base type the workspace
    /// does not define — <c>IDisposable</c>, <c>Exception</c> — has no symbol to be a
    /// caller of, so the only place the fact can appear is one that prints the name the
    /// declaration wrote and says plainly that it stops there.
    /// <para>
    /// Derived types do also appear under Callers, labelled "inherits". That repetition is
    /// deliberate and matches the terse formatter: a reader scanning for "who touches this"
    /// should not have to know that inheritance was filed elsewhere.
    /// </para>
    /// </summary>
    private static void WriteInheritance(StringBuilder text, SymbolDetail detail)
    {
        if (detail.BaseTypes.Count == 0 && detail.DerivedTypes.Count == 0)
        {
            return;
        }

        text.Append("\n## Inheritance\n\n");

        foreach (var baseType in detail.BaseTypes)
        {
            text.Append("- derives from ").Append(Code(baseType.Name));
            text.Append(baseType.TargetPath is { } path
                ? $" — {Code($"{path}:{baseType.TargetLine}")}"
                : " — not defined in this workspace");
            text.Append('\n');
        }

        foreach (var derived in detail.DerivedTypes)
        {
            text.Append("- derived by ").Append(Code(derived.Name))
                .Append(" — ").Append(Code($"{derived.RelativePath}:{derived.Line}")).Append('\n');
        }
    }

    private static void WriteOverloads(StringBuilder text, SymbolDetail detail)
    {
        if (detail.Overloads.Count == 0)
        {
            return;
        }

        text.Append("\n## Overloads (").Append(detail.Overloads.Count).Append(")\n\n");
        foreach (var overload in detail.Overloads)
        {
            text.Append("- ").Append(Code(Flatten(
                overload.Signature ?? overload.ParameterText ?? "(unknown signature)")));
            text.Append(" — line ").Append(overload.Line);
            if (overload.IsCurrent)
            {
                text.Append(" ← this one");
            }

            text.Append('\n');
        }
    }

    private static void WriteRelated(
        StringBuilder text,
        string heading,
        IReadOnlyList<RelatedSymbol> related,
        int relatedLimit,
        bool incoming,
        IReadOnlyList<CalleeCallSites>? calleeSites = null)
    {
        if (related.Count == 0)
        {
            return;
        }

        text.Append("\n## ").Append(heading).Append(" (").Append(related.Count);
        if (related.Count >= relatedLimit)
        {
            // Exactly at the cap means "possibly more": the query cannot tell, so
            // neither can the report.
            text.Append(" — query capped at ").Append(relatedLimit).Append(", more may exist");
        }

        text.Append(")\n\n");

        var sitesBySymbol = calleeSites?.ToDictionary(s => s.Callee.Id, s => s.Sites);
        foreach (var entry in related)
        {
            text.Append("- ").Append(Code(entry.Name))
                .Append(" — ").Append(KindLabels.For(entry.ReferenceKind));
            if (entry.Confidence != EdgeConfidence.Unique)
            {
                text.Append(", ").Append(KindLabels.For(entry.Confidence));
            }

            text.Append(" — ").Append(Code($"{entry.RelativePath}:{entry.Line}")).Append('\n');

            if (sitesBySymbol is not null && sitesBySymbol.TryGetValue(entry.Id, out var sites))
            {
                foreach (var site in sites)
                {
                    text.Append("  - line ").Append(site.Line);

                    // The source as written. Gating this line on arguments used to drop the
                    // receiver — the evidence behind the confidence mark — from every
                    // reference that has none, which, since a use may now bind to a
                    // member of a type, is most of them.
                    text.Append(": ").Append(Code(Flatten(site.SourceText(entry.ReferenceKind, entry.Name))));

                    text.Append('\n');
                }
            }
        }

        _ = incoming; // Same rendering both directions; the parameter documents the call.
    }

    private static void WriteIoSites(StringBuilder text, IReadOnlyList<IoBoundarySite> sites)
    {
        if (sites.Count == 0)
        {
            return;
        }

        text.Append("\n## I/O boundaries (").Append(sites.Count).Append(")\n\n");
        text.Append("Direction is the API's documented contract or the user's own mark — never derived from syntax.\n\n");
        foreach (var site in sites)
        {
            text.Append("- ").Append(Code(site.Name))
                .Append(" — ").Append(IoDirectionLabels.For(site.Direction))
                .Append(" (").Append(site.Origin == IoMatchOrigin.UserMark
                    ? "your mark"
                    : $"catalog: {site.Family}").Append(')');

            text.Append(" — ").Append(Code($"{site.RelativePath}:{site.Line}"));
            if (site.ArgumentText is { Length: > 0 } args)
            {
                text.Append(" — ").Append(Code(Flatten(args)));
            }

            text.Append('\n');
            if (site.GateNote is { Length: > 0 } gate)
            {
                text.Append("  - matched because: ").Append(gate).Append('\n');
            }
        }
    }

    private static void WriteSameValue(StringBuilder text, ValueMatchSet? sameValue)
    {
        if (sameValue is null || sameValue.Matches.Count == 0)
        {
            return;
        }

        text.Append("\n## Same value elsewhere (").Append(sameValue.Matches.Count);
        if (sameValue.Truncated)
        {
            text.Append(" — first ").Append(sameValue.Limit).Append(", more exist");
        }

        text.Append(", ").Append(sameValue.Canonical).Append(")\n\n");
        text.Append(ValueFacts.EvidenceSentence).Append('\n').Append('\n');
        foreach (var match in sameValue.Matches)
        {
            text.Append("- ").Append(Code(match.Name))
                .Append(" — ").Append(KindLabels.For(match.Kind))
                .Append(", ").Append(match.EqualityNote)
                .Append(" — ").Append(Code($"{match.RelativePath}:{match.Line}"))
                .Append(" [").Append(match.Language).Append("]\n");
        }
    }

    private static void WriteUnresolved(StringBuilder text, SymbolDetail detail)
    {
        if (detail.UnresolvedReferences.Count == 0)
        {
            return;
        }

        text.Append("\n## Unresolved references (")
            .Append(detail.UnresolvedReferences.Count).Append(")\n\n");
        text.Append("Referenced here, but no definition is in the index — library calls land here.\n\n");
        foreach (var reference in detail.UnresolvedReferences)
        {
            text.Append("- ").Append(Code(reference.Name))
                .Append(" — ").Append(KindLabels.For(reference.Kind))
                .Append(", line ").Append(reference.Line).Append('\n');
        }
    }

    private static void WriteSource(StringBuilder text, SymbolContextReport report)
    {
        if (report.SourceExcerpt is not { } excerpt)
        {
            return;
        }

        var detail = report.Detail;
        text.Append("\n## Source (lines ").Append(detail.StartLine)
            .Append('–').Append(report.SourceExcerptEndLine);
        if (report.SourceTruncated)
        {
            text.Append(" of ").Append(detail.StartLine).Append('–').Append(detail.EndLine)
                .Append(" — truncated");
        }

        text.Append(")\n\n");

        // The fence must be longer than any backtick run inside the excerpt, or source
        // containing ``` would end the block early and the rest would render as prose.
        var fence = new string('`', Math.Max(3, LongestBacktickRun(excerpt) + 1));
        text.Append(fence).Append(FenceLanguage(detail.Language)).Append('\n');
        text.Append(excerpt).Append('\n');
        text.Append(fence).Append('\n');
    }

    private static string FenceLanguage(string language) => language switch
    {
        "C#" => "csharp",
        "C++" => "cpp",
        _ => language.ToLowerInvariant(),
    };

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, current = 0;
        foreach (var c in text)
        {
            current = c == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    /// <summary>Inline code span, widened when the content itself carries backticks.</summary>
    private static string Code(string text)
    {
        if (!text.Contains('`'))
        {
            return $"`{text}`";
        }

        var fence = new string('`', LongestBacktickRun(text) + 1);
        return $"{fence} {text} {fence}";
    }

    /// <summary>Verbatim slices can span lines; a list row cannot.</summary>
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
