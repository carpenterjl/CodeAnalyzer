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
}
