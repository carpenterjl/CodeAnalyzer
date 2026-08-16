using CodeAnalyzer.Cli.Querying;
using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Cli.Tests;

[Collection("cli-workspace")]
public class SymbolLocatorTests(CliWorkspaceFixture fixture)
{
    private AgentToolset Toolset => new(fixture.Session);

    [Fact]
    public void AUniqueNameResolvesDirectly()
    {
        var result = Toolset.Locate("uart_init");

        var resolved = Assert.IsType<LocateResult.Resolved>(result);
        Assert.Equal("uart_init", resolved.Symbol.Name);
        Assert.Equal("drivers/uart.c", resolved.Symbol.RelativePath);
    }

    [Fact]
    public void AnIdFromAPreviousResultResolves()
    {
        var byName = Assert.IsType<LocateResult.Resolved>(Toolset.Locate("uart_init"));

        var byId = Toolset.Locate($"#{byName.Symbol.Id}");

        var resolved = Assert.IsType<LocateResult.Resolved>(byId);
        Assert.Equal(byName.Symbol.Id, resolved.Symbol.Id);
    }

    [Fact]
    public void ANameDefinedTwiceIsAnsweredWithBothCandidates_NeverAGuess()
    {
        var result = Toolset.Locate("init");

        var ambiguous = Assert.IsType<LocateResult.Ambiguous>(result);
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.False(ambiguous.More);

        var paths = ambiguous.Candidates.Select(c => c.RelativePath).ToList();
        Assert.Contains("app/main.c", paths);
        Assert.Contains("drivers/init.c", paths);
    }

    [Fact]
    public void APathPrefixDisambiguates()
    {
        var result = Toolset.Locate("drivers/init.c:init");

        var resolved = Assert.IsType<LocateResult.Resolved>(result);
        Assert.Equal("drivers/init.c", resolved.Symbol.RelativePath);
    }

    [Fact]
    public void ThePathMayBeAnySuffixOfTheRelativePath()
    {
        var result = Toolset.Locate("init.c:init");

        var resolved = Assert.IsType<LocateResult.Resolved>(result);
        Assert.Equal("drivers/init.c", resolved.Symbol.RelativePath);
    }

    [Fact]
    public void AnUnknownNameGetsSuggestionsInsteadOfSilence()
    {
        var result = Toolset.Locate("uart_wrte");

        var notFound = Assert.IsType<LocateResult.NotFound>(result);
        Assert.Contains("uart_wrte", notFound.Message);
        Assert.Contains(notFound.Suggestions, s => s.Name == "uart_write");
    }

    [Fact]
    public void AStaleIdSaysWhatHappenedAndWhatToDo()
    {
        var result = Toolset.Locate("#999999");

        var notFound = Assert.IsType<LocateResult.NotFound>(result);
        Assert.Contains("no longer in the index", notFound.Message);
        Assert.Contains("re-run search", notFound.Message);
    }

    [Fact]
    public void ATypeWinsOverTheConstructorItDeclares()
    {
        // Through the real index: PacketWriter is a class and a constructor sharing its
        // name, and asking for it used to cost a round trip to be told so.
        var result = Toolset.Locate("PacketWriter");

        var resolved = Assert.IsType<LocateResult.Resolved>(result);
        Assert.Equal(SymbolKind.Class, resolved.Symbol.Kind);
    }

    [Fact]
    public void TheConstructorIsStillReachableByItsOwnId()
    {
        // The rule picks a winner for a bare name; it must not make the loser unreachable.
        var type = Assert.IsType<LocateResult.Resolved>(Toolset.Locate("PacketWriter")).Symbol;
        var detail = Toolset.GetDetail(type.Id);

        var constructor = Assert.Single(detail!.Members, m => m.Name == "PacketWriter");

        var byId = Assert.IsType<LocateResult.Resolved>(Toolset.Locate($"#{constructor.Id}"));
        Assert.Equal(SymbolKind.Method, byId.Symbol.Kind);
    }

    [Fact]
    public void AMissedContainerDotMemberSuggestsOnTheMemberName()
    {
        // Found by walking into it: asking for a member that does not exist under a
        // container that does used to return the miss with no suggestions at all,
        // because the fuzzy scorer was handed the whole dotted string. The scorer matches
        // a subsequence of a bare name, and no name contains that dot.
        var result = Toolset.Locate("Protocol.CmdRed");

        var notFound = Assert.IsType<LocateResult.NotFound>(result);
        Assert.Contains("Protocol.CmdRed", notFound.Message);
        Assert.Contains(notFound.Suggestions, s => s.Name == "CmdRead");
    }

    // The rule itself, at its edges. These are the cases where resolving would be a guess
    // rather than a reading of what the source says.

    [Fact]
    public void TwoSameNamedTypesStayAmbiguous()
    {
        var candidates = new[]
        {
            Type(1, "Frame"),
            Type(2, "Frame"),
        };

        Assert.Null(SymbolLocator.TypeContainingEveryOtherCandidate(candidates));
    }

    [Fact]
    public void AMemberDeclaredBySomeOtherTypeStaysAmbiguous()
    {
        var candidates = new[]
        {
            Type(1, "Frame"),
            Member(2, "Frame", containerId: 99),
        };

        Assert.Null(SymbolLocator.TypeContainingEveryOtherCandidate(candidates));
    }

    [Fact]
    public void TwoFreeFunctionsSharingANameStayAmbiguous()
    {
        var candidates = new[]
        {
            Member(1, "init", containerId: null),
            Member(2, "init", containerId: null),
        };

        Assert.Null(SymbolLocator.TypeContainingEveryOtherCandidate(candidates));
    }

    [Fact]
    public void ACappedCandidateListIsRefused()
    {
        // Past the cap the list is a sample, and the row that was cut could be a second
        // type. Refusing is the only answer that cannot be wrong.
        var candidates = new List<LocatedSymbol> { Type(1, "Frame") };
        for (var i = 2; i <= SymbolLocator.MaxCandidates + 1; i++)
        {
            candidates.Add(Member(i, "Frame", containerId: 1));
        }

        Assert.Null(SymbolLocator.TypeContainingEveryOtherCandidate(candidates));
    }

    [Fact]
    public void SeveralMembersOfTheOneTypeAllResolveToIt()
    {
        // C++ gives a type a constructor and a destructor; overloaded constructors give it
        // several. All of them are declared by the type, so the type still wins.
        var candidates = new[]
        {
            Type(1, "Frame"),
            Member(2, "Frame", containerId: 1),
            Member(3, "Frame", containerId: 1),
        };

        var winner = SymbolLocator.TypeContainingEveryOtherCandidate(candidates);

        Assert.NotNull(winner);
        Assert.Equal(1, winner.Id);
    }

    private static LocatedSymbol Type(long id, string name) =>
        new(id, name, SymbolKind.Class, null, "a.cs", 1);

    private static LocatedSymbol Member(long id, string name, long? containerId) =>
        new(id, name, SymbolKind.Method, "()", "a.cs", 2, containerId);
}
