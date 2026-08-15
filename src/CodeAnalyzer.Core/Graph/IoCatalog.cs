using System.Reflection;
using System.Text.Json;
using CodeAnalyzer.Core.Domain;

namespace CodeAnalyzer.Core.Graph;

/// <summary>
/// One API in the I/O catalog: a called name (exact or prefix), the languages it applies
/// to, an optional co-occurrence requirement, and the direction the API's documented
/// contract states.
/// <para>
/// The requirement exists because member-call languages store only the member name —
/// <c>port.Write(...)</c> is just <c>Write</c> in the index — which is far too generic to
/// catalog on its own. Requiring the same file to also reference the API's type or import
/// its module keeps the match honest, and the rule is stated on every site it admits.
/// </para>
/// </summary>
public sealed record IoCatalogEntry
{
    /// <summary>Where the entry came from, shown with every match ("STM32 HAL").</summary>
    public required string Family { get; init; }

    /// <summary>Languages the entry applies to, using <see cref="LanguageNames"/> values.</summary>
    public required IReadOnlyList<string> Languages { get; init; }

    /// <summary>Exact called name. Exactly one of this and <see cref="Prefix"/> is set.</summary>
    public string? Name { get; init; }

    /// <summary>Called-name prefix, e.g. <c>HAL_UART_Transmit</c> covering the _IT/_DMA variants.</summary>
    public string? Prefix { get; init; }

    public required IoDirection Direction { get; init; }

    /// <summary>
    /// Type names, any one of which the calling file must reference (<c>SerialPort</c>).
    /// Empty when the entry has no type requirement.
    /// </summary>
    public IReadOnlyList<string> RequiredTypeRefs { get; init; } = [];

    /// <summary>
    /// Include/import paths, any one of which the calling file must depend on
    /// (<c>sys/socket.h</c>, <c>serial</c>). Empty when the entry has no dependency requirement.
    /// </summary>
    public IReadOnlyList<string> RequiredDependencies { get; init; } = [];

    /// <summary>True when a match needs a co-occurring fact beyond the name.</summary>
    public bool IsGated => RequiredTypeRefs.Count > 0 || RequiredDependencies.Count > 0;

    /// <summary>
    /// The human-readable statement of the gate, shown on every site this entry admits.
    /// Null for ungated entries, whose name alone was distinctive enough to ship.
    /// </summary>
    public string? GateNote
    {
        get
        {
            if (!IsGated)
            {
                return null;
            }

            var parts = new List<string>(2);
            if (RequiredTypeRefs.Count > 0)
            {
                parts.Add($"references {string.Join(" or ", RequiredTypeRefs)}");
            }

            if (RequiredDependencies.Count > 0)
            {
                parts.Add($"depends on {string.Join(" or ", RequiredDependencies)}");
            }

            return $"in a file that {string.Join(" or ", parts)}";
        }
    }
}

/// <summary>
/// The I/O API catalog: the shipped list of known transmit/receive functions, loaded from
/// an embedded JSON resource so extending it is editing data, not code.
/// <para>
/// A malformed entry is rejected individually and recorded in <see cref="Errors"/> — one
/// bad row must not take the rest of the catalog with it, and it must not be silently
/// repaired into something the file does not say.
/// </para>
/// </summary>
public sealed class IoCatalog
{
    private const string ResourceName = "CodeAnalyzer.Core.Resources.io-catalog.json";

    private static readonly Lazy<IoCatalog> BuiltInLazy = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    private IoCatalog(IReadOnlyList<IoCatalogEntry> entries, IReadOnlyList<string> errors)
    {
        Entries = entries;
        Errors = errors;
    }

    public IReadOnlyList<IoCatalogEntry> Entries { get; }

    /// <summary>Rejected entries, one message each, for diagnostics. Empty for the shipped file.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>The catalog shipped in this assembly. Loaded once and cached.</summary>
    public static IoCatalog BuiltIn => BuiltInLazy.Value;

    private static readonly HashSet<string> KnownLanguages = new(StringComparer.Ordinal)
    {
        LanguageNames.C,
        LanguageNames.Cpp,
        LanguageNames.CSharp,
        LanguageNames.Python,
        LanguageNames.Verilog,
        LanguageNames.Html,
    };

    /// <summary>
    /// Parses catalog JSON. Never throws on a bad entry — the entry is skipped and the
    /// reason recorded — but a document that is not JSON at all is a programming error
    /// in the shipped resource and does throw.
    /// </summary>
    public static IoCatalog Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        var entries = new List<IoCatalogEntry>();
        var errors = new List<string>();

        if (!document.RootElement.TryGetProperty("entries", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return new IoCatalog([], ["catalog root has no \"entries\" array"]);
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var entry = ParseEntry(element, index, errors);
            if (entry is not null)
            {
                entries.Add(entry);
            }

            index++;
        }

        return new IoCatalog(entries, errors);
    }

    private static IoCatalogEntry? ParseEntry(JsonElement element, int index, List<string> errors)
    {
        void Reject(string reason) => errors.Add($"entry {index}: {reason}");

        if (element.ValueKind != JsonValueKind.Object)
        {
            Reject("not an object");
            return null;
        }

        var family = ReadString(element, "family");
        if (string.IsNullOrWhiteSpace(family))
        {
            Reject("missing family");
            return null;
        }

        var languages = ReadStringList(element, "languages");
        if (languages.Count == 0)
        {
            Reject("missing languages");
            return null;
        }

        foreach (var language in languages)
        {
            if (!KnownLanguages.Contains(language))
            {
                // A typo here would compile into an entry that silently never matches.
                Reject($"unknown language \"{language}\"");
                return null;
            }
        }

        var name = ReadString(element, "name");
        var prefix = ReadString(element, "prefix");
        if (string.IsNullOrEmpty(name) == string.IsNullOrEmpty(prefix))
        {
            Reject("exactly one of name/prefix required");
            return null;
        }

        var direction = ReadString(element, "direction") switch
        {
            "in" => IoDirection.Input,
            "out" => IoDirection.Output,
            "inout" => IoDirection.InOut,
            _ => (IoDirection?)null,
        };
        if (direction is null)
        {
            Reject("direction must be \"in\", \"out\" or \"inout\"");
            return null;
        }

        return new IoCatalogEntry
        {
            Family = family!,
            Languages = languages,
            Name = string.IsNullOrEmpty(name) ? null : name,
            Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
            Direction = direction.Value,
            RequiredTypeRefs = ReadStringList(element, "requiresTypeRef"),
            RequiredDependencies = ReadStringList(element, "requiresDependency"),
        };
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringList(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                list.Add(text);
            }
        }

        return list;
    }
}
