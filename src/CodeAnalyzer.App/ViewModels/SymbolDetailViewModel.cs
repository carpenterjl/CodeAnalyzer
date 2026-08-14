using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>A member of a struct, class, or Verilog module, shown in the composition inspector.</summary>
public sealed record MemberItem(long SymbolId, string Name, string KindLabel, string? TypeText, string? Value);

/// <summary>A caller or callee row. <paramref name="ConfidenceLabel"/> is shown verbatim so
/// the user can see when a link is only a name match.</summary>
public sealed record RelatedSymbolItem(
    long SymbolId,
    string Name,
    string KindLabel,
    string RelativePath,
    int Line,
    string ConfidenceLabel,
    bool IsAmbiguous);

/// <summary>A reference that matched no definition in the workspace, e.g. a libc call.</summary>
public sealed record UnresolvedReferenceItem(string Name, string KindLabel, int Line);

/// <summary>
/// One definition sharing the focused symbol's name in its own scope.
/// <para>
/// <paramref name="Parameters"/> is the whole point of the row: the members of an overload
/// set differ in nothing else, so a list showing only names would repeat one line N times.
/// </para>
/// </summary>
public sealed record OverloadItem(
    long SymbolId,
    string Parameters,
    int Line,
    bool IsCurrent);

/// <summary>
/// Facts about the focused symbol. Every property here maps to a stored column;
/// nothing is inferred, and unresolved references are listed rather than guessed at.
/// </summary>
public sealed partial class SymbolDetailViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _hasSymbol;

    [ObservableProperty]
    private long _symbolId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _kindLabel = string.Empty;

    /// <summary>
    /// What this symbol is, in one line — "public method · overload 2 of 3". Composed by
    /// <see cref="Core.Domain.SymbolFacts"/> so the pane, the graph node and the search
    /// row cannot word the same facts three different ways.
    /// </summary>
    [ObservableProperty]
    private string _descriptor = string.Empty;

    /// <summary>The parameter list as written, or null where the declaration has none.</summary>
    [ObservableProperty]
    private string? _parameters;

    [ObservableProperty]
    private string? _scopePath;

    [ObservableProperty]
    private string? _signature;

    /// <summary>Literal initializer, verbatim from source, for constants and parameters.</summary>
    [ObservableProperty]
    private string? _value;

    [ObservableProperty]
    private string? _typeText;

    /// <summary>Verbatim modifier keywords in source order, e.g. "public sealed override".</summary>
    [ObservableProperty]
    private string? _modifiers;

    [ObservableProperty]
    private string? _relativePath;

    [ObservableProperty]
    private int _line;

    [ObservableProperty]
    private string? _language;

    [ObservableProperty]
    private string _emptyMessage = "Select a symbol to see its details.";

    /// <summary>Empty unless the name is overloaded — a set of one is not a set.</summary>
    public ObservableCollection<OverloadItem> Overloads { get; } = [];

    public ObservableCollection<MemberItem> Members { get; } = [];

    public ObservableCollection<RelatedSymbolItem> Callers { get; } = [];

    public ObservableCollection<RelatedSymbolItem> Callees { get; } = [];

    public ObservableCollection<UnresolvedReferenceItem> UnresolvedReferences { get; } = [];

    public void Clear()
    {
        HasSymbol = false;
        SymbolId = 0;
        Name = string.Empty;
        KindLabel = string.Empty;
        Descriptor = string.Empty;
        Parameters = null;
        ScopePath = null;
        Signature = null;
        Value = null;
        TypeText = null;
        Modifiers = null;
        RelativePath = null;
        Line = 0;
        Language = null;
        Overloads.Clear();
        Members.Clear();
        Callers.Clear();
        Callees.Clear();
        UnresolvedReferences.Clear();
    }
}
