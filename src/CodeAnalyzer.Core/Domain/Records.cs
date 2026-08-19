namespace CodeAnalyzer.Core.Domain;

/// <summary>Result of indexing one source file.</summary>
public enum FileStatus
{
    Ok = 0,
    ParseError = 1,
    Skipped = 2,
}

/// <summary>A source position, 1-based line and 0-based column.</summary>
public readonly record struct SourcePosition(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>
/// A half-open span of source text. Offsets are character indices into the source string,
/// matching what the tree-sitter binding reports, not byte offsets.
/// </summary>
public readonly record struct SourceSpan(SourcePosition Start, SourcePosition End, int StartOffset, int EndOffset)
{
    public bool Contains(int offset) => offset >= StartOffset && offset < EndOffset;

    /// <summary>Length in characters; used to pick the innermost enclosing symbol.</summary>
    public int Length => EndOffset - StartOffset;
}

/// <summary>A file that has been crawled and (possibly) parsed.</summary>
public sealed record FileRecord
{
    public long Id { get; init; }

    /// <summary>Path relative to the workspace root, using forward slashes.</summary>
    public required string RelativePath { get; init; }

    public required string Language { get; init; }

    /// <summary>SHA-256 of the file contents; drives incremental re-indexing.</summary>
    public required byte[] ContentHash { get; init; }

    public long Size { get; init; }

    /// <summary>Last write time as Unix milliseconds; used to skip hashing unchanged files.</summary>
    public long ModifiedUnixMs { get; init; }

    public FileStatus Status { get; init; } = FileStatus.Ok;
}

/// <summary>
/// A declaration extracted from source. Every field is a fact read out of the
/// syntax tree; nothing here is inferred.
/// </summary>
public sealed record SymbolRecord
{
    public long Id { get; init; }
    public long FileId { get; init; }

    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }

    /// <summary>Enclosing symbol: struct for a field, class for a method, module for a port.</summary>
    public long? ContainerId { get; init; }

    /// <summary>
    /// Index of the container within the same <see cref="ParseResult"/>. Analyzers set this
    /// because database ids do not exist until the writer runs; the writer translates it
    /// into <see cref="ContainerId"/>.
    /// </summary>
    public int? ContainerLocalIndex { get; init; }

    /// <summary>Dotted path of enclosing scopes, e.g. <c>Acme.Widgets.Button</c>. Empty at file scope.</summary>
    public string ScopePath { get; init; } = string.Empty;

    /// <summary>Verbatim source slice of the declarator, including parameters where applicable.</summary>
    public string? Signature { get; init; }

    /// <summary>Verbatim literal initializer for constants, macros and Verilog parameters.</summary>
    public string? Value { get; init; }

    /// <summary>Verbatim declared type, where the grammar exposes one.</summary>
    public string? TypeText { get; init; }

    /// <summary>
    /// Verbatim modifier keywords in source order, space-joined ("public sealed override").
    /// Null where the language pack captures none — Python, for one, has no such syntax.
    /// </summary>
    public string? Modifiers { get; init; }

    public required string Language { get; init; }
    public required SourceSpan Span { get; init; }

    /// <summary>False for prototypes and extern declarations, which are not resolution targets.</summary>
    public bool IsDefinition { get; init; } = true;

    /// <summary>Parameter count for callables, used as a soft filter during resolution.</summary>
    public int? ParameterCount { get; init; }

    /// <summary>
    /// The parameter list exactly as written, brackets included, truncated with a trailing
    /// ellipsis past 120 characters. Null for a declaration with no parameter list — which
    /// is not the same as <c>()</c>, and the two must stay distinguishable.
    /// </summary>
    public string? ParameterText { get; init; }

    /// <summary>
    /// The comment written immediately above this declaration, or null when there is none.
    /// <para>
    /// "Immediately" is the whole rule: comment lines are walked upward from the declaration
    /// and stop at the first blank line, so a comment separated by whitespace belongs to the
    /// file or to the section, not to this symbol. A trailing comment on the line above
    /// (<c>int a; // about a</c>) is excluded too — it is about the statement it sits on.
    /// </para>
    /// <para>
    /// Stored with the comment punctuation removed and the lines joined by single spaces,
    /// which is the form that can be searched: a reader looking for "retry" should not have
    /// to know whether the file writes <c>///</c>, <c>#</c> or <c>&lt;!--</c>. The verbatim
    /// text is still one <c>get_context</c> away. Capped like <see cref="ErrorText"/>.
    /// </para>
    /// </summary>
    public string? DocComment { get; init; }
}

/// <summary>A use of a name at a source location, before resolution.</summary>
public sealed record ReferenceRecord
{
    public long Id { get; init; }
    public long FileId { get; init; }

    /// <summary>The innermost symbol containing this reference: the caller. Null at file scope.</summary>
    public long? FromSymbolId { get; init; }

    /// <summary>
    /// Index of the enclosing symbol within the same <see cref="ParseResult"/>, translated
    /// into <see cref="FromSymbolId"/> by the writer. Null when the reference sits at file scope.
    /// </summary>
    public int? FromSymbolLocalIndex { get; init; }

    public required string Name { get; init; }
    public required ReferenceKind Kind { get; init; }

    /// <summary>Argument count at a call site, where countable.</summary>
    public int? ArgumentCount { get; init; }

    /// <summary>
    /// Verbatim argument list at a call site, truncated with a trailing ellipsis past
    /// 200 characters so the largest table stays bounded. Null without an argument list.
    /// </summary>
    public string? ArgumentText { get; init; }

    /// <summary>
    /// Verbatim receiver expression at a member call or member access — the
    /// <c>orchestrator</c> in <c>orchestrator.IndexAsync(…)</c> — truncated with a trailing
    /// ellipsis past 100 characters. Null where the reference is a bare name or the pack
    /// captures no receiver; absence of a capture is not an assertion of bareness.
    /// </summary>
    public string? ReceiverText { get; init; }

    /// <summary>
    /// How this call's result is consumed at the site, read off the call node's
    /// ancestors, or <see cref="ResultFate.Unknown"/> where the walker makes no claim.
    /// Null for any reference that is not a call site at all — absence of the fact, not
    /// an unknown fact. The call-flow query filters on exactly that invariant.
    /// </summary>
    public ResultFate? Fate { get; init; }

    /// <summary>
    /// Verbatim assignment target when <see cref="Fate"/> is
    /// <see cref="ResultFate.Assigned"/> — the <c>cfg</c> in <c>var cfg = Load();</c>,
    /// a member access, a tuple — truncated like <see cref="ReceiverText"/>. Null otherwise.
    /// </summary>
    public string? FateName { get; init; }

    public required SourcePosition Position { get; init; }
}

/// <summary>A resolved link from a reference to a candidate definition.</summary>
public sealed record EdgeRecord
{
    public long ReferenceId { get; init; }
    public long TargetSymbolId { get; init; }
    public EdgeConfidence Confidence { get; init; }
}

/// <summary>An <c>#include</c> or <c>import</c> from one file to another.</summary>
public sealed record FileDependencyRecord
{
    public long FileId { get; init; }

    /// <summary>The raw path text as written in the source.</summary>
    public required string DependencyPath { get; init; }

    /// <summary>The workspace file it resolved to, or null when it points outside the workspace.</summary>
    public long? DependencyFileId { get; init; }
}

/// <summary>Everything extracted from a single file by a language analyzer.</summary>
public sealed record ParseResult
{
    public required string RelativePath { get; init; }
    public required string Language { get; init; }
    public required byte[] ContentHash { get; init; }
    public long Size { get; init; }
    public long ModifiedUnixMs { get; init; }
    public FileStatus Status { get; init; } = FileStatus.Ok;

    public IReadOnlyList<SymbolRecord> Symbols { get; init; } = [];
    public IReadOnlyList<ReferenceRecord> References { get; init; } = [];
    public IReadOnlyList<FileDependencyRecord> Dependencies { get; init; } = [];

    /// <summary>
    /// Set when <see cref="Status"/> is <see cref="FileStatus.ParseError"/> (a hard
    /// failure that produced nothing — routine syntax errors store null) or
    /// <see cref="FileStatus.Skipped"/> (the reason the tool decided not to index the
    /// file; a skip must always state why).
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 1-based line where the parser first lost its footing, or null when nothing in the
    /// tree was malformed. Independent of <see cref="ErrorMessage"/>: that is set when the
    /// file produced nothing, this when an otherwise fully indexed file contains a
    /// construct the grammar cannot read.
    /// </summary>
    public int? ErrorLine { get; init; }

    /// <summary>
    /// The first line of the text at <see cref="ErrorLine"/>, verbatim and capped. Null
    /// where the parser expected a token and found none, which has a position but no text.
    /// </summary>
    public string? ErrorText { get; init; }

    /// <summary>
    /// 1-based last line of the construct the parse stopped inside. The declarations a
    /// broken construct swallowed are not in the index, so their count cannot be stated —
    /// but the construct's extent can, and it is the honest proxy: an unclosed
    /// <c>&lt;script&gt;</c> whose error line says 3 and whose extent says 40 consumed the
    /// rest of the file, which is exactly the shape that hid 33 XAML declarations for nine
    /// rounds behind a label naming only where the parse stopped.
    /// </summary>
    public int? ErrorEndLine { get; init; }

    /// <summary>
    /// Lines in the file as parsed, for every file and not only broken ones. It is the
    /// denominator <see cref="ErrorEndLine"/> is meaningless without: an extent running to
    /// line 220 is unremarkable in a 4,000-line file and means the rest of the file was
    /// never read in a 222-line one, and only the second deserves an alarm.
    /// </summary>
    public int? LineCount { get; init; }
}
