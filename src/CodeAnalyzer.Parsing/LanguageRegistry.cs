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

            // CommunityToolkit.Mvvm. Both of these are the whole bound surface of a WPF or
            // MAUI application written this decade: the XAML says {Binding BoxSizeX} and
            // {Binding SaveCommand}, and neither name is written in any .cs file. Measured
            // on OpenSim Studio before these were added — 239 [ObservableProperty] fields
            // and 70 [RelayCommand] methods across 26 files, 265 generated names the index
            // held no definition of, 37 unresolved bindings and 925 other unresolved
            // references naming one of them.
            GeneratedMembers =
            [
                new()
                {
                    Attribute = "ObservableProperty",
                    AppliesTo = SymbolKind.Field,
                    Produces = SymbolKind.Property,
                    NameStyle = GeneratedNameStyle.PascalCaseOfBackingField,
                },
                new()
                {
                    Attribute = "RelayCommand",
                    AppliesTo = SymbolKind.Method,
                    Produces = SymbolKind.Property,
                    NameStyle = GeneratedNameStyle.CommandForMethod,

                    // The generator picks IRelayCommand, IAsyncRelayCommand or a generic
                    // form from the method's signature. The interface every one of them
                    // implements is the part that is knowable without running it, and a
                    // declared type that is right but imprecise beats one that is neither.
                    TypeText = "IRelayCommand",
                },
            ],
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
            // XAML on a grammar of our own: tree-sitter-html compiled a second time with
            // HTML's tag table switched off, so no tag is void, raw-text or implicitly
            // closed. See grammars/xaml/README.md.
            //
            // It ran on the stock HTML grammar for nine rounds, and the note that stood
            // here blamed the property element (<Grid.RowDefinitions>) for the errors it
            // reported. That was wrong, and wrong in a way that hid a real loss: <Style>
            // is a raw-text element in HTML, so everything between <Style ...> and </Style>
            // was read as CSS and discarded — 32 declarations in this repo's own
            // Controls.xaml, with no error raised at all. The visible error came from
            // </Style.Triggers>, which begins with </Style and therefore ends the raw text
            // in the wrong place; the property element was the messenger.
            Name = Xaml,
            GrammarId = "xaml",
            Extensions = [".xaml"],
            QueryPackName = "xaml",
            // What can own a reference in a XAML file is a named element — the root owns
            // the x:Class reference to its code-behind class — or a keyed resource, whose
            // body holds the bindings and resource lookups a template is written from.
            CallerKinds = new HashSet<SymbolKind> { SymbolKind.MarkupElement, SymbolKind.ResourceKey },
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
