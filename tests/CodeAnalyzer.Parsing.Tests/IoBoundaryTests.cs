using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using CodeAnalyzer.Core.Workspaces;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// One indexed workspace shared by every test in the class: a C# software side talking
/// over SerialPort, a C firmware side on the STM32 HAL, and a Python tool on pyserial —
/// the exact shape the I/O boundary feature exists for. Catalog and marks are query-time
/// arguments, so the tests vary them without re-indexing.
/// </summary>
public sealed class IoBoundaryFixture : IDisposable
{
    public IoBoundaryFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "codeanalyzer-io", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        WriteFile("software/Comms.cs", """
            using System.IO.Ports;

            namespace Software;

            public class Comms
            {
                private readonly SerialPort _port = new SerialPort("COM3", 115200);

                public void SendFrame(byte[] frame)
                {
                    _port.Write(frame, 0, frame.Length);
                }

                public string ReadReply()
                {
                    return _port.ReadExisting();
                }
            }
            """);

        // Calls something named Write in a file that never references an I/O type: the
        // co-occurrence gate must keep this out.
        WriteFile("software/Logger.cs", """
            namespace Software;

            public class Logger
            {
                private readonly System.Text.StringBuilder _buffer = new();

                public void Save(string text)
                {
                    _buffer.Write(text);
                }
            }
            """);

        WriteFile("firmware/uart.c", """
            #include "stm32g4xx_hal.h"

            extern UART_HandleTypeDef huart1;
            extern SPI_HandleTypeDef hspi1;

            static unsigned char rx_buf[8];

            void hmi_poll(void)
            {
                HAL_UART_Receive(&huart1, rx_buf, 8, 100);
            }

            void hmi_send(unsigned char *tx, int n)
            {
                HAL_UART_Transmit_IT(&huart1, tx, n);
            }

            void spi_exchange(unsigned char *tx, unsigned char *rx)
            {
                HAL_SPI_TransmitReceive(&hspi1, tx, rx, 4, 10);
            }

            void frame_send(unsigned char *frame, int n)
            {
                hmi_send(frame, n);
            }

            void app_run(void)
            {
                frame_send(rx_buf, 8);
            }

            typedef struct {
                unsigned char sync;
                unsigned char cmd;
                unsigned short value;
            } hmi_frame;

            static hmi_frame tx_frame;

            void hmi_send_frame(void)
            {
                HAL_UART_Transmit(&huart1, (unsigned char *)&tx_frame, 4, 50);
            }
            """);

        WriteFile("tools/reader.py", """
            import serial

            def poll(ser):
                ser.write(b"\x42")
                return ser.read(8)
            """);

        // Same member names, no serial import: the dependency gate must keep this out.
        WriteFile("tools/notes.py", """
            def save(f, data):
                f.write(data)
                return f.read()
            """);

        Session = WorkspaceSession.Open(Root, new TreeSitterAnalyzerFactory());
        Session.IndexAsync([string.Empty]).GetAwaiter().GetResult();
    }

    public string Root { get; }

    public WorkspaceSession Session { get; }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        Session.Dispose();
        WorkspaceCacheCleanup.Delete(Root);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures are not test failures.
        }
    }
}

public class IoBoundaryTests(IoBoundaryFixture fixture) : IClassFixture<IoBoundaryFixture>
{
    private static readonly IReadOnlyList<IoMark> NoMarks = [];

    private List<IoBoundarySite> AllSites(IReadOnlyList<IoMark>? marks = null) =>
        fixture.Session.Read(() =>
            fixture.Session.IoBoundaries.GetAllSites(IoCatalog.BuiltIn.Entries, marks ?? NoMarks));

    [Fact]
    public void HalCallsSurfaceWithTheirDocumentedDirections()
    {
        var sites = AllSites();

        var receive = Assert.Single(sites, s => s.Name == "HAL_UART_Receive");
        Assert.Equal(IoDirection.Input, receive.Direction);
        Assert.Equal(IoMatchOrigin.Catalog, receive.Origin);
        Assert.Equal("STM32 HAL", receive.Family);
        Assert.Equal("firmware/uart.c", receive.RelativePath);
        Assert.Equal("(&huart1, rx_buf, 8, 100)", receive.ArgumentText);
        // A bare C name distinctive enough to ship ungated states no gate.
        Assert.Null(receive.GateNote);

        // _IT variant reached through the prefix, not an exact name.
        var transmit = Assert.Single(sites, s => s.Name == "HAL_UART_Transmit_IT");
        Assert.Equal(IoDirection.Output, transmit.Direction);
    }

    [Fact]
    public void TheLongerPrefixWinsWhereTwoCatalogEntriesOverlap()
    {
        var sites = AllSites().Where(s => s.Name == "HAL_SPI_TransmitReceive").ToList();

        // One site, inout — not a second "out" from the HAL_SPI_Transmit prefix.
        var site = Assert.Single(sites);
        Assert.Equal(IoDirection.InOut, site.Direction);
    }

    [Fact]
    public void AMemberNameNeedsItsTypeInTheSameFile()
    {
        var sites = AllSites();

        var write = Assert.Single(sites, s => s.Name == "Write");
        Assert.Equal("software/Comms.cs", write.RelativePath);
        Assert.Equal(IoDirection.Output, write.Direction);
        Assert.NotNull(write.GateNote);
        Assert.Contains("SerialPort", write.GateNote);

        var read = Assert.Single(sites, s => s.Name == "ReadExisting");
        Assert.Equal(IoDirection.Input, read.Direction);

        // Logger.cs calls .Write too, but references no I/O type: no site anywhere in it.
        Assert.DoesNotContain(sites, s => s.RelativePath == "software/Logger.cs");
    }

    [Fact]
    public void APythonMemberNameNeedsItsImportInTheSameFile()
    {
        var sites = AllSites();

        var write = Assert.Single(sites, s => s.Name == "write");
        Assert.Equal("tools/reader.py", write.RelativePath);
        Assert.Equal(IoDirection.Output, write.Direction);
        Assert.Equal("pyserial", write.Family);

        var read = Assert.Single(sites, s => s.Name == "read");
        Assert.Equal("tools/reader.py", read.RelativePath);
        Assert.Equal(IoDirection.Input, read.Direction);

        // notes.py writes and reads too, but imports nothing: silence.
        Assert.DoesNotContain(sites, s => s.RelativePath == "tools/notes.py");
    }

    [Fact]
    public void AUserMarkTurnsAnyFunctionIntoABoundary()
    {
        var sites = AllSites([new IoMark { Name = "frame_send", Direction = IoDirection.Output }]);

        var site = Assert.Single(sites, s => s.Name == "frame_send");
        Assert.Equal(IoMatchOrigin.UserMark, site.Origin);
        Assert.Equal(IoDirection.Output, site.Direction);
        Assert.Null(site.Family);
        Assert.Equal("(rx_buf, 8)", site.ArgumentText);
    }

    [Fact]
    public void AMarkWithDirectionNoneSuppressesTheCatalogMatch()
    {
        var sites = AllSites([new IoMark { Name = "HAL_UART_Receive", Direction = IoDirection.None }]);

        Assert.DoesNotContain(sites, s => s.Name == "HAL_UART_Receive");
        // Only the named calls are suppressed; the rest of the catalog still speaks.
        Assert.Contains(sites, s => s.Name == "HAL_UART_Transmit_IT");
    }

    [Fact]
    public void AMarkOverridesTheCatalogRatherThanDuplicatingIt()
    {
        var sites = AllSites([new IoMark { Name = "HAL_UART_Receive", Direction = IoDirection.Output }]);

        // The user's assertion replaces the catalog's answer — one site, theirs.
        var site = Assert.Single(sites, s => s.Name == "HAL_UART_Receive");
        Assert.Equal(IoMatchOrigin.UserMark, site.Origin);
        Assert.Equal(IoDirection.Output, site.Direction);
    }

    [Fact]
    public void AScopedMarkStopsAtItsDirectory()
    {
        var sites = AllSites([new IoMark { Name = "write", Direction = IoDirection.Output, Scope = "tools" }]);

        // Inside the scope the mark takes over — including notes.py, which the catalog's
        // gate had excluded: the user said calls named write in tools/ are boundaries.
        var toolsSites = sites.Where(s => s.Name == "write").ToList();
        Assert.Equal(2, toolsSites.Count);
        Assert.All(toolsSites, s => Assert.Equal(IoMatchOrigin.UserMark, s.Origin));
        Assert.Contains(toolsSites, s => s.RelativePath == "tools/notes.py");

        // Outside the scope nothing changes: the C# Write stays a catalog match.
        var csharpWrite = Assert.Single(sites, s => s.Name == "Write");
        Assert.Equal(IoMatchOrigin.Catalog, csharpWrite.Origin);
    }

    [Fact]
    public void CallerScopedQueryReturnsOnlyThatCallersSites()
    {
        var pollId = FindSymbol("hmi_poll");

        var sites = fixture.Session.Read(() =>
            fixture.Session.IoBoundaries.GetSitesForCallers([pollId], IoCatalog.BuiltIn.Entries, NoMarks));

        var site = Assert.Single(sites);
        Assert.Equal("HAL_UART_Receive", site.Name);
        Assert.Equal(pollId, site.CallerSymbolId);
    }

    [Fact]
    public void SiteDetailsCarryTheCallerAndTheVerbatimArguments()
    {
        var receive = AllSites().Single(s => s.Name == "HAL_UART_Receive");

        var details = fixture.Session.Read(() =>
            fixture.Session.IoBoundaries.GetSiteDetails([receive.RefId]));

        var detail = Assert.Single(details);
        Assert.Equal("HAL_UART_Receive", detail.Name);
        Assert.Equal("firmware/uart.c", detail.RelativePath);
        Assert.Equal("C", detail.Language);
        Assert.Equal("hmi_poll", detail.CallerName);
        Assert.Equal("(&huart1, rx_buf, 8, 100)", detail.ArgumentText);
    }

    [Fact]
    public void ThePacketFramingChainReachesTheStructMembers()
    {
        var transmit = AllSites().Single(s => s.Name == "HAL_UART_Transmit");

        var frame = fixture.Session.Read(() =>
            fixture.Session.IoBoundaries.GetPacketFraming(transmit.RefId));

        // tx_frame → its definition → declared type hmi_frame → the workspace struct →
        // the members. That chain is the packet layout on the wire.
        var argument = Assert.Single(frame, a => a.Token == "tx_frame");
        Assert.False(argument.IsUnresolved);
        Assert.Equal("hmi_frame", argument.StructName);
        Assert.Equal(["sync", "cmd", "value"], argument.Members.Select(m => m.Name));

        // huart1's type is not defined in this workspace: the chain states what it can
        // and stops, rather than inventing a layout.
        var handle = Assert.Single(frame, a => a.Token == "huart1");
        Assert.Null(handle.StructName);
        Assert.Empty(handle.Members);
    }

    [Fact]
    public void AnEmptyCatalogAndNoMarksAnswerNothing()
    {
        var sites = fixture.Session.Read(() =>
            fixture.Session.IoBoundaries.GetAllSites([], NoMarks));

        Assert.Empty(sites);
    }

    private long FindSymbol(string name) =>
        fixture.Session.Read(() =>
            fixture.Session.Search.Search(name).First(h => h.Name == name).SymbolId);
}
