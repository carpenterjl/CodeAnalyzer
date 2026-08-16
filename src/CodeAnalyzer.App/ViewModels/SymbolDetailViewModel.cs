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
/// One call site behind an I/O boundary stub: where it is, who makes it, and the verbatim
/// argument list — the data that crosses the boundary.
/// </summary>
public sealed record IoSiteItem(
    long RefId,
    string RelativePath,
    string Language,
    int Line,
    string? ArgumentText,
    string? CallerName,
    long? CallerSymbolId);

/// <summary>One field of a frame layout, as the pane draws it.</summary>
public sealed record IoFrameMemberItem(string Name, string? TypeText, string? Value);

/// <summary>
/// One argument identifier at the selected boundary call site, with the chain of stored
/// facts behind it and every uncertainty spelled out — a confident-looking frame built on
/// a name match would be worse than none.
/// </summary>
public sealed record IoFrameArgumentItem(
    string Token,
    string? ChainText,
    string? WarningText,
    IReadOnlyList<IoFrameMemberItem> Members);

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
/// A definition elsewhere whose literal denotes the same value as the focused symbol's.
/// <para>
/// <paramref name="EqualityNote"/> is the row's justification and is worded once, in
/// <see cref="Core.Domain.ValueFacts"/>: "8'hA5 = 165 — numerically equal". The claim is
/// deliberately narrow — sharing a value is evidence of an agreement, not proof of one.
/// </para>
/// </summary>
public sealed record SameValueItem(
    long SymbolId,
    string Name,
    string KindLabel,
    string EqualityNote,
    string Language,
    string RelativePath,
    int Line,
    bool IsOtherLanguage);

/// <summary>
/// Facts about the focused symbol. Every property here maps to a stored column;
/// nothing is inferred, and unresolved references are listed rather than guessed at.
/// </summary>
public sealed partial class SymbolDetailViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyMessage))]
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

    // ---- I/O boundary stub mode ------------------------------------------

    /// <summary>
    /// True while the pane is showing an I/O boundary stub instead of a symbol. The two
    /// modes are exclusive: a stub is not a symbol, and pretending it is one would give
    /// it a kind and a definition it does not have.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyMessage))]
    private bool _hasIoStub;

    /// <summary>The boundary API's name, e.g. "HAL_UART_Transmit".</summary>
    [ObservableProperty]
    private string _ioStubName = string.Empty;

    /// <summary>"output boundary · catalog: STM32 HAL" — the direction and who asserted it.</summary>
    [ObservableProperty]
    private string _ioStubDescriptor = string.Empty;

    /// <summary>The co-occurrence rule that admitted a generic member name; null when ungated.</summary>
    [ObservableProperty]
    private string? _ioStubGateNote;

    public ObservableCollection<IoSiteItem> IoSites { get; } = [];

    /// <summary>The framing chain for the site currently open in the preview.</summary>
    public ObservableCollection<IoFrameArgumentItem> IoFrame { get; } = [];

    /// <summary>The pane's idle text shows only when neither mode has content.</summary>
    public bool ShowEmptyMessage => !HasSymbol && !HasIoStub;

    /// <summary>Empty unless the name is overloaded — a set of one is not a set.</summary>
    public ObservableCollection<OverloadItem> Overloads { get; } = [];

    // ---- same value elsewhere --------------------------------------------

    /// <summary>
    /// Definitions elsewhere carrying this symbol's value. Empty when the literal is not
    /// one the parser can certify, or when nothing else carries it — which are different
    /// facts from each other and both different from "not looked at".
    /// </summary>
    public ObservableCollection<SameValueItem> SameValues { get; } = [];

    /// <summary>"165 · also in C#, Python, Verilog" — what is shared and where it reaches.</summary>
    [ObservableProperty]
    private string? _sameValueSummary;

    /// <summary>Set when more definitions carry the value than the list shows.</summary>
    [ObservableProperty]
    private string? _sameValueTruncationNote;

    public ObservableCollection<MemberItem> Members { get; } = [];

    public ObservableCollection<RelatedSymbolItem> Callers { get; } = [];

    public ObservableCollection<RelatedSymbolItem> Callees { get; } = [];

    public ObservableCollection<UnresolvedReferenceItem> UnresolvedReferences { get; } = [];

    /// <summary>Leaves symbol content in place; showing a symbol clears this the same way.</summary>
    public void ClearIoStub()
    {
        HasIoStub = false;
        IoStubName = string.Empty;
        IoStubDescriptor = string.Empty;
        IoStubGateNote = null;
        IoSites.Clear();
        IoFrame.Clear();
    }

    public void Clear()
    {
        ClearIoStub();
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
        SameValues.Clear();
        SameValueSummary = null;
        SameValueTruncationNote = null;
        Members.Clear();
        Callers.Clear();
        Callees.Clear();
        UnresolvedReferences.Clear();
    }
}
