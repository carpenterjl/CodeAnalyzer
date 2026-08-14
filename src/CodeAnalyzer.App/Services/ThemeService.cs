using System.Windows;

namespace CodeAnalyzer.App.Services;

public sealed class ThemeService : IThemeService
{
    // App.xaml keeps the active theme dictionary in slot 0 and shared control styles after it.
    private const int ThemeDictionaryIndex = 0;

    public AppTheme Current { get; private set; } = AppTheme.Dark;

    public event EventHandler<AppTheme>? ThemeChanged;

    public void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative),
        };

        if (dictionaries.Count > ThemeDictionaryIndex)
        {
            dictionaries[ThemeDictionaryIndex] = replacement;
        }
        else
        {
            dictionaries.Insert(ThemeDictionaryIndex, replacement);
        }

        Current = theme;
        ThemeChanged?.Invoke(this, theme);
    }

    public void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
}
