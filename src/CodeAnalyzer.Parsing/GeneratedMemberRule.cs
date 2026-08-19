using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Parsing;

/// <summary>How a generated member takes its name from the declaration that triggers it.</summary>
public enum GeneratedNameStyle
{
    /// <summary>
    /// A backing field's name, made into the property a generator exposes: <c>_boxSizeX</c>,
    /// <c>m_boxSizeX</c> and <c>boxSizeX</c> all yield <c>BoxSizeX</c>. These are
    /// CommunityToolkit.Mvvm's own rules, and a field whose name survives them unchanged
    /// (<c>BoxSizeX</c> already) generates nothing, because the generator would collide
    /// with the field itself.
    /// </summary>
    PascalCaseOfBackingField,

    /// <summary>
    /// A method's name with <c>Command</c> appended and a trailing <c>Async</c> dropped:
    /// both <c>Save()</c> and <c>SaveAsync()</c> expose <c>SaveCommand</c>.
    /// </summary>
    CommandForMethod,
}

/// <summary>
/// One member a source generator adds to a type because a declaration inside it carries a
/// given attribute.
/// <para>
/// The index holds what the source files say, and a generated member is written in no
/// source file — which is why a CommunityToolkit.Mvvm application's entire bound surface
/// was invisible to it. A rule here says what a generator would have written, so the
/// member exists as a definition that bindings and calls can resolve to; the synthesised
/// record carries the attribute verbatim in its modifiers, so no reader can mistake it for
/// a declaration someone typed.
/// </para>
/// <para>
/// This is deliberately a small, named list rather than a general mechanism. A generator
/// can emit anything; what is modelled here is the handful of shapes whose output name is
/// a pure function of the input name, which is the only case that can be got right without
/// running the generator.
/// </para>
/// </summary>
public sealed record GeneratedMemberRule
{
    /// <summary>Attribute name as written, without brackets, namespace or arguments.</summary>
    public required string Attribute { get; init; }

    /// <summary>The kind of declaration the attribute must sit on for the rule to fire.</summary>
    public required SymbolKind AppliesTo { get; init; }

    /// <summary>The kind of the member the generator adds.</summary>
    public required SymbolKind Produces { get; init; }

    /// <summary>How the generated member's name follows from the declaration's.</summary>
    public required GeneratedNameStyle NameStyle { get; init; }

    /// <summary>
    /// Declared type of the generated member, or <see langword="null"/> to reuse the
    /// triggering declaration's — a property generated from a backing field has the field's
    /// type, while a command has the generator's own.
    /// </summary>
    public string? TypeText { get; init; }

    /// <summary>
    /// The generated name, or <see langword="null"/> when this declaration generates
    /// nothing the index does not already hold.
    /// </summary>
    public static string? NameFor(GeneratedNameStyle style, string declared) => style switch
    {
        GeneratedNameStyle.PascalCaseOfBackingField => PropertyName(declared),
        GeneratedNameStyle.CommandForMethod => CommandName(declared),
        _ => null,
    };

    private static string? PropertyName(string field)
    {
        var trimmed = field.StartsWith("m_", StringComparison.Ordinal)
            ? field[2..]
            : field.TrimStart('_');

        if (trimmed.Length == 0 || !char.IsLetter(trimmed[0]))
        {
            return null;
        }

        var name = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];

        // A field already spelled like the property generates nothing: the generator needs
        // a name to differ from, and emitting one here would invent a duplicate of a
        // declaration the index already holds.
        return name == field ? null : name;
    }

    private static string? CommandName(string method)
    {
        if (method.Length == 0)
        {
            return null;
        }

        var stem = method.EndsWith("Async", StringComparison.Ordinal) && method.Length > 5
            ? method[..^5]
            : method;

        return stem.Length == 0 ? null : stem + "Command";
    }
}
