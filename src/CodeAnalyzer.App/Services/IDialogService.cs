using CodeAnalyzer.Core.Crawling;

namespace CodeAnalyzer.App.Services;

public interface IDialogService
{
    /// <summary>Shows a folder picker. Returns null if the user cancels.</summary>
    string? PickFolder(string title, string? initialDirectory = null);

    /// <summary>Shows a save dialog. Returns the chosen path, or null if the user cancels.</summary>
    string? PickSaveFile(string title, string filter, string defaultFileName);

    /// <summary>
    /// Opens the workspace settings dialog. Returns the edited settings, or null when the
    /// user cancels — the caller decides what applying them means (a re-index).
    /// </summary>
    WorkspaceSettings? EditSettings(WorkspaceSettings current);

    /// <summary>A yes/no question. True means yes.</summary>
    bool Confirm(string title, string message);

    void ShowError(string title, string message);
}
