using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

public class IoStubTests
{
    private static IoBoundarySite Site(
        long refId,
        long? caller,
        string name = "HAL_UART_Transmit",
        IoDirection direction = IoDirection.Output,
        IoMatchOrigin origin = IoMatchOrigin.Catalog,
        string? family = "STM32 HAL",
        string? gateNote = null) => new()
    {
        RefId = refId,
        CallerSymbolId = caller,
        Name = name,
        Direction = direction,
        Origin = origin,
        Family = family,
        GateNote = gateNote,
        RelativePath = "firmware/uart.c",
        Line = (int)refId,
        ArgumentText = $"(arg{refId})",
    };

    [Fact]
    public void SitesMergeIntoOneStubPerCallerAndApi()
    {
        var stubs = IoBoundaryService.GroupIntoStubs(
        [
            Site(1, caller: 10),
            Site(2, caller: 10),
            Site(3, caller: 11),
        ]);

        Assert.Equal(2, stubs.Count);

        var merged = Assert.Single(stubs, s => s.CallerSymbolId == 10);
        Assert.Equal([1L, 2L], merged.RefIds);
        // The first site's arguments stand for the stub; the rest arrive on click.
        Assert.Equal("(arg1)", merged.ArgumentText);
    }

    [Fact]
    public void ASiteWithoutACallerHasNoNodeToHangOff()
    {
        var stubs = IoBoundaryService.GroupIntoStubs([Site(1, caller: null)]);

        Assert.Empty(stubs);
    }

    [Fact]
    public void TwoSatisfiedGatesOnOneCallKeepBothFamiliesVisible()
    {
        var stubs = IoBoundaryService.GroupIntoStubs(
        [
            Site(1, caller: 10, name: "Write", family: ".NET SerialPort",
                gateNote: "in a file that references SerialPort"),
            Site(1, caller: 10, name: "Write", family: "HID",
                gateNote: "in a file that references HidDevice or HidStream"),
        ]);

        var stub = Assert.Single(stubs);
        Assert.Equal(".NET SerialPort / HID", stub.Family);
        Assert.Contains("SerialPort", stub.GateNote);
        Assert.Contains("HidDevice", stub.GateNote);
        // One call site, even though two entries matched it.
        Assert.Equal([1L], stub.RefIds);
    }

    [Fact]
    public void ThePayloadCarriesTheStubWithStringIdsAndItsSource()
    {
        var fragment = new GraphFragment
        {
            FocusId = 10,
            Nodes =
            [
                new GraphNode
                {
                    Id = 10,
                    Name = "hmi_poll",
                    Kind = SymbolKind.Function,
                    RelativePath = "firmware/uart.c",
                    Line = 5,
                },
            ],
            IoStubs =
            [
                new IoStub
                {
                    CallerSymbolId = 10,
                    Name = "HAL_UART_Receive",
                    Direction = IoDirection.Input,
                    Origin = IoMatchOrigin.Catalog,
                    Family = "STM32 HAL",
                    ArgumentText = "(&huart1, rx, 8, 100)",
                    RefIds = [42],
                },
                new IoStub
                {
                    CallerSymbolId = 10,
                    Name = "frame_send",
                    Direction = IoDirection.Output,
                    Origin = IoMatchOrigin.UserMark,
                    RefIds = [43, 44],
                },
            ],
        };

        var payload = GraphPayloadBuilder.Build(fragment);

        Assert.Equal(2, payload.IoStubs.Count);

        var catalogStub = payload.IoStubs[0];
        Assert.Equal("io:10:HAL_UART_Receive:in", catalogStub.Id);
        Assert.Equal("10", catalogStub.Caller);
        Assert.Equal("in", catalogStub.Direction);
        Assert.Equal("input", catalogStub.DirectionLabel);
        Assert.Equal("catalog: STM32 HAL", catalogStub.Source);
        Assert.Equal(["42"], catalogStub.RefIds);
        Assert.Equal(1, catalogStub.SiteCount);

        var markStub = payload.IoStubs[1];
        Assert.Equal("your mark", markStub.Source);
        Assert.Equal("out", markStub.Direction);
        Assert.Equal(2, markStub.SiteCount);
    }

    [Fact]
    public void AFragmentWithoutStubsSerialisesAnEmptyList()
    {
        var payload = GraphPayloadBuilder.Build(new GraphFragment());

        Assert.Empty(payload.IoStubs);
    }

    [Fact]
    public void TheBoundariesViewGroupsByTopDirectoryAndSplitsByDirection()
    {
        var sites = new List<IoBoundarySite>
        {
            Site(1, caller: 10, name: "Write", direction: IoDirection.Output) with
            {
                RelativePath = "software/Comms.cs",
                CallerName = "SendFrame",
            },
            Site(2, caller: 11, name: "HAL_UART_Receive", direction: IoDirection.Input) with
            {
                RelativePath = "firmware/uart.c",
            },
            Site(3, caller: 12, name: "HAL_SPI_TransmitReceive", direction: IoDirection.InOut) with
            {
                RelativePath = "firmware/spi.c",
            },
        };

        var payload = ViewPayloadBuilder.Build(sites);

        Assert.Equal(3, payload.TotalSites);

        // The inout site does both, so it appears on both sides — stated, not hidden.
        Assert.Equal(["firmware", "software"], payload.Outputs.Select(g => g.Id));
        Assert.Equal(["firmware"], payload.Inputs.Select(g => g.Id).Distinct());

        var output = payload.Outputs.Single(g => g.Id == "software").Sites.Single();
        Assert.Equal("Write", output.Name);
        Assert.Equal("SendFrame", output.Caller);
        Assert.Equal("10", output.CallerId);

        var inputNames = payload.Inputs.SelectMany(g => g.Sites).Select(s => s.Name).ToList();
        Assert.Contains("HAL_UART_Receive", inputNames);
        Assert.Contains("HAL_SPI_TransmitReceive", inputNames);
    }
}
