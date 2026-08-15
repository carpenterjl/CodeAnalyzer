using System.Collections.ObjectModel;
using CodeAnalyzer.Core.Crawling;
using CodeAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeAnalyzer.App.ViewModels;

/// <summary>
/// One user I/O mark as the settings dialog lists it. The mark itself rides along so
/// removal and rebuild never have to reconstruct one from display text.
/// </summary>
public sealed record IoMarkRow(IoMark Mark)
{
    public string Name => Mark.Name;

    public string DirectionLabel => IoDirectionLabels.For(Mark.Direction);

    /// <summary>Where the mark applies — a path, or the whole workspace.</summary>
    public string ScopeLabel => Mark.Scope ?? "whole workspace";
}

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

        _initialHonorGitIgnore = current.HonorGitIgnore;
        HonorGitIgnore = current.HonorGitIgnore == true;

        foreach (var mark in current.IoMarks)
        {
            IoMarks.Add(new IoMarkRow(mark));
        }
    }

    /// <summary>
    /// The workspace's I/O boundary marks. Created from the detail pane's context menu;
    /// the dialog only lists and removes them.
    /// </summary>
    public ObservableCollection<IoMarkRow> IoMarks { get; } = [];

    [RelayCommand]
    private void RemoveIoMark(IoMarkRow row) => IoMarks.Remove(row);

    /// <summary>
    /// Null means the user has never been asked (the ask-once prompt fires on open). The
    /// dialog shows a plain two-state box; an untouched box preserves the null so the
    /// prompt still fires, and any change is an explicit answer.
    /// </summary>
    private readonly bool? _initialHonorGitIgnore;

    [ObservableProperty]
    private bool _honorGitIgnore;

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
            HonorGitIgnore = HonorGitIgnore == (_initialHonorGitIgnore == true)
                ? _initialHonorGitIgnore
                : HonorGitIgnore,
            IoMarks = IoMarks.Select(row => row.Mark).ToList(),
        }
        : null;
}
