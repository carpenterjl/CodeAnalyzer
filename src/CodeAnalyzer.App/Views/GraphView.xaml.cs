using System.Windows;
using System.Windows.Controls;
using CodeAnalyzer.App.Services;

namespace CodeAnalyzer.App.Views;

/// <summary>
/// Hosts the WebView2 control and hands it to <see cref="WebView2GraphViewService"/>.
/// <para>
/// This code-behind is the only place in the app that touches a WebView2 type outside the
/// service itself; view models see nothing but <see cref="IGraphViewService"/>. Startup is
/// awaited rather than blocked on, so a slow runtime launch never freezes the shell.
/// </para>
/// </summary>
public partial class GraphView : UserControl
{
    private readonly WebView2GraphViewService _service;

    public GraphView(WebView2GraphViewService service)
    {
        _service = service;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Only ever initialise once, even if the control is re-parented.
        Loaded -= OnLoaded;

        try
        {
            await _service.AttachAsync(Browser);

            Browser.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // The most likely cause by far is a missing WebView2 runtime, so say that
            // plainly instead of showing a stack trace in the middle of the window.
            Browser.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text =
                "The graph renderer could not start. It needs the Microsoft Edge WebView2 " +
                "runtime, which ships with Windows 11 and is a free download for Windows 10.\n\n" +
                ex.Message;
        }
    }
}
