using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodeAnalyzer.App.Services;
using CodeAnalyzer.App.ViewModels;

namespace CodeAnalyzer.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel, GraphView graphView, IThemeService themeService)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        // Restoring on Loaded rather than in the constructor means the shell is already on
        // screen and interactive while the previous workspace's cached index comes back.
        Loaded += async (_, _) => await viewModel.RestoreSessionAsync();
        Closing += (_, _) => viewModel.SaveSession();

        // Focus is view state, so Ctrl+F ends here rather than in the view model.
        viewModel.SearchFocusRequested += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };

        // Injected rather than created in XAML: the graph view needs its renderer service
        // from the container, and a parameterless XAML instance could not get one.
        GraphHost.Content = graphView;

        // AvalonEdit's highlighting palette is a process-wide singleton, so retinting it
        // is a view-layer concern rather than something a view model should reach into.
        CodePreviewBehavior.ApplySyntaxTheme(themeService.Current == AppTheme.Dark);
        themeService.ThemeChanged += (_, theme) =>
            CodePreviewBehavior.ApplySyntaxTheme(theme == AppTheme.Dark);
    }

    /// <summary>
    /// Down moves from the search box into the results — selecting the first result, which
    /// also focuses it in the graph. Escape clears the query without leaving the box.
    /// </summary>
    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _viewModel.Search.Results.Count > 0)
        {
            _viewModel.Search.SelectedResult = _viewModel.Search.Results[0];

            if (SearchResults.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
            {
                item.Focus();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.Search.Query = string.Empty;
            e.Handled = true;
        }
    }
}
