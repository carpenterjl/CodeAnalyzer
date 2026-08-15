using System.Windows;
using CodeAnalyzer.App.ViewModels;
using CodeAnalyzer.App.Views;
using CodeAnalyzer.Core.Crawling;
using Microsoft.Win32;

namespace CodeAnalyzer.App.Services;

public sealed class DialogService : IDialogService
{
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrEmpty(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public WorkspaceSettings? EditSettings(WorkspaceSettings current)
    {
        var window = new SettingsWindow(new SettingsDialogViewModel(current))
        {
            Owner = Application.Current.MainWindow,
        };

        return window.ShowDialog() == true ? window.Result : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
