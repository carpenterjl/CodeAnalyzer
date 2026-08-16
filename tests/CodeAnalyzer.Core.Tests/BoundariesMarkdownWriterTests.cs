using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Export;
using CodeAnalyzer.Core.Graph;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// The boundaries table goes through <see cref="ViewPayloadBuilder"/> first — the same
/// grouping the Boundaries view draws — so these tests pin the whole host-side path:
/// sites in, markdown out.
/// </summary>
public class BoundariesMarkdownWriterTests
{
    private static IoBoundarySite Site(
        string name,
        IoDirection direction,
        string path = "fw/main.c",
        string? caller = "HMI_Write_Data",
        string? argText = "(frame, 8)",
        IoMatchOrigin origin = IoMatchOrigin.Catalog,
        string? family = "STM32 HAL",
        string? gate = null) => new()
        {
            RefId = 1,
            CallerName = caller,
            Name = name,
            Direction = direction,
            Origin = origin,
            Family = family,
            GateNote = gate,
            RelativePath = path,
            Line = 42,
            ArgumentText = argText,
        };

    private static string Write(params IoBoundarySite[] sites) =>
        BoundariesMarkdownWriter.Write(ViewPayloadBuilder.Build(sites), "demo-workspace");

    [Fact]
    public void HeaderNamesTheWorkspaceAndTheDirectionRule()
    {
        var text = Write(Site("HAL_UART_Transmit", IoDirection.Output));

        Assert.StartsWith("# I/O boundaries — demo-workspace", text);
        Assert.Contains("never derived from syntax", text);
        Assert.Contains("1 call sites", text);
    }

    [Fact]
    public void OutputsAndInputsAreSeparateTablesGroupedByTopDirectory()
    {
        var text = Write(
            Site("HAL_UART_Transmit", IoDirection.Output, "fw/main.c"),
            Site("ReadLine", IoDirection.Input, "app/Link.cs", family: ".NET SerialPort"));

        Assert.Contains("## Outputs — data leaving", text);
        Assert.Contains("## Inputs — data arriving", text);
        Assert.Contains("### fw", text);
        Assert.Contains("### app", text);
        Assert.Contains("| `HAL_UART_Transmit` | `HMI_Write_Data` | `fw/main.c:42` | `(frame, 8)` | catalog: STM32 HAL |", text);
    }

    [Fact]
    public void AnInOutSiteAppearsInBothTables()
    {
        var text = Write(Site("HAL_SPI_TransmitReceive", IoDirection.InOut));

        var outputs = text.IndexOf("## Outputs", StringComparison.Ordinal);
        var inputs = text.IndexOf("## Inputs", StringComparison.Ordinal);
        Assert.True(outputs >= 0 && inputs >= 0);
        Assert.Contains("HAL_SPI_TransmitReceive", text[outputs..inputs]);
        Assert.Contains("HAL_SPI_TransmitReceive", text[inputs..]);
    }

    [Fact]
    public void AGateNoteRidesTheSourceCell()
    {
        var text = Write(Site("Write", IoDirection.Output, "app/Link.cs",
            family: ".NET SerialPort", gate: "name match in a file that references SerialPort"));

        Assert.Contains("catalog: .NET SerialPort — name match in a file that references SerialPort |", text);
    }

    [Fact]
    public void AUserMarkNamesTheUserAsTheSource()
    {
        var text = Write(Site("SendResponse", IoDirection.Output,
            origin: IoMatchOrigin.UserMark, family: null));

        Assert.Contains("| your mark |", text);
    }

    [Fact]
    public void PipesInVerbatimArgumentsCannotEndTheCell()
    {
        var text = Write(Site("Write", IoDirection.Output, argText: "(FLAG_A | FLAG_B)"));

        Assert.Contains(@"`(FLAG_A \| FLAG_B)`", text);
    }

    [Fact]
    public void NoSitesSaysSoInsteadOfRenderingEmptyTables()
    {
        var text = Write();

        Assert.Contains("No catalog API matched and nothing is marked.", text);
        Assert.DoesNotContain("## Outputs", text);
    }
}
