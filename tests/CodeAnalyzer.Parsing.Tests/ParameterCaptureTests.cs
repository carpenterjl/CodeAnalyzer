using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Parsing;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// Pins a decision rather than a behaviour: no query pack captures a parameter, so
/// <see cref="SymbolKind.Parameter"/> was taken out of the reference resolver's referencable
/// set. If this test starts failing, someone has switched the capability on and the resolver
/// needs the kind back — see the comment where it used to be.
/// <para>
/// Only the packs changed hands. <c>CaptureNames</c> still maps <c>def.parameter</c> to the
/// kind and ranks its specificity, so a pack that starts emitting parameters gets symbols of
/// the right kind on day one; removing the resolver entry was a statement about what is
/// actually produced, not a judgement that parameters are worthless.
/// </para>
/// </summary>
public class ParameterCaptureTests
{
    [Fact]
    public void NoQueryPackCapturesAParameter()
    {
        var offenders = new List<string>();

        foreach (var definition in LanguageRegistry.All)
        {
            var pack = QueryPack.Load(definition.QueryPackName);

            if (pack.SymbolQuery.Contains("def.parameter", StringComparison.Ordinal))
            {
                offenders.Add(definition.QueryPackName);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{string.Join(", ", offenders)} now capture(s) a parameter. Parameters are "
            + "reachable by name inside their own function, so SymbolKind.Parameter belongs "
            + "back in ReferenceResolver's `referencable` set — it was removed in M21.3 only "
            + "because nothing produced one.");
    }

}
