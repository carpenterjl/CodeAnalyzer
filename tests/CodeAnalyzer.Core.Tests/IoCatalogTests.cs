using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Core.Graph;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

public class IoCatalogTests
{
    [Fact]
    public void TheShippedCatalogLoadsWithoutASingleRejectedEntry()
    {
        var catalog = IoCatalog.BuiltIn;

        Assert.Empty(catalog.Errors);
        Assert.NotEmpty(catalog.Entries);
    }

    /// <summary>
    /// The design rule the catalog file states in its own comment: member-call languages
    /// store only the member name, so their entries must carry a co-occurrence gate. An
    /// ungated "Write" for C# would flood every workspace with false stubs. C entries may
    /// go ungated only because bare C call names like HAL_UART_Transmit are distinctive.
    /// </summary>
    [Fact]
    public void EveryMemberNameLanguageEntryIsGated()
    {
        foreach (var entry in IoCatalog.BuiltIn.Entries)
        {
            if (entry.Languages.Contains(LanguageNames.CSharp)
                || entry.Languages.Contains(LanguageNames.Python))
            {
                Assert.True(entry.IsGated, $"{entry.Family}/{entry.Name ?? entry.Prefix} is ungated");
            }
        }
    }

    [Fact]
    public void EveryEntryHasExactlyOneMatchForm()
    {
        foreach (var entry in IoCatalog.BuiltIn.Entries)
        {
            Assert.True(
                (entry.Name is null) != (entry.Prefix is null),
                $"{entry.Family}: name and prefix are not mutually exclusive");
        }
    }

    [Fact]
    public void ABadEntryIsRejectedAloneAndTheRestSurvive()
    {
        var catalog = IoCatalog.Parse("""
            {
              "entries": [
                { "family": "Good", "languages": ["C"], "name": "uart_send", "direction": "out" },
                { "family": "NoDirection", "languages": ["C"], "name": "x", "direction": "sideways" },
                { "family": "NoLanguage", "languages": [], "name": "y", "direction": "in" },
                { "family": "TypoLanguage", "languages": ["c"], "name": "z", "direction": "in" },
                { "family": "BothForms", "languages": ["C"], "name": "a", "prefix": "a_", "direction": "in" },
                { "family": "NeitherForm", "languages": ["C"], "direction": "in" },
                { "languages": ["C"], "name": "b", "direction": "in" },
                "not even an object",
                { "family": "AlsoGood", "languages": ["Python"], "name": "recv", "direction": "in",
                  "requiresDependency": ["socket"] }
              ]
            }
            """);

        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal("Good", catalog.Entries[0].Family);
        Assert.Equal("AlsoGood", catalog.Entries[1].Family);
        Assert.Equal(7, catalog.Errors.Count);
    }

    [Fact]
    public void AMissingEntriesArrayIsAnErrorNotACrash()
    {
        var catalog = IoCatalog.Parse("""{ "something": 1 }""");

        Assert.Empty(catalog.Entries);
        Assert.Single(catalog.Errors);
    }

    [Theory]
    [InlineData("in", IoDirection.Input)]
    [InlineData("out", IoDirection.Output)]
    [InlineData("inout", IoDirection.InOut)]
    public void DirectionsParseToTheirEnumValues(string text, IoDirection expected)
    {
        var catalog = IoCatalog.Parse($$"""
            { "entries": [ { "family": "F", "languages": ["C"], "name": "n", "direction": "{{text}}" } ] }
            """);

        Assert.Equal(expected, Assert.Single(catalog.Entries).Direction);
    }

    [Fact]
    public void TheGateNoteStatesTheRuleTheMatchWillRelyOn()
    {
        var typeGated = new IoCatalogEntry
        {
            Family = "F",
            Languages = [LanguageNames.CSharp],
            Name = "Write",
            Direction = IoDirection.Output,
            RequiredTypeRefs = ["SerialPort", "HidStream"],
        };
        Assert.Equal("in a file that references SerialPort or HidStream", typeGated.GateNote);

        var depGated = new IoCatalogEntry
        {
            Family = "F",
            Languages = [LanguageNames.C],
            Name = "send",
            Direction = IoDirection.Output,
            RequiredDependencies = ["sys/socket.h", "winsock2.h"],
        };
        Assert.Equal("in a file that depends on sys/socket.h or winsock2.h", depGated.GateNote);

        var ungated = new IoCatalogEntry
        {
            Family = "F",
            Languages = [LanguageNames.C],
            Prefix = "HAL_UART_Transmit",
            Direction = IoDirection.Output,
        };
        Assert.Null(ungated.GateNote);
    }
}
