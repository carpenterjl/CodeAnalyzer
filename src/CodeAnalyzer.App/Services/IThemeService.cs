namespace CodeAnalyzer.App.Services;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Swaps the active theme dictionary. The graph view subscribes to <see cref="ThemeChanged"/>
/// so the WebView2 page can flip its CSS variables in step with WPF.
/// </summary>
public interface IThemeService
{
    AppTheme Current { get; }

    event EventHandler<AppTheme>? ThemeChanged;

    void Apply(AppTheme theme);

    void Toggle();
}
