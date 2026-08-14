using CodeAnalyzer.Core.Crawling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// Edit state for the workspace settings dialog. Holds text, not parsed values, so the
/// user can type freely; <see cref="TryBuild"/> is where the text has to become numbers.
/// </summary>
public sealed partial class SettingsDialogViewModel : ObservableObject
{
    /// <summary>Sanity bounds on the size cap, in megabytes. Zero would index nothing.</summary>
    public const double MinSizeCapMb = 0.1;

    public const double MaxSizeCapMb = 500;

    public SettingsDialogViewModel(WorkspaceSettings current)
    {
        IgnoreText = string.Join(Environment.NewLine, current.ExtraIgnoredDirectories);
        MaxFileSizeMbText = (current.MaxFileSizeBytes / (1024.0 * 1024.0)).ToString("0.##");
    }

    /// <summary>One directory name per line. Entries with path separators are dropped.</summary>
    [ObservableProperty]
    private string _ignoreText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeCapError))]
    private string _maxFileSizeMbText = string.Empty;

    /// <summary>Validation text under the size field; null when the value parses.</summary>
    public string? SizeCapError =>
        TryParseSizeCap(out _) ? null : $"Enter a size between {MinSizeCapMb} and {MaxSizeCapMb} MB.";

    private bool TryParseSizeCap(out long bytes)
    {
        bytes = 0;
        if (!double.TryParse(MaxFileSizeMbText, out var mb) || mb < MinSizeCapMb || mb > MaxSizeCapMb)
        {
            return false;
        }

        bytes = (long)(mb * 1024 * 1024);
        return true;
    }

    /// <summary>The edited settings, or null while the size cap does not parse.</summary>
    public WorkspaceSettings? TryBuild() => TryParseSizeCap(out var bytes)
        ? new WorkspaceSettings
        {
            ExtraIgnoredDirectories = WorkspaceSettings.ParseDirectoryNames(IgnoreText),
            MaxFileSizeBytes = bytes,
        }
        : null;
}
