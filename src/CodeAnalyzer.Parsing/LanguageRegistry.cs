using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Parsing;

/// <summary>
/// Maps file extensions to language definitions. Adding a language means adding an
/// entry here plus a query pack folder; nothing else in the pipeline changes.
/// </summary>
public static class LanguageRegistry
{
    public const string C = LanguageNames.C;
    public const string Cpp = LanguageNames.Cpp;
    public const string CSharp = LanguageNames.CSharp;
    public const string Python = LanguageNames.Python;
    public const string Verilog = LanguageNames.Verilog;
    public const string Html = LanguageNames.Html;
    public const string JavaScript = LanguageNames.JavaScript;
    public const string Xaml = LanguageNames.Xaml;

    private static readonly LanguageDefinition[] Definitions =
    [
        new()
        {
            Name = C,
            GrammarId = "C",
            Extensions = [".c", ".h"],
            QueryPackName = "c",
        },
        new()
        {
            Name = Cpp,
            GrammarId = "CPP",
            Extensions = [".cpp", ".cxx", ".cc", ".c++", ".hpp", ".hxx", ".hh", ".h++", ".ipp", ".inl"],
            QueryPackName = "cpp",
        },
        new()
        {
            Name = CSharp,
            GrammarId = "C#",
            Extensions = [".cs"],
            QueryPackName = "csharp",
        },
        new()
        {
            Name = Python,
            GrammarId = "Python",
            Extensions = [".py", ".pyi", ".pyw"],
            QueryPackName = "python",
        },
        new()
        {
            Name = Verilog,
            GrammarId = "Verilog",
            Extensions = [".v", ".vh", ".sv", ".svh", ".vlg"],
            QueryPackName = "verilog",
            CallerKinds = new HashSet<SymbolKind> { SymbolKind.Module, SymbolKind.Function },
        },
        new()
        {
            Name = Html,
            GrammarId = "HTML",
            Extensions = [".html", ".htm", ".xhtml"],
            QueryPackName = "html",
        },
        new()
        {
            Name = JavaScript,
            GrammarId = "JavaScript",
            // .jsx is deliberately absent: the grammar parses JSX, but nothing here has
            // been tested against it, and a language claimed but unverified is worse than
            // one left out.
            Extensions = [".js", ".mjs", ".cjs"],
            QueryPackName = "javascript",
        },
        new()
        {
            // XAML on the HTML grammar. There is no XAML or XML grammar in the bundle, and
            // the two languages agree on the part that matters here — elements, attributes,
            // quoted values, self-closing tags — which was verified against this repo's own
            // nine .xaml files before the pack was written. Where they disagree is the
            // property element (<Grid.RowDefinitions>), which the grammar reports as an
            // error; names inside such an element are still extracted, and the error is
            // reworded for the reader by GrammarNotes rather than presented as the file's
            // fault. See the pack header for the full list of what this does and does not
            // see.
            Name = Xaml,
            GrammarId = "HTML",
            Extensions = [".xaml"],
            QueryPackName = "xaml",
        },
    ];

    private static readonly Dictionary<string, LanguageDefinition> ByExtension =
        BuildExtensionMap();

    private static readonly Dictionary<string, LanguageDefinition> ByName =
        Definitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, LanguageDefinition> BuildExtensionMap()
    {
        var map = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            foreach (var extension in definition.Extensions)
            {
                map[extension] = definition;
            }
        }

        return map;
    }

    /// <summary>All languages currently supported, in registration order.</summary>
    public static IReadOnlyList<LanguageDefinition> All => Definitions;

    /// <summary>Returns the language for a file extension, or null when unsupported.</summary>
    public static LanguageDefinition? ForExtension(string extension) =>
        ByExtension.GetValueOrDefault(extension);

    public static LanguageDefinition? ForName(string name) =>
        ByName.GetValueOrDefault(name);

    /// <summary>
    /// True when a query pack exists for this language. Languages are registered here
    /// before their packs are written, so the pipeline checks this before parsing.
    /// </summary>
    public static bool HasQueryPack(LanguageDefinition definition) =>
        QueryPack.Exists(definition.QueryPackName);
}
