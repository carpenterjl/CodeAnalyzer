using System.Collections.ObjectModel;
using CodeAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>One row in the search results list.</summary>
public sealed record SearchResultItem(
    long SymbolId,
    string Name,
    string KindLabel,
    string ScopePath,
    string RelativePath,
    int Line,
    string? ContainerName = null,
    string? Parameters = null,
    string Descriptor = "")
{
    /// <summary>
    /// What the row shows: the containing symbol prefixed and the parameter list appended,
    /// so two same-named members in different classes — and two overloads in the same one —
    /// are tellable apart without opening either. <see cref="Name"/> stays bare because
    /// relocation after a live update matches on it.
    /// </summary>
    public string DisplayName =>
        (ContainerName is null ? Name : $"{ContainerName}.{Name}") + Parameters;
}

/// <summary>
/// One kind-family toggle above the results. The families are the ones the graph already
/// colours by, so a filter and the picture it produces use the same vocabulary.
/// </summary>
public sealed partial class SearchKindFilter(string group) : ObservableObject
{
    /// <summary>The group name from <see cref="SymbolKindGroups"/>. Not shown to the user.</summary>
    public string Group { get; } = group;

    public string Label { get; } = group;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Fuzzy symbol search. M3 wires <see cref="Query"/> to the scorer behind a debounce;
/// for now it holds the shape the view binds to.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    public SearchViewModel()
    {
        foreach (var group in SymbolKindGroups.All)
        {
            var filter = new SearchKindFilter(group);

            // One event for the whole row of toggles: the shell only needs to know that
            // the filter changed, not which chip did it.
            filter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SearchKindFilter.IsSelected))
                {
                    OnPropertyChanged(nameof(HasKindFilter));
                    KindFilterChanged?.Invoke(this, EventArgs.Empty);
                }
            };

            KindFilters.Add(filter);
        }
    }

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private SearchResultItem? _selectedResult;

    [ObservableProperty]
    private string _emptyMessage = "Open a workspace to search symbols.";

    /// <summary>
    /// Something the result list cannot say for itself — that it was cut, or which
    /// question was actually asked. Null most of the time; shown above the rows, because
    /// a list that silently stops short is the one thing a search must never be.
    /// </summary>
    [ObservableProperty]
    private string? _notice;

    public ObservableCollection<SearchResultItem> Results { get; } = [];

    /// <summary>The kind-family toggles. Nothing selected means no filter at all.</summary>
    public ObservableCollection<SearchKindFilter> KindFilters { get; } = [];

    /// <summary>Drives the "clear filters" affordance, which is pointless when none are set.</summary>
    public bool HasKindFilter => KindFilters.Any(filter => filter.IsSelected);

    /// <summary>A kind toggle changed, so the shell re-runs the current query.</summary>
    public event EventHandler? KindFilterChanged;

    /// <summary>
    /// The kinds a search should be restricted to, or null for no restriction. Null rather
    /// than "every kind" so that an unfiltered search takes exactly the path it always did.
    /// </summary>
    public IReadOnlySet<SymbolKind>? SelectedKinds()
    {
        var groups = KindFilters.Where(filter => filter.IsSelected).Select(filter => filter.Group).ToList();
        return groups.Count == 0 ? null : SymbolKindGroups.KindsIn(groups);
    }

    /// <summary>Returns the list to its unfiltered state.</summary>
    public void ClearKindFilters()
    {
        foreach (var filter in KindFilters)
        {
            filter.IsSelected = false;
        }
    }

    public event EventHandler<SearchResultItem>? ResultActivated;

    /// <summary>
    /// Selecting a result focuses it. Keyboard arrows therefore walk the graph through the
    /// results, which is faster than requiring a second click to open each one.
    /// </summary>
    partial void OnSelectedResultChanged(SearchResultItem? value)
    {
        if (value is not null)
        {
            ResultActivated?.Invoke(this, value);
        }
    }
}
