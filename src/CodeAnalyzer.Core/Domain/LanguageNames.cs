namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// The language names stored in the index.
/// <para>
/// They live here rather than next to the parser registry because resolution and the UI
/// both branch on them, and neither can reference the parsing assembly.
/// </para>
/// </summary>
public static class LanguageNames
{
    public const string C = "C";
    public const string Cpp = "C++";
    public const string CSharp = "C#";
    public const string Python = "Python";
    public const string Verilog = "Verilog";
    public const string Html = "HTML";
    public const string JavaScript = "JavaScript";
    public const string Xaml = "XAML";
}

/// <summary>
/// What to say about a language whose grammar was written for a different one.
/// <para>
/// One language here is read with a borrowed grammar, and the borrowing shows: the parser
/// reports errors on syntax that is perfectly valid in the language it is actually reading.
/// Left alone, the error list would tell a user their file is broken when it is not —
/// which is worse than saying nothing, because it is the kind of claim this tool exists
/// to avoid. So the note replaces the generic wording wherever a parse error is described.
/// </para>
/// <para>
/// It lives beside <see cref="LanguageNames"/> for the same reason those do: the wording
/// is consumed by the UI and could be consumed by anything else that lists a parse
/// problem, and a second copy would eventually disagree.
/// </para>
/// </summary>
public static class GrammarNotes
{
    /// <summary>
    /// The sentence to show instead of the generic syntax-error wording, or null when the
    /// language is read by its own grammar and an error means what it says.
    /// </summary>
    public static string? For(string language) => language switch
    {
        LanguageNames.Xaml =>
            "XAML is read with the HTML grammar — property elements such as "
            + "<Grid.RowDefinitions> are valid XAML but not valid HTML, so the parser "
            + "reports them. The names in this file are still indexed.",
        _ => null,
    };
}
