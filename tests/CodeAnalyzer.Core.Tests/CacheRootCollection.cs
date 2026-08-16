using Xunit;

namespace CodeAnalyzer.Core.Tests;

/// <summary>
/// Serialises every test class that redirects the cache root.
/// <para>
/// <c>CODEANALYZER_CACHE_ROOT</c> is process-wide state, and xUnit runs test classes in an
/// assembly concurrently. Two classes each doing the careful thing on its own — save the
/// old value, set its own, restore on dispose — still corrupt each other when they overlap,
/// because the "old value" one of them saves is the other one's temporary. The symptom is a
/// test that fails in a full run and passes when run alone, which is the worst shape a
/// failure can take: it makes every unrelated green run slightly less believable.
/// </para>
/// <para>
/// Nothing here needs shared setup, so the collection carries no fixture. Membership is the
/// whole point — classes in one collection do not run in parallel with each other.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class CacheRootCollection
{
    public const string Name = "cache-root";
}
