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
/// A language read with a borrowed grammar can have the borrowing show: the parser reports
/// errors on syntax that is perfectly valid in the language it is actually reading. Left
/// alone, the error list would tell a user their file is broken when it is not — which is
/// worse than saying nothing, because it is the kind of claim this tool exists to avoid.
/// So the note replaces the generic wording wherever a parse error is described.
/// </para>
/// <para>
/// No language needs one today. XAML did until the divergences were closed in the grammar
/// itself, which is the better place: an excuse explains an error away, and this returning
/// null is what makes a XAML error worth reading again. The hook stays because the next
/// borrowed grammar will arrive before the argument for it is re-derived.
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
        // XAML used to need an excuse here. It no longer does: the grammar it is read with
        // now accepts the property element (<Grid.RowDefinitions>), the XML prologue and a
        // CDATA section, which were the three constructs HTML has no rule for. This repo
        // and JGraph together hold 23 XAML files and report zero parse errors between them,
        // where before they reported fifteen.
        //
        // Deliberately not replaced with a softer note. A note that fires on every error is
        // a standing excuse, and a standing excuse is what let a real swallow read as a
        // known limitation for nine rounds. An error reported here now is unexplained, and
        // should be — that is the state in which someone goes and looks.
        _ => null,
    };
}
