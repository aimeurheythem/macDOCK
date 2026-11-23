using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MacDock;

/// <summary>
/// The typeface of the whole app. Apple's San Francisco (SF Pro) is used when it is installed; its
/// licence doesn't allow shipping it with a Windows app, so otherwise MacDock uses Inter (bundled, SIL
/// Open Font Licence), which was designed after it and reads the same at interface sizes. Text is
/// drawn the way macOS draws it: ideal glyph spacing and smooth grayscale anti-aliasing.
/// </summary>
public static class MacFonts
{
    public static readonly FontFamily Text = Create();

    private static FontFamily Create()
    {
        foreach (var name in new[] { "SF Pro Text", "SF Pro", "SF Pro Display" })
            if (Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase)))
                return new FontFamily(name + ", Segoe UI Variable Text, Segoe UI, Segoe UI Emoji");
        return new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Inter, Segoe UI Variable Text, Segoe UI, Segoe UI Emoji");
    }

    /// <summary>Makes the Mac typeface and smooth text the default for every window and text element.</summary>
    public static void ApplyAppWide()
    {
        try { TextElement.FontFamilyProperty.OverrideMetadata(typeof(TextElement), new FrameworkPropertyMetadata(Text)); } catch (ArgumentException) { }
        try { System.Windows.Controls.TextBlock.FontFamilyProperty.OverrideMetadata(typeof(System.Windows.Controls.TextBlock), new FrameworkPropertyMetadata(Text)); } catch (ArgumentException) { }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not Window window) return;
            Smooth(window);
        }));
    }

    /// <summary>macOS-style text on <paramref name="element"/> and everything inside it.</summary>
    public static void Smooth(DependencyObject element)
    {
        TextOptions.SetTextFormattingMode(element, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(element, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(element, TextHintingMode.Animated);
    }
}
