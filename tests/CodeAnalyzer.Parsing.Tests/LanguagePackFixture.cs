using CodeAnalyzer.Core.Analysis;
using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// One analyzer for a language, plus the few lookups every pack test needs.
/// </summary>
public abstract class LanguagePackFixture(string language, string fileName) : IDisposable
{
    private readonly TreeSitterAnalyzer _analyzer =
        new(LanguageRegistry.ForName(language)
            ?? throw new InvalidOperationException($"'{language}' is not registered."));

    public void Dispose() => _analyzer.Dispose();

    protected ParseResult Analyze(string source) =>
        _analyzer.Analyze(fileName, source, CancellationToken.None);

    /// <summary>The one symbol with this name, failing with a readable message otherwise.</summary>
    protected static SymbolRecord Symbol(ParseResult result, string name)
    {
        var matches = result.Symbols.Where(s => s.Name == name).ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        // Duplicates mean two patterns claimed the same declaration and neither won, which
        // would put the same node in the graph twice; say so rather than picking one.
        var all = string.Join(", ", result.Symbols.Select(s => $"{s.Name}:{s.Kind}"));
        throw new Xunit.Sdk.XunitException(
            $"expected exactly one '{name}', found {matches.Count}. All symbols: {all}");
    }

    protected static int IndexOf(ParseResult result, string name, SymbolKind kind) =>
        result.Symbols.ToList().FindIndex(s => s.Name == name && s.Kind == kind);

    protected static IReadOnlyList<string> MembersOf(ParseResult result, string containerName)
    {
        var symbols = result.Symbols.ToList();
        var container = symbols.FindIndex(s => s.Name == containerName);

        return symbols
            .Where(s => s.ContainerLocalIndex == container)
            .Select(s => s.Name)
            .ToList();
    }

    protected static IReadOnlyList<string> ReferenceNames(ParseResult result, ReferenceKind kind) =>
        result.References.Where(r => r.Kind == kind).Select(r => r.Name).ToList();
}
