using System.Reflection;

namespace CodeAnalyzer.Parsing;

/// <summary>
/// The .scm query sources for one language, loaded from embedded resources.
/// </summary>
public sealed record QueryPack(string Name, string SymbolQuery, string ReferenceQuery)
{
    private const string ResourcePrefix = "CodeAnalyzer.Parsing.Queries.";

    private static readonly Assembly OwningAssembly = typeof(QueryPack).Assembly;

    public static bool Exists(string packName) =>
        ReadResource(packName, "symbols.scm") is not null;

    /// <summary>
    /// Loads a pack. Throws when the pack is missing, because reaching here means the
    /// registry claims a language is supported while its queries were never embedded.
    /// </summary>
    public static QueryPack Load(string packName)
    {
        var symbols = ReadResource(packName, "symbols.scm")
            ?? throw new InvalidOperationException(
                $"Query pack '{packName}' has no embedded symbols.scm. " +
                $"Expected resource '{ResourcePrefix}{packName}.symbols.scm'.");

        // A language may legitimately define no references, so this one is optional.
        var references = ReadResource(packName, "refs.scm") ?? string.Empty;

        return new QueryPack(packName, symbols, references);
    }

    private static string? ReadResource(string packName, string fileName)
    {
        var resourceName = $"{ResourcePrefix}{packName}.{fileName}";
        using var stream = OwningAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
