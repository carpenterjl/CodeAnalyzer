using System.Collections;
using System.Windows;

namespace CodeAnalyzer.App.Services;

public sealed class ThemeService : IThemeService
{
    // App.xaml keeps the active theme dictionary in slot 0 and shared control styles after it.
    private const int ThemeDictionaryIndex = 0;

    /// <summary>
    /// The dictionary every view actually reads from.
    /// <para>
    /// Slot 0 is claimed once by this empty, app-owned dictionary, and each theme is then
    /// copied into it key by key. Replacing the slot outright looks simpler but does not
    /// repaint a window that is already on screen: the DynamicResource references held by
    /// the live visual tree are not re-resolved when a merged dictionary is swapped, so
    /// only a restart would show the new theme. Writing the keys into a dictionary that is
    /// already merged invalidates each reference in turn, which is what makes Ctrl+T
    /// change the window rather than just the graph page.
    /// </para>
    /// <para>
    /// Copying is complete because Dark.xaml and Light.xaml define exactly the same keys —
    /// a key added to one and not the other would keep the previous theme's value here.
    /// </para>
    /// </summary>
    private ResourceDictionary? _active;

    public AppTheme Current { get; private set; } = AppTheme.Dark;

    public event EventHandler<AppTheme>? ThemeChanged;

    public void Apply(AppTheme theme)
    {
        var source = new ResourceDictionary
        {
            Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative),
        };

        foreach (DictionaryEntry entry in source)
        {
            ActiveDictionary()[entry.Key] = entry.Value;
        }

        Current = theme;
        ThemeChanged?.Invoke(this, theme);
    }

    public void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    private ResourceDictionary ActiveDictionary()
    {
        if (_active is not null)
        {
            return _active;
        }

        _active = [];

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (dictionaries.Count > ThemeDictionaryIndex)
        {
            dictionaries[ThemeDictionaryIndex] = _active;
        }
        else
        {
            dictionaries.Insert(ThemeDictionaryIndex, _active);
        }

        return _active;
    }
}
