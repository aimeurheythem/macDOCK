using System.Windows;
using System.Windows.Media;

namespace MacDock.Native;

/// <summary>
/// The glass every menu and panel uses: exactly the dock's tint color and opacity (over the same blur),
/// with text that stays readable (white when the result is dark, near-black when it is light).
/// Code reads the brushes here; XAML menus use the same values as application resources
/// (GlassMenuBackground, GlassMenuForeground, GlassMenuSeparator, GlassMenuRim).
/// </summary>
internal static class GlassTheme
{
    public static Brush Background { get; private set; } = Make(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    public static Brush Foreground { get; private set; } = Brushes.White;
    public static Brush Rim { get; private set; } = Make(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
    public static Brush Hover { get; private set; } = Make(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    public static bool IsDark { get; private set; } = true;
    public static Color Tint { get; private set; } = Colors.White;
    public static double Opacity { get; private set; } = 0.25;

    /// <summary>Whether glass over a wallpaper area of brightness <paramref name="wallpaper"/> (0..1) looks dark.</summary>
    public static bool LooksDark(double wallpaper)
    {
        double tint = (0.2126 * Tint.R + 0.7152 * Tint.G + 0.0722 * Tint.B) / 255.0;
        return wallpaper * (1 - Opacity) + tint * Opacity < 0.58;
    }

    public static void Apply(Color tint, double opacity, bool dark)
    {
        // Never fully transparent: clicks on a transparent pixel would fall through the menu.
        byte alpha = (byte)Math.Clamp(Math.Round(opacity * 255), 6, 255);
        IsDark = dark;
        Tint = tint;
        Opacity = opacity;
        Background = Make(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
        Foreground = dark ? Brushes.White : Make(Color.FromRgb(0x1D, 0x1D, 0x1F));
        Rim = Make(dark ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        Hover = Make(dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        var resources = Application.Current?.Resources;
        if (resources == null) return;
        resources["GlassMenuBackground"] = Background;
        resources["GlassMenuForeground"] = Foreground;
        resources["GlassMenuRim"] = Rim;
        resources["GlassMenuSeparator"] = Make(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
    }

    private static Brush Make(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
