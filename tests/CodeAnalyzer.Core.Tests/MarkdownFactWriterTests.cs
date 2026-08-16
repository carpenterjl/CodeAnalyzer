using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Export;
using CodeAnalyzer.Core.Graph;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// Pins the markdown fact report: sections appear only when they have facts, the caps
/// word themselves, confidence rides every related line, and verbatim source cannot
/// break the document's own fences.
/// </summary>
public class MarkdownFactWriterTests
{
    private static SymbolDetail Detail() => new()
    {
        Id = 412,
        Name = "uart_init",
        Kind = SymbolKind.Function,
        RelativePath = "drivers/uart.c",
        Language = "C",
        StartLine = 9,
        EndLine = 24,
        Signature = "int uart_init(void)",
        ParameterText = "void",
    };

    private static SymbolContextReport Report(SymbolDetail detail) => new()
    {
        Detail = detail,
        RelatedLimit = 100,
    };

    [Fact]
    public void HeaderScopesTheClaimToOneSymbolsNeighbourhood()
    {
        var text = MarkdownFactWriter.Write(Report(Detail()));

        Assert.StartsWith("# `uart_init` — function", text);
        Assert.Contains("`drivers/uart.c:9` · C", text);
        Assert.Contains("this symbol and its resolved neighbourhood, nothing more", text);
        Assert.Contains("**Signature:** `int uart_init(void)`", text);
    }

    [Fact]
    public void ProvenanceRidesTheHeaderWhenGiven()
    {
        var report = Report(Detail()) with { Provenance = "index built 2026-08-16" };

        Assert.Contains("· index built 2026-08-16", MarkdownFactWriter.Write(report));
    }

    [Fact]
    public void EmptySectionsAreOmittedNotRenderedHollow()
    {
        var text = MarkdownFactWriter.Write(Report(Detail()));

        Assert.DoesNotContain("## Members", text);
        Assert.DoesNotContain("## Callers", text);
        Assert.DoesNotContain("## Callees", text);
        Assert.DoesNotContain("## I/O boundaries", text);
        Assert.DoesNotContain("## Same value elsewhere", text);
        Assert.DoesNotContain("## Unresolved references", text);
        Assert.DoesNotContain("## Source", text);
        Assert.DoesNotContain("## Inheritance", text);
    }

    [Fact]
    public void InheritanceNamesABaseTypeTheWorkspaceDoesNotDefine()
    {
        // The case the fact report has to get right, and the reason the section cannot be
        // left to Callers: `IDisposable` has no symbol here, so it can never be a caller of
        // anything. Printing the name and saying where it stops beats omitting the fact.
        var detail = Detail() with
        {
            BaseTypes =
            [
                new BaseTypeFact("SessionBase", 88, "core/session.cs", 12),
                new BaseTypeFact("IDisposable", null, null, null),
            ],
            DerivedTypes =
            [
                new RelatedSymbol(91, "ChildSession", SymbolKind.Class, "core/child.cs", 4,
                    ReferenceKind.Inherit, EdgeConfidence.Unique),
            ],
        };

        var text = MarkdownFactWriter.Write(Report(detail));

        Assert.Contains("## Inheritance", text);
        Assert.Contains("derives from `SessionBase` — `core/session.cs:12`", text);
        Assert.Contains("derives from `IDisposable` — not defined in this workspace", text);
        Assert.Contains("derived by `ChildSession` — `core/child.cs:4`", text);
    }

    [Fact]
    public void RelatedLinesCarryTheirConfidenceAndExactStaysUnadorned()
    {
        var detail = Detail() with
        {
            Callers =
            [
                new RelatedSymbol(1, "main", SymbolKind.Function, "app/main.c", 4,
                    ReferenceKind.Call, EdgeConfidence.Unique),
                new RelatedSymbol(2, "boot", SymbolKind.Function, "app/boot.c", 7,
                    ReferenceKind.Call, EdgeConfidence.Ambiguous),
            ],
        };

        var text = MarkdownFactWriter.Write(Report(detail));

        Assert.Contains("- `main` — call — `app/main.c:4`", text);
        Assert.Contains("- `boot` — call, one of several name matches — `app/boot.c:7`", text);
    }

    [Fact]
    public void AListAtTheCapSaysMoreMayExist()
    {
        var callers = Enumerable.Range(0, 100)
            .Select(i => new RelatedSymbol(i, $"caller_{i}", SymbolKind.Function,
                "a.c", i + 1, ReferenceKind.Call, EdgeConfidence.Unique))
            .ToList();

        var text = MarkdownFactWriter.Write(Report(Detail() with { Callers = callers }));

        Assert.Contains("## Callers (100 — query capped at 100, more may exist)", text);
    }

    [Fact]
    public void ABelowCapListStatesItsPlainCount()
    {
        var detail = Detail() with
        {
            Callers =
            [
                new RelatedSymbol(1, "main", SymbolKind.Function, "app/main.c", 4,
                    ReferenceKind.Call, EdgeConfidence.Unique),
            ],
        };

        var text = MarkdownFactWriter.Write(Report(detail));

        Assert.Contains("## Callers (1)\n", text);
        Assert.DoesNotContain("more may exist", text);
    }

    [Fact]
    public void CalleeCallSitesIndentUnderTheirCallee()
    {
        var callee = new RelatedSymbol(9, "uart_configure", SymbolKind.Function,
            "drivers/uart.c", 5, ReferenceKind.Call, EdgeConfidence.Unique);
        var report = Report(Detail() with { Callees = [callee] }) with
        {
            CalleeSites =
            [
                new CalleeCallSites(callee,
                    [new EdgeCallSite(10, "(UART_BAUD)", EdgeConfidence.Unique)]),
            ],
        };

        var text = MarkdownFactWriter.Write(report);

        Assert.Contains("- `uart_configure` — call — `drivers/uart.c:5`\n  - line 10: `uart_configure(UART_BAUD)`", text);
    }

    /// <summary>
    /// A reference with no arguments still has a site worth printing. Gating the line on
    /// arguments dropped the receiver — the evidence behind the confidence mark — from
    /// every use, which is most references now that a use may bind to a type's member.
    /// </summary>
    [Fact]
    public void ASiteWithAReceiverAndNoArgumentsStillShowsTheReceiver()
    {
        var callee = new RelatedSymbol(9, "MarkupElement", SymbolKind.EnumMember,
            "Domain/SymbolKind.cs", 34, ReferenceKind.Use, EdgeConfidence.Unique);
        var report = Report(Detail() with { Callees = [callee] }) with
        {
            CalleeSites =
            [
                new CalleeCallSites(callee,
                    [new EdgeCallSite(632, null, EdgeConfidence.Unique, "SymbolKind", "MarkupElement")]),
            ],
        };

        var text = MarkdownFactWriter.Write(report);

        Assert.Contains("- line 632: `SymbolKind.MarkupElement`", text);
    }

    [Fact]
    public void IoSitesNameDirectionSourceAndGate()
    {
        var report = Report(Detail()) with
        {
            IoSites =
            [
                new IoBoundarySite
                {
                    RefId = 1,
                    Name = "Write",
                    Direction = IoDirection.Output,
                    Origin = IoMatchOrigin.Catalog,
                    Family = ".NET SerialPort",
                    GateNote = "name match in a file that references SerialPort",
                    RelativePath = "app/Link.cs",
                    Line = 33,
                    ArgumentText = "(frame, 0, len)",
                },
            ],
        };

        var text = MarkdownFactWriter.Write(report);

        Assert.Contains("## I/O boundaries (1)", text);
        Assert.Contains("never derived from syntax", text);
        Assert.Contains("- `Write` — output (catalog: .NET SerialPort) — `app/Link.cs:33` — `(frame, 0, len)`", text);
        Assert.Contains("  - matched because: name match in a file that references SerialPort", text);
    }

    [Fact]
    public void SameValueSectionRepeatsTheEvidenceSentence()
    {
        var report = Report(Detail()) with
        {
            SameValue = new ValueMatchSet
            {
                Matches =
                [
                    new ValueMatch(7, "CMD_READ", SymbolKind.Macro, null, "0xA5",
                        165, null, "C", "fw/protocol.h", 12),
                ],
                Canonical = "165",
                Limit = 50,
            },
        };

        var text = MarkdownFactWriter.Write(report);

        Assert.Contains("## Same value elsewhere (1, 165)", text);
        Assert.Contains(ValueFacts.EvidenceSentence, text);
        Assert.Contains("- `CMD_READ` — macro, 0xA5 = 165 — numerically equal — `fw/protocol.h:12` [C]", text);
    }

    [Fact]
    public void SourceFenceOutgrowsBackticksInTheExcerpt()
    {
        var report = Report(Detail()) with
        {
            SourceExcerpt = "var fence = \"```\";",
            SourceExcerptEndLine = 9,
        };

        var text = MarkdownFactWriter.Write(report);

        Assert.Contains("````c\n", text);
        Assert.Contains("\n````\n", text);
    }

    [Fact]
    public void ATruncatedExcerptSaysWhatWasCut()
    {
        var report = Report(Detail() with { EndLine = 400 }) with
        {
            SourceExcerpt = "int uart_init(void) {",
            SourceExcerptEndLine = 128,
            SourceTruncated = true,
        };

        var text = MarkdownFactWriter.Write(report);

        Assert.Contains("## Source (lines 9–128 of 9–400 — truncated)", text);
    }

    [Fact]
    public void CSharpFencesAsCsharp()
    {
        var report = Report(Detail() with { Language = "C#" }) with
        {
            SourceExcerpt = "public int X;",
            SourceExcerptEndLine = 9,
        };

        Assert.Contains("```csharp\n", MarkdownFactWriter.Write(report));
    }

    [Fact]
    public void UnresolvedReferencesSayWhatTheyAre()
    {
        var detail = Detail() with
        {
            UnresolvedReferences = [new UnresolvedReference("memset", ReferenceKind.Call, 14)],
        };

        var text = MarkdownFactWriter.Write(Report(detail));

        Assert.Contains("## Unresolved references (1)", text);
        Assert.Contains("no definition is in the index", text);
        Assert.Contains("- `memset` — call, line 14", text);
    }
}
