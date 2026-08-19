using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Parsing;

/// <summary>
/// Binds one of our language names to a tree-sitter grammar, the file extensions that
/// select it, and the query pack that drives extraction.
/// </summary>
public sealed record LanguageDefinition
{
    /// <summary>Our language name, stored in the index, e.g. <c>C</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Grammar id passed to <c>new Language(id)</c>. These are the names the
    /// TreeSitter.DotNet package registers, which do not always match ours.
    /// </summary>
    public required string GrammarId { get; init; }

    /// <summary>Lower-case extensions including the dot.</summary>
    public required IReadOnlyList<string> Extensions { get; init; }

    /// <summary>Folder name under <c>Queries/</c> holding symbols.scm and refs.scm.</summary>
    public required string QueryPackName { get; init; }

    /// <summary>
    /// Symbol kinds that can enclose a reference, i.e. what counts as "the caller".
    /// A reference inside a struct body belongs to no caller, but one inside a
    /// function body belongs to that function.
    /// </summary>
    public IReadOnlySet<SymbolKind> CallerKinds { get; init; } = DefaultCallerKinds;

    /// <summary>
    /// Members a source generator adds to declarations carrying a given attribute. Empty
    /// for every language whose toolchain has no such step, which is most of them.
    /// </summary>
    public IReadOnlyList<GeneratedMemberRule> GeneratedMembers { get; init; } = [];

    public static readonly IReadOnlySet<SymbolKind> DefaultCallerKinds = new HashSet<SymbolKind>
    {
        SymbolKind.Function,
        SymbolKind.Method,
        SymbolKind.Macro,
        SymbolKind.Module,
    };
}
