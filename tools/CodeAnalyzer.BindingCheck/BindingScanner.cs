// System.IO explicitly: a UseWPF project's implicit usings do not include it.
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodeAnalyzer.BindingCheck;

/// <summary>One thing a XAML file asks the compiler or the runtime to find by name.</summary>
/// <param name="Kind">Binding or Handler — they are checked by different rules.</param>
public sealed record XamlNameUse(
    string File,
    int Line,
    string Attribute,
    string Path,
    XamlUseKind Kind);

public enum XamlUseKind
{
    /// <summary>A property path inside a markup extension, checked against candidate types.</summary>
    Binding,

    /// <summary>An event handler name, checked against the file's own x:Class type.</summary>
    Handler,
}

/// <summary>Everything one XAML file asks for, plus what it says its code-behind is.</summary>
public sealed record XamlFileUses(string File, string? CodeBehindType, IReadOnlyList<XamlNameUse> Uses);

/// <summary>
/// Reads XAML as XML, because that is what it is.
/// <para>
/// Deliberately not the product's own XAML query pack: that pack runs on the HTML grammar
/// and is honest about what the borrowing costs it, while this tool needs property
/// elements and namespace prefixes read correctly. A checker that inherited the same blind
/// spots as the thing it checks would agree with it for the wrong reasons.
/// </para>
/// </summary>
public static class BindingScanner
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Markup extensions carrying a property path. Anything else — StaticResource,
    /// x:Static, x:Type — names something this checker does not resolve, and is counted
    /// as skipped rather than passed.
    /// </summary>
    private static readonly Regex BindingExtension = new(
        @"\{\s*(?<kind>Binding|TemplateBinding)\b(?<body>[^}]*)\}",
        RegexOptions.Compiled);

    /// <summary>
    /// The path inside a binding: either the first positional argument or Path=… .
    /// A path with an indexer or an attached-property parenthesis is left to the caller
    /// to reject, so those are never quietly treated as resolved.
    /// </summary>
    private static readonly Regex PathArgument = new(
        @"(?:^|,)\s*(?:Path\s*=\s*)?(?<path>[A-Za-z_][A-Za-z0-9_.\[\]\(\)]*)",
        RegexOptions.Compiled);

    /// <summary>Attributes whose value is a code-behind method name.</summary>
    private static bool IsHandlerAttribute(XAttribute attribute, string value) =>
        attribute.Name.Namespace == XNamespace.None
        && !value.StartsWith('{')
        && attribute.Name.LocalName is not ("Name" or "Key" or "Class")
        && HandlerNamePattern.IsMatch(attribute.Name.LocalName)
        && IdentifierPattern.IsMatch(value);

    // An event attribute is spelled like an event: Click, MouseDown, SelectionChanged,
    // PreviewKeyDown. This is a naming convention, not a fact from the source, so a miss
    // here means a handler goes unchecked — never that a good name is reported bad.
    private static readonly Regex HandlerNamePattern = new(
        @"^(?:Preview)?(?:[A-Z][a-z0-9]+)*(?:Click|Changed|Down|Up|Opened|Closed|Loaded|Unloaded|Enter|Leave|Move|Wheel|Focus|Drop|Checked|Expanded|Collapsed|Activated|Deactivated|Selected)$",
        RegexOptions.Compiled);

    private static readonly Regex IdentifierPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static XamlFileUses Scan(string path, string relativeTo)
    {
        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        var relative = Path.GetRelativePath(relativeTo, path).Replace('\\', '/');

        var codeBehind = document.Root?.Attribute(XamlNs + "Class")?.Value;
        var uses = new List<XamlNameUse>();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value;
                var line = (attribute as IXmlLineInfo).HasLineInfo()
                    ? ((IXmlLineInfo)attribute).LineNumber
                    : 0;

                foreach (Match match in BindingExtension.Matches(value))
                {
                    var body = match.Groups["body"].Value;
                    var extracted = ExtractPath(body);
                    if (extracted is not null)
                    {
                        uses.Add(new XamlNameUse(
                            relative, line, attribute.Name.LocalName, extracted, XamlUseKind.Binding));
                    }
                }

                if (IsHandlerAttribute(attribute, value))
                {
                    uses.Add(new XamlNameUse(
                        relative, line, attribute.Name.LocalName, value, XamlUseKind.Handler));
                }
            }
        }

        return new XamlFileUses(relative, codeBehind, uses);
    }

    /// <summary>
    /// The property path a binding names, or null when there is nothing checkable: a
    /// pathless <c>{Binding}</c>, a binding sourced from a resource, or one whose path
    /// this tool will not claim to understand.
    /// </summary>
    public static string? ExtractPath(string body)
    {
        body = body.Trim();

        // A binding pointed at something other than the ambient data context is naming a
        // property on a type this tool has no way to identify. Skipped, not passed.
        if (body.Contains("Source=", StringComparison.Ordinal)
            || body.Contains("ElementName=", StringComparison.Ordinal))
        {
            return null;
        }

        var match = PathArgument.Match(body);
        if (!match.Success)
        {
            return null;
        }

        var path = match.Groups["path"].Value.TrimEnd('.');

        // Indexers and attached properties are real paths this tool cannot walk. Refusing
        // them keeps it from reporting a working binding as broken.
        if (path.Length == 0 || path.Contains('[') || path.Contains('('))
        {
            return null;
        }

        // A binding reached through a RelativeSource starts at the DataContext, which is
        // exactly where a plain path starts.
        if (path.StartsWith("DataContext.", StringComparison.Ordinal))
        {
            path = path["DataContext.".Length..];
        }

        // The route a ContextMenu takes to reach the window's view model: the menu is
        // outside the visual tree, so it hops through its placement target's Tag.
        if (path.StartsWith("PlacementTarget.Tag.", StringComparison.Ordinal))
        {
            path = path["PlacementTarget.Tag.".Length..];
        }

        return path is "DataContext" or "PlacementTarget" or "Tag" ? null : path;
    }
}
