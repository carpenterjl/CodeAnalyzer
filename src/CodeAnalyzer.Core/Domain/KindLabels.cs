namespace CodeAnalyzer.Core.Domain;

/// <summary>
/// Display vocabulary for the persisted enums.
/// <para>
/// This lives in Core rather than in the WPF layer because two very different renderers
/// consume it: the XAML detail pane and the JSON payload sent to the graph page. A second
/// copy would eventually disagree with the first, and the confidence wording in particular
/// is a promise about how much the resolver actually knows.
/// </para>
/// </summary>
public static class KindLabels
{
    public static string For(SymbolKind kind) => kind switch
    {
        SymbolKind.Function => "function",
        SymbolKind.Method => "method",
        SymbolKind.Class => "class",
        SymbolKind.Struct => "struct",
        SymbolKind.Union => "union",
        SymbolKind.Enum => "enum",
        SymbolKind.EnumMember => "enum member",
        SymbolKind.Field => "field",
        SymbolKind.Variable => "variable",
        SymbolKind.Constant => "constant",
        SymbolKind.Macro => "macro",
        SymbolKind.Typedef => "typedef",
        SymbolKind.Namespace => "namespace",
        SymbolKind.Interface => "interface",
        SymbolKind.Property => "property",
        SymbolKind.Parameter => "parameter",
        SymbolKind.Module => "module",
        SymbolKind.Port => "port",
        SymbolKind.MarkupElement => "element",
        SymbolKind.ResourceKey => "resource",
        _ => "symbol",
    };

    public static string For(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Call => "call",
        ReferenceKind.Use => "use",
        ReferenceKind.TypeUse => "type use",
        ReferenceKind.Include => "include",
        ReferenceKind.Import => "import",
        ReferenceKind.Inherit => "inherits",
        ReferenceKind.Instantiate => "instantiates",
        ReferenceKind.Binding => "binding",
        ReferenceKind.Resource => "resource",
        ReferenceKind.Handler => "handler",
        _ => "reference",
    };

    public static string For(EdgeConfidence confidence) => confidence switch
    {
        EdgeConfidence.Unique => "exact",
        EdgeConfidence.Ambiguous => "one of several name matches",
        EdgeConfidence.Weak => "cross-language name match",
        _ => string.Empty,
    };

    /// <summary>
    /// Collapses the symbol kinds into the handful of visual families the graph colours by.
    /// Returned verbatim as a CSS class suffix, so the values must stay lower-case and stable.
    /// </summary>
    public static string GroupFor(SymbolKind kind) => kind switch
    {
        SymbolKind.Function or SymbolKind.Method => "function",
        SymbolKind.Class or SymbolKind.Struct or SymbolKind.Union or SymbolKind.Enum
            or SymbolKind.Typedef or SymbolKind.Interface => "type",
        SymbolKind.Constant or SymbolKind.EnumMember => "constant",
        SymbolKind.Macro => "macro",
        SymbolKind.Module or SymbolKind.Namespace => "module",
        _ => "variable",
    };

    /// <summary>Wire name for a confidence level. Stable: the graph page keys styling off it.</summary>
    public static string TokenFor(EdgeConfidence confidence) => confidence switch
    {
        EdgeConfidence.Unique => "unique",
        EdgeConfidence.Ambiguous => "ambiguous",
        _ => "weak",
    };
}
