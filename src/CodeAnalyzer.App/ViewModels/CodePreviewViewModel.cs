using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// Read-only source preview for the selected symbol. M4 swaps the backing text control
/// for AvalonEdit with syntax highlighting; the view model contract stays the same.
/// </summary>
public sealed partial class CodePreviewViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _relativePath;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _language;

    /// <summary>1-based line to scroll to and highlight.</summary>
    [ObservableProperty]
    private int _highlightLine;

    [ObservableProperty]
    private bool _hasContent;

    [ObservableProperty]
    private string _emptyMessage = "Select a symbol to preview its source.";

    public void Clear()
    {
        RelativePath = null;
        Text = string.Empty;
        Language = null;
        HighlightLine = 0;
        HasContent = false;
    }
}
