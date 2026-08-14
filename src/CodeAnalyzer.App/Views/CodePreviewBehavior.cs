using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace CodeAnalyzer.App.Views;

/// <summary>
/// Binds an AvalonEdit editor to plain view-model properties.
/// <para>
/// AvalonEdit's <c>Text</c> is a plain CLR property and cannot be bound, so the alternative
/// would be handing a <c>TextDocument</c> to the view model. These attached properties keep
/// the editor type inside the view layer instead.
/// </para>
/// </summary>
public static class CodePreviewBehavior
{
    /// <summary>Source text to display.</summary>
    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.RegisterAttached(
            "SourceText",
            typeof(string),
            typeof(CodePreviewBehavior),
            new PropertyMetadata(string.Empty, OnSourceTextChanged));

    /// <summary>File path, used only to choose a syntax highlighter by extension.</summary>
    public static readonly DependencyProperty FilePathProperty =
        DependencyProperty.RegisterAttached(
            "FilePath",
            typeof(string),
            typeof(CodePreviewBehavior),
            new PropertyMetadata(null, OnFilePathChanged));

    /// <summary>1-based line to reveal and highlight. Zero means "leave the view alone".</summary>
    public static readonly DependencyProperty HighlightLineProperty =
        DependencyProperty.RegisterAttached(
            "HighlightLine",
            typeof(int),
            typeof(CodePreviewBehavior),
            new PropertyMetadata(0, OnHighlightLineChanged));

    public static void SetSourceText(DependencyObject element, string value) =>
        element.SetValue(SourceTextProperty, value);

    public static string GetSourceText(DependencyObject element) =>
        (string)element.GetValue(SourceTextProperty);

    public static void SetFilePath(DependencyObject element, string? value) =>
        element.SetValue(FilePathProperty, value);

    public static string? GetFilePath(DependencyObject element) =>
        (string?)element.GetValue(FilePathProperty);

    public static void SetHighlightLine(DependencyObject element, int value) =>
        element.SetValue(HighlightLineProperty, value);

    public static int GetHighlightLine(DependencyObject element) =>
        (int)element.GetValue(HighlightLineProperty);

    private static void OnSourceTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor)
        {
            return;
        }

        var text = e.NewValue as string ?? string.Empty;
        if (editor.Text == text)
        {
            return;
        }

        editor.Text = text;
        editor.ScrollToHome();

        // Replacing the document resets the caret, so re-apply the target line afterwards.
        RevealLine(editor, GetHighlightLine(editor));
    }

    private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor)
        {
            return;
        }

        var path = e.NewValue as string;
        var extension = string.IsNullOrEmpty(path) ? null : Path.GetExtension(path);

        // Matching on extension rather than our own language name means AvalonEdit's own
        // mapping decides, so .h picks up the C/C++ highlighter without a table here.
        editor.SyntaxHighlighting = string.IsNullOrEmpty(extension)
            ? null
            : HighlightingManager.Instance.GetDefinitionByExtension(extension);
    }

    private static void OnHighlightLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextEditor editor)
        {
            RevealLine(editor, (int)e.NewValue);
        }
    }

    private static void RevealLine(TextEditor editor, int line)
    {
        if (line <= 0 || editor.Document is null || line > editor.Document.LineCount)
        {
            return;
        }

        // Deferred to Loaded priority: right after a document swap the text view has no
        // measured line height yet, and scrolling would land in the wrong place.
        editor.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (line > editor.Document.LineCount)
                {
                    return;
                }

                editor.TextArea.Caret.Line = line;
                editor.TextArea.Caret.Column = 1;
                editor.ScrollToLine(line);
            }));
    }

    /// <summary>
    /// Recolours the shared highlighting definitions for a dark background.
    /// <para>
    /// AvalonEdit ships a single palette tuned for black-on-white. On the dark surface its
    /// near-black identifiers and punctuation disappear entirely, so every colour has to be
    /// dealt with, not just the ones worth styling: named roles get a deliberate colour and
    /// anything else is lifted until it is legible. The original palette is snapshotted on
    /// first use so switching back to the light theme restores it exactly.
    /// </para>
    /// </summary>
    public static void ApplySyntaxTheme(bool dark)
    {
        CaptureOriginalPalette();

        foreach (var (colour, original) in OriginalForegrounds)
        {
            colour.Foreground = original;

            if (!dark)
            {
                continue;
            }

            var replacement = DarkColourFor(colour.Name) ?? Lift(original);
            if (replacement.HasValue)
            {
                colour.Foreground = new SimpleHighlightingBrush(replacement.Value);
            }
        }
    }

    // Reference equality: two roles can share a colour value without being the same entry,
    // and restoring the wrong original would corrupt the palette.
    private static readonly Dictionary<HighlightingColor, HighlightingBrush?> OriginalForegrounds =
        new(ReferenceEqualityComparer.Instance);

    private static bool _paletteCaptured;

    private static void CaptureOriginalPalette()
    {
        if (_paletteCaptured)
        {
            return;
        }

        _paletteCaptured = true;

        foreach (var definition in HighlightingManager.Instance.HighlightingDefinitions)
        {
            foreach (var colour in definition.NamedHighlightingColors)
            {
                OriginalForegrounds.TryAdd(colour, colour.Foreground);
            }

            // Named colours are only part of the story: plenty of rules carry an inline
            // colour, and those are exactly the ones that end up invisible.
            Walk(definition.MainRuleSet, new HashSet<HighlightingRuleSet>(ReferenceEqualityComparer.Instance));
        }
    }

    private static void Walk(HighlightingRuleSet? ruleSet, HashSet<HighlightingRuleSet> seen)
    {
        if (ruleSet is null || !seen.Add(ruleSet))
        {
            return;
        }

        foreach (var rule in ruleSet.Rules)
        {
            Remember(rule.Color);
        }

        foreach (var span in ruleSet.Spans)
        {
            Remember(span.StartColor);
            Remember(span.SpanColor);
            Remember(span.EndColor);
            Walk(span.RuleSet, seen);
        }
    }

    private static void Remember(HighlightingColor? colour)
    {
        if (colour is not null)
        {
            OriginalForegrounds.TryAdd(colour, colour.Foreground);
        }
    }

    private static Color? DarkColourFor(string? name) => name is null ? null : Role(name) switch
    {
        SyntaxRole.Comment => Color.FromRgb(0x6A, 0x82, 0x6A),
        SyntaxRole.String => Color.FromRgb(0x9E, 0xCE, 0x6A),
        SyntaxRole.Number => Color.FromRgb(0xE0, 0xAF, 0x68),
        SyntaxRole.Preprocessor => Color.FromRgb(0xF7, 0x76, 0x8E),
        SyntaxRole.Type => Color.FromRgb(0xC3, 0x9A, 0xF0),
        SyntaxRole.Method => Color.FromRgb(0x7D, 0xCF, 0xFF),
        SyntaxRole.Keyword => Color.FromRgb(0x7A, 0xA2, 0xF7),
        _ => null,
    };

    /// <summary>Default text colour for anything the palette leaves unstyled.</summary>
    private static readonly Color UnstyledForeground = Color.FromRgb(0xC8, 0xCE, 0xD6);

    /// <summary>Below this relative luminance a colour is unreadable on the dark surface.</summary>
    private const double MinimumLuminance = 0.45;

    /// <summary>
    /// Blends a too-dark colour toward white until it is readable, keeping its hue so the
    /// definition's own distinctions survive. A null brush means "inherit", which is
    /// already the right answer.
    /// </summary>
    private static Color? Lift(HighlightingBrush? brush)
    {
        if (brush is null)
        {
            return null;
        }

        Color colour;
        try
        {
            // Simple brushes ignore the context; anything more exotic is left alone.
            if (brush.GetColor(null!) is not { } resolved)
            {
                return null;
            }

            colour = resolved;
        }
        catch (Exception e) when (e is NullReferenceException or NotSupportedException)
        {
            return null;
        }

        var luminance = ((0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B)) / 255.0;
        if (luminance >= MinimumLuminance)
        {
            return null;
        }

        if (colour is { R: 0, G: 0, B: 0 })
        {
            return UnstyledForeground;
        }

        var blend = Math.Clamp((MinimumLuminance - luminance) / MinimumLuminance, 0, 0.75);
        return Color.FromRgb(
            Mix(colour.R, blend),
            Mix(colour.G, blend),
            Mix(colour.B, blend));
    }

    private static byte Mix(byte channel, double towardsWhite) =>
        (byte)Math.Round(channel + ((255 - channel) * towardsWhite));

    private enum SyntaxRole
    {
        Other,
        Comment,
        String,
        Number,
        Preprocessor,
        Type,
        Method,
        Keyword,
    }

    private static SyntaxRole Role(string name)
    {
        if (Has(name, "Comment") || Has(name, "Documentation"))
        {
            return SyntaxRole.Comment;
        }

        if (Has(name, "String") || Has(name, "Char"))
        {
            return SyntaxRole.String;
        }

        if (Has(name, "Number") || Has(name, "Digits") || Has(name, "Literal"))
        {
            return SyntaxRole.Number;
        }

        if (Has(name, "Preprocessor") || Has(name, "Directive"))
        {
            return SyntaxRole.Preprocessor;
        }

        if (Has(name, "Type") || Has(name, "Class"))
        {
            return SyntaxRole.Type;
        }

        if (Has(name, "Method") || Has(name, "Function"))
        {
            return SyntaxRole.Method;
        }

        // Everything left that reads as a language word: keywords, modifiers, visibility.
        return Has(name, "Keyword") || Has(name, "Modifier") || Has(name, "Visibility")
            ? SyntaxRole.Keyword
            : SyntaxRole.Other;
    }

    private static bool Has(string name, string fragment) =>
        name.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
