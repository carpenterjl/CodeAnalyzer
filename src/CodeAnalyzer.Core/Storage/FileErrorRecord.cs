namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// A file whose last parse was imperfect.
/// <para>
/// <paramref name="Message"/> is null for a routine syntax error, where tree-sitter
/// recovered and the partial symbols are still indexed; it carries the exception text for
/// a hard failure that produced nothing. <paramref name="SymbolCount"/> lets the UI say
/// which of the two the user is looking at without implying the file was skipped.
/// </para>
/// </summary>
public sealed record FileErrorRecord(
    string RelativePath,
    string Language,
    string? Message,
    int SymbolCount);
