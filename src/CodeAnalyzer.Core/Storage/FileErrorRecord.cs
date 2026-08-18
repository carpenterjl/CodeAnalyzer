namespace CodeAnalyzer.Core.Storage;

/// <summary>
/// A file whose last parse was imperfect.
/// <para>
/// <paramref name="Message"/> is null for a routine syntax error, where tree-sitter
/// recovered and the partial symbols are still indexed; it carries the exception text for
/// a hard failure that produced nothing. <paramref name="SymbolCount"/> lets the UI say
/// which of the two the user is looking at without implying the file was skipped.
/// </para>
/// <para>
/// <paramref name="Line"/> and <paramref name="Text"/> locate and quote the first thing the
/// grammar could not read. They are what makes an imperfect parse answerable: a file in
/// this list is far more often written in a dialect newer than the bundled grammar than it
/// is actually broken, and without the position the reader cannot tell which.
/// </para>
/// <para>
/// <paramref name="EndLine"/> is the last line of the construct the parse stopped inside.
/// What that construct swallowed is not in the index and cannot be counted; its extent is
/// the honest proxy, and a span running far past <paramref name="Line"/> is the shape of a
/// swallow. Null on indexes written before the column existed.
/// </para>
/// <para>
/// <paramref name="LineCount"/> is what makes that extent readable. Reaching line 220 is
/// unremarkable in a long file and means the rest was never read in a 222-line one, so the
/// two are only ever worth reporting together — see <see cref="ConsumedTheRestOfTheFile"/>.
/// </para>
/// </summary>
public sealed record FileErrorRecord(
    string RelativePath,
    string Language,
    string? Message,
    int SymbolCount,
    int? Line = null,
    string? Text = null,
    int? EndLine = null,
    int? LineCount = null)
{
    /// <summary>
    /// The construct the parse stopped inside runs to the last line of the file.
    /// <para>
    /// This is the shape of a swallow rather than of a typo: a construct that ends where the
    /// file ends did not end, and everything after the error is inside it and unread. It is
    /// worth a line of its own precisely because it is currently true of nothing — 1,214
    /// files across two workspaces and not one match. An alarm added while its population is
    /// zero has no backlog to be ignored in, which is the state in which the first real one
    /// gets read.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Kept in step with <see cref="FileErrorQuery.ConsumedTheRestOfTheFileSql"/>, which is
    /// the same test written for the header's COUNT.
    /// </remarks>
    public bool ConsumedTheRestOfTheFile =>
        Line is { } from && EndLine is { } to && LineCount is { } total && to > from && to >= total;
}
