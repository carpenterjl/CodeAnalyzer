using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Analysis;

/// <summary>
/// Extracts symbols and references from one file's source text.
/// <para>
/// This seam lives in Core so the indexing pipeline never references the parsing
/// implementation: the tree-sitter binding stays swappable and fakeable in tests.
/// </para>
/// <para>
/// Instances are not thread-safe. Each pipeline worker owns one, because native parser
/// handles carry no documented thread-safety guarantee.
/// </para>
/// </summary>
public interface ILanguageAnalyzer : IDisposable
{
    /// <summary>The language this analyzer handles, matching <see cref="FileRecord.Language"/>.</summary>
    string Language { get; }

    /// <summary>
    /// Parses <paramref name="source"/> and returns everything found. Implementations must
    /// not throw on malformed input; they report <see cref="FileStatus.ParseError"/> instead,
    /// since tree-sitter still yields usable partial trees for broken files.
    /// </summary>
    ParseResult Analyze(string relativePath, string source, CancellationToken cancellationToken);
}

/// <summary>
/// Creates per-worker analyzers and answers which files are worth crawling.
/// </summary>
public interface IAnalyzerFactory
{
    /// <summary>True when an extension maps to a language that has a usable query pack.</summary>
    bool IsSupportedExtension(string extension);

    /// <summary>The language name for an extension, or null when unsupported.</summary>
    string? GetLanguageForExtension(string extension);

    /// <summary>
    /// Creates an analyzer for a language. Called once per worker per language, not per file,
    /// since constructing one compiles the query pack and loads a native grammar.
    /// </summary>
    ILanguageAnalyzer Create(string language);
}
