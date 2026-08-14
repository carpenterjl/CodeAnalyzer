using CodeAnalyzer.Core.Domain;
using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// The search filters and the graph's colours must offer the same families, or picking
/// "type" would hide something the legend calls a type.
/// </summary>
public class SymbolKindGroupsTests
{
    [Fact]
    public void EveryKindLandsInExactlyOneOfferedGroup()
    {
        foreach (var kind in Enum.GetValues<SymbolKind>())
        {
            Assert.Contains(SymbolKindGroups.For(kind), SymbolKindGroups.All);
        }
    }

    [Fact]
    public void SelectingEveryGroupSelectsEveryKind()
    {
        // The "nothing selected means no filter" rule only holds if the two states are
        // otherwise the same: an all-on filter must not quietly drop a kind.
        var kinds = SymbolKindGroups.KindsIn(SymbolKindGroups.All);

        Assert.Equal(Enum.GetValues<SymbolKind>().Length, kinds.Count);
    }

    [Fact]
    public void AnInterfaceIsItsOwnGroupRatherThanATypeMatchingItsOwnColourOnTheCanvas()
    {
        Assert.Equal("interface", SymbolKindGroups.For(SymbolKind.Interface));
        Assert.Equal("type", SymbolKindGroups.For(SymbolKind.Class));

        Assert.DoesNotContain(SymbolKind.Interface, SymbolKindGroups.KindsIn(["type"]));
    }

    [Fact]
    public void EveryOtherGroupIsTheOneTheGraphAlreadyColoursBy()
    {
        // Derived from KindLabels.GroupFor rather than restated, so the two cannot drift.
        foreach (var kind in Enum.GetValues<SymbolKind>())
        {
            if (kind != SymbolKind.Interface)
            {
                Assert.Equal(KindLabels.GroupFor(kind), SymbolKindGroups.For(kind));
            }
        }
    }

    [Fact]
    public void AnUnknownGroupNameContributesNothingInsteadOfThrowing()
    {
        // Filter names can arrive from a saved preference written by another build.
        Assert.Empty(SymbolKindGroups.KindsIn(["constellation"]));
        Assert.Equal(SymbolKindGroups.KindsIn(["macro"]), SymbolKindGroups.KindsIn(["macro", "constellation"]));
    }
}
