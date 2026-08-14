using CodeAnalyzer.Core.Analysis;

namespace CodeAnalyzer.Parsing;

/// <summary>
/// Bridges the registry to the pipeline: decides which files are indexable and builds
/// per-worker analyzers.
/// </summary>
public sealed class TreeSitterAnalyzerFactory : IAnalyzerFactory
{
    public bool IsSupportedExtension(string extension)
    {
        var definition = LanguageRegistry.ForExtension(extension);

        // A language is only indexable once its query pack exists, so registering a
        // language ahead of writing its queries cannot produce empty results.
        return definition is not null && LanguageRegistry.HasQueryPack(definition);
    }

    public string? GetLanguageForExtension(string extension)
    {
        var definition = LanguageRegistry.ForExtension(extension);
        return definition is not null && LanguageRegistry.HasQueryPack(definition)
            ? definition.Name
            : null;
    }

    public ILanguageAnalyzer Create(string language)
    {
        var definition = LanguageRegistry.ForName(language)
            ?? throw new ArgumentException($"Unknown language '{language}'.", nameof(language));

        return new TreeSitterAnalyzer(definition);
    }
}
