using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// Indexing progress surface. The orchestrator reports into this via IProgress, throttled
/// to roughly 10 Hz, so the UI thread only ever applies small property updates.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string _message = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private bool _isIndexing;

    [ObservableProperty]
    private int _filesDiscovered;

    [ObservableProperty]
    private int _filesProcessed;

    [ObservableProperty]
    private int _errorCount;

    /// <summary>Null renders an indeterminate bar, which is what the crawl phase wants.</summary>
    [ObservableProperty]
    private double? _progressPercent;

    public bool HasProgress => IsIndexing;

    public event EventHandler? CancelRequested;

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    public void Reset()
    {
        IsIndexing = false;
        FilesDiscovered = 0;
        FilesProcessed = 0;
        ErrorCount = 0;
        ProgressPercent = null;
        Message = "Ready";
    }
}
