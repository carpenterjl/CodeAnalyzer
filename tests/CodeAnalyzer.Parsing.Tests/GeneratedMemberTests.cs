using CodeAnalyzer.Core.Domain;
using CodeAnalyzer.Parsing;
using Xunit;

namespace CodeAnalyzer.Parsing.Tests;

/// <summary>
/// The members a source generator adds, which are written in no source file and were
/// therefore in no index.
/// <para>
/// This is the one place the index deliberately holds a definition nobody typed, so these
/// tests are as much about the limits as the feature: only the named attributes, only where
/// the generated name is a pure function of the declared one, never over a name the file
/// already declares, and always carrying the attribute in its modifiers so no reader
/// mistakes it for something someone wrote.
/// </para>
/// <para>
/// Measured before it was built, on OpenSim Studio: 239 <c>[ObservableProperty]</c> fields
/// and 70 <c>[RelayCommand]</c> methods across 26 files, producing 300 distinct names of
/// which the index held 265 nowhere — and 37 unresolved bindings plus 925 other unresolved
/// references naming one of them.
/// </para>
/// </summary>
public class GeneratedMemberTests : IDisposable
{
    private readonly TreeSitterAnalyzer _csharp =
        new(LanguageRegistry.ForName(LanguageRegistry.CSharp)!);

    public void Dispose() => _csharp.Dispose();

    private static SymbolRecord? Find(ParseResult result, string name) =>
        result.Symbols.FirstOrDefault(s => s.Name == name);

    [Fact]
    public void AnObservablePropertyFieldAlsoDeclaresThePropertyItGenerates()
    {
        var result = _csharp.Analyze("vm.cs", """
            public partial class GeometryViewModel
            {
                [ObservableProperty] private double _boxSizeX = 0.1;
            }
            """, CancellationToken.None);

        var generated = Find(result, "BoxSizeX");

        Assert.NotNull(generated);
        Assert.Equal(SymbolKind.Property, generated!.Kind);
        Assert.Equal("double", generated.TypeText);

        // The field it came from is untouched and still a field.
        Assert.Equal(SymbolKind.Field, Find(result, "_boxSizeX")!.Kind);
    }

    /// <summary>
    /// The honesty requirement. Everything else in the index is a thing someone wrote; these
    /// rows are not, and they say so in the column every surface already prints.
    /// </summary>
    [Fact]
    public void AGeneratedMemberNamesTheAttributeThatGeneratedIt()
    {
        var result = _csharp.Analyze("vm.cs", """
            public partial class MainViewModel
            {
                [ObservableProperty] private string _title = "";

                [RelayCommand]
                private void OpenWorkspace() { }
            }
            """, CancellationToken.None);

        Assert.Equal("[ObservableProperty] public", Find(result, "Title")!.Modifiers);
        Assert.Equal("[RelayCommand] public", Find(result, "OpenWorkspaceCommand")!.Modifiers);
    }

    [Fact]
    public void ARelayCommandMethodGeneratesACommandAndDropsTheAsyncSuffix()
    {
        var result = _csharp.Analyze("vm.cs", """
            public partial class MainViewModel
            {
                [RelayCommand]
                private async Task ReindexAsync() { }
            }
            """, CancellationToken.None);

        var generated = Find(result, "ReindexCommand");

        Assert.NotNull(generated);
        Assert.Equal(SymbolKind.Property, generated!.Kind);
        Assert.Equal("IRelayCommand", generated.TypeText);
        Assert.Null(Find(result, "ReindexAsyncCommand"));
    }

    /// <summary>
    /// A hand-written member beside the generated one means the author turned the generator
    /// off for it, and the real declaration is the better answer. Inventing a second row of
    /// the same name would put a phantom overload in every listing of that type.
    /// </summary>
    [Fact]
    public void ANameTheFileAlreadyDeclaresIsNotInvented()
    {
        var result = _csharp.Analyze("vm.cs", """
            public partial class MainViewModel
            {
                [ObservableProperty] private string _title = "";

                public string Title => _title.Trim();
            }
            """, CancellationToken.None);

        var titles = result.Symbols.Where(s => s.Name == "Title").ToList();

        // One Title, and it is the one in the file: the generated row would have carried the
        // attribute in its modifiers, and this one carries what the author wrote.
        Assert.Single(titles);
        Assert.DoesNotContain("[ObservableProperty]", titles[0].Modifiers ?? string.Empty);
    }

    /// <summary>
    /// The generated member belongs to the type, not to the field it was generated from —
    /// the two share a span exactly, and containment cannot be read off a tie.
    /// </summary>
    [Fact]
    public void AGeneratedMemberIsAMemberOfTheTypeNotOfItsBackingField()
    {
        var result = _csharp.Analyze("vm.cs", """
            public partial class SettingsViewModel
            {
                [ObservableProperty] private string _ignoreText = "";
            }
            """, CancellationToken.None);

        var symbols = result.Symbols;
        var generated = symbols.Single(s => s.Name == "IgnoreText");
        var container = symbols[generated.ContainerLocalIndex!.Value];

        Assert.Equal("SettingsViewModel", container.Name);
    }

    /// <summary>
    /// A field whose name already reads as the property generates nothing: the generator
    /// needs a name to differ from, and emitting one here would duplicate a declaration the
    /// index already holds.
    /// </summary>
    [Fact]
    public void AFieldAlreadySpelledLikeItsPropertyGeneratesNothing()
    {
        var result = _csharp.Analyze("vm.cs", """
            public partial class Odd
            {
                [ObservableProperty] private string Title = "";
            }
            """, CancellationToken.None);

        Assert.Single(result.Symbols, s => s.Name == "Title");
    }

    /// <summary>
    /// An attribute with no rule generates nothing. Test attributes are on most methods in
    /// most repositories, and a mechanism that invented a member per attribute would double
    /// the size of every test project's index.
    /// </summary>
    [Fact]
    public void AnUnknownAttributeGeneratesNothing()
    {
        var result = _csharp.Analyze("t.cs", """
            public class Tests
            {
                [Fact]
                private void Works() { }
            }
            """, CancellationToken.None);

        Assert.Equal(1, result.Symbols.Count(s => s.Name.StartsWith("Works", StringComparison.Ordinal)));
    }
}
