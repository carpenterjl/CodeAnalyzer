using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Parsing;

/// <summary>
/// The capture-name vocabulary shared by every query pack. Keeping it in one place means
/// a new language needs only a .scm file, not analyzer changes.
/// </summary>
internal static class CaptureNames
{
    public const string DefinitionPrefix = "def.";
    public const string ReferencePrefix = "ref.";

    public const string Name = "name";
    public const string Value = "value";
    public const string Type = "type";
    public const string Parameters = "params";
    public const string Arguments = "args";
    public const string Modifier = "modifier";
    public const string Receiver = "receiver";

    private static readonly Dictionary<string, SymbolKind> SymbolKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["function"] = SymbolKind.Function,
        ["method"] = SymbolKind.Method,
        ["prototype"] = SymbolKind.Function,
        ["method_prototype"] = SymbolKind.Method,
        ["class"] = SymbolKind.Class,
        ["struct"] = SymbolKind.Struct,
        ["union"] = SymbolKind.Union,
        ["enum"] = SymbolKind.Enum,
        ["enum_member"] = SymbolKind.EnumMember,
        ["field"] = SymbolKind.Field,
        ["variable"] = SymbolKind.Variable,
        ["constant"] = SymbolKind.Constant,
        ["macro"] = SymbolKind.Macro,
        ["typedef"] = SymbolKind.Typedef,
        ["namespace"] = SymbolKind.Namespace,
        ["interface"] = SymbolKind.Interface,
        ["property"] = SymbolKind.Property,
        ["parameter"] = SymbolKind.Parameter,
        ["module"] = SymbolKind.Module,
        ["port"] = SymbolKind.Port,
        ["markup_element"] = SymbolKind.MarkupElement,
        ["resource_key"] = SymbolKind.ResourceKey,
    };

    private static readonly Dictionary<string, ReferenceKind> ReferenceKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["call"] = ReferenceKind.Call,
        ["use"] = ReferenceKind.Use,
        ["type"] = ReferenceKind.TypeUse,
        ["include"] = ReferenceKind.Include,
        ["import"] = ReferenceKind.Import,
        ["inherit"] = ReferenceKind.Inherit,
        ["instantiate"] = ReferenceKind.Instantiate,
        ["binding"] = ReferenceKind.Binding,
        ["handler"] = ReferenceKind.Handler,
    };

    /// <summary>Capture suffixes that declare a symbol but are not resolution targets.</summary>
    private static readonly HashSet<string> NonDefiningSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "prototype",
        "method_prototype",
    };

    public static bool TryGetSymbolKind(string captureName, out SymbolKind kind, out bool isDefinition)
    {
        kind = SymbolKind.Unknown;
        isDefinition = true;

        if (!captureName.StartsWith(DefinitionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = captureName[DefinitionPrefix.Length..];
        if (!SymbolKinds.TryGetValue(suffix, out kind))
        {
            return false;
        }

        isDefinition = !NonDefiningSuffixes.Contains(suffix);
        return true;
    }

    public static bool TryGetReferenceKind(string captureName, out ReferenceKind kind)
    {
        kind = ReferenceKind.Unknown;

        if (!captureName.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return ReferenceKinds.TryGetValue(captureName[ReferencePrefix.Length..], out kind);
    }

    /// <summary>
    /// Ranks symbol kinds so that when two patterns declare the same name at the same
    /// position, the more specific claim wins.
    /// <para>
    /// Overlapping patterns are the normal way to express a narrowing rule that tree-sitter
    /// predicates cannot: a broad pattern captures every C# field, a second one captures the
    /// <c>const</c> ones, and the constant wins here. Likewise a Python <c>def</c> nested in
    /// a class is a method, while the same pattern at file scope leaves it a function.
    /// </para>
    /// </summary>
    public static int Specificity(SymbolKind kind) => kind switch
    {
        SymbolKind.Constant => 9,
        SymbolKind.EnumMember or SymbolKind.Macro => 8,
        SymbolKind.Port => 7,
        SymbolKind.Property => 6,
        SymbolKind.Field or SymbolKind.Method => 5,
        SymbolKind.Class or SymbolKind.Struct or SymbolKind.Union or SymbolKind.Enum
            or SymbolKind.Typedef or SymbolKind.Interface or SymbolKind.Namespace
            or SymbolKind.Module or SymbolKind.MarkupElement or SymbolKind.ResourceKey => 4,
        SymbolKind.Function => 3,
        SymbolKind.Variable => 2,
        SymbolKind.Parameter => 1,
        _ => 0,
    };

    /// <summary>
    /// Ranks reference kinds so that when two patterns capture the same position, the more
    /// specific one wins: a call site is a call, not a bare identifier use.
    /// </summary>
    public static int Specificity(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Call => 5,
        ReferenceKind.Instantiate => 5,
        ReferenceKind.Inherit => 4,
        ReferenceKind.Include => 4,
        ReferenceKind.Import => 4,
        ReferenceKind.TypeUse => 3,
        ReferenceKind.Binding => 3,
        ReferenceKind.Resource => 3,
        // Above the extension kinds: a handler pattern and the brace test cannot both
        // match the same value, but an attribute-name rule is the more specific claim.
        ReferenceKind.Handler => 4,
        ReferenceKind.Use => 1,
        _ => 0,
    };
}
