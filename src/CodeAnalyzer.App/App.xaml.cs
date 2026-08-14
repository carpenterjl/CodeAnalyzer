using System.Windows;
using System.Windows.Threading;
using CodeAnalyzer.App.Services;
using CodeAnalyzer.App.ViewModels;
using CodeAnalyzer.App.Views;
using CodeAnalyzer.Core.Analysis;
using CodeAnalyzer.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeAnalyzer.App;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var builder = Host.CreateApplicationBuilder();
        ConfigureServices(builder.Services);
        _host = builder.Build();

        // No long-running hosted services yet, so this returns immediately.
        _host.Start();

        _host.Services.GetRequiredService<IThemeService>().Apply(AppTheme.Dark);

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Shell services
        services.AddSingleton<IUiDispatcher, UiDispatcher>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();

        // Parsing. The concrete tree-sitter factory is bound only here, so the rest of
        // the app depends on the abstraction.
        services.AddSingleton<IAnalyzerFactory, TreeSitterAnalyzerFactory>();

        // The renderer is registered twice on purpose: view models resolve the interface,
        // while GraphView needs the concrete type to hand over its WebView2 control.
        services.AddSingleton<WebView2GraphViewService>();
        services.AddSingleton<IGraphViewService>(p => p.GetRequiredService<WebView2GraphViewService>());
        services.AddSingleton<GraphView>();

        // View models: one instance each, since the shell owns exactly one of every pane.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<WorkspaceTreeViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<GraphViewModel>();
        services.AddSingleton<SymbolDetailViewModel>();
        services.AddSingleton<CodePreviewViewModel>();
        services.AddSingleton<StatusBarViewModel>();

        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Unhandled UI exception");

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "CodeAnalyzer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
