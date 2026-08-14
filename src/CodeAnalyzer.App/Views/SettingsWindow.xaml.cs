using System.Windows;
using CodeAnalyzer.App.ViewModels;
using CodeAnalyzer.Core.Crawling;

namespace CodeAnalyzer.App.Views;

/// <summary>
/// Workspace settings dialog. Deliberately dumb: the view model holds the edit state, and
/// the result travels back through <see cref="Result"/> so the service that opened the
/// dialog never touches controls.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsDialogViewModel _viewModel;

    public SettingsWindow(SettingsDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>The edited settings, set only when the user accepted with a valid value.</summary>
    public WorkspaceSettings? Result { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        // An unparseable size cap keeps the dialog open; the error text under the field
        // already says why.
        if (_viewModel.TryBuild() is { } settings)
        {
            Result = settings;
            DialogResult = true;
        }
    }
}
