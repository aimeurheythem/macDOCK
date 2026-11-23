using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacDock.Controls;

/// <summary>
/// The MacDock logo, drawn in vector: a dusk-gradient squircle (a desktop wallpaper) with a thin menu
/// bar at the top and a frosted glass dock holding three app icons at the bottom.
/// Used for the app icon (.ico, generated from it) and inside the installer.
/// </summary>
internal static class AppLogo
{
    /// <summary>Resolution-independent logo, 1 x 1 unit box scaled to <paramref name="size"/>.</summary>
    public static DrawingImage Create(double size = 256)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) Draw(dc, size);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Renders the logo to an exact-pixel bitmap (for icons).</summary>
    public static BitmapSource Render(int px)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, px);
        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Draw(DrawingContext dc, double s)
    {
        // Tiny sizes (taskbar, 16-24 px) keep only the essentials so they stay crisp.
        bool tiny = s < 28;
        double inset = s * 0.04;
        var tile = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
        double radius = tile.Width * 0.235;
        var shape = new RectangleGeometry(tile, radius, radius);

        // Wallpaper: blue to violet, with a warm glow low on the right.
        var wallpaper = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x2F, 0x7B, 0xFF), 0),
                new GradientStop(Color.FromRgb(0x6A, 0x5C, 0xF6), 0.55),
                new GradientStop(Color.FromRgb(0xC0, 0x5C, 0xE0), 1),
            },
        };
        dc.DrawGeometry(wallpaper, null, shape);
        dc.PushClip(shape);
        var glow = new RadialGradientBrush(Color.FromArgb(0x88, 0xFF, 0xB8, 0x7A), Color.FromArgb(0, 0xFF, 0xB8, 0x7A))
        {
            Center = new Point(0.8, 0.75), GradientOrigin = new Point(0.8, 0.75), RadiusX = 0.6, RadiusY = 0.6,
        };
        dc.DrawRectangle(glow, null, tile);
        var sheen = new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90);
        dc.DrawRectangle(sheen, null, new Rect(tile.X, tile.Y, tile.Width, tile.Height * 0.45));

        // Menu bar.
        if (!tiny)
        {
            var bar = new Rect(tile.X, tile.Y, tile.Width, tile.Height * 0.115);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), null, bar);
            double dot = bar.Height * 0.32;
            dc.DrawEllipse(Brushes.White, null, new Point(bar.X + radius * 0.9, bar.Y + bar.Height / 2), dot, dot);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), null,
                new Rect(bar.Right - radius * 0.9 - bar.Height * 1.6, bar.Y + bar.Height * 0.36, bar.Height * 1.6, bar.Height * 0.28), 1, 1);
        }
        dc.Pop();

        // Frosted dock.
        double dockH = tile.Height * (tiny ? 0.30 : 0.235);
        var dock = new Rect(tile.X + tile.Width * 0.13, tile.Bottom - tile.Height * 0.08 - dockH, tile.Width * 0.74, dockH);
        double dockR = dockH * 0.36;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF)),
            new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Math.Max(0.6, s * 0.006)), dock, dockR, dockR);

        // Three app icons in the dock (the middle one magnified, like a hover).
        var colors = new[]
        {
            (Color.FromRgb(0x34, 0xD3, 0x59), Color.FromRgb(0x1E, 0xA4, 0x46)),
            (Color.FromRgb(0x4D, 0xB2, 0xFF), Color.FromRgb(0x0A, 0x6C, 0xFF)),
            (Color.FromRgb(0xFF, 0xB3, 0x40), Color.FromRgb(0xFF, 0x7A, 0x1A)),
        };
        double icon = dockH * 0.62, gap = dock.Width * 0.08;
        double total = icon * 3 + gap * 2 + icon * 0.35;
        double x = dock.X + (dock.Width - total) / 2;
        for (int i = 0; i < 3; i++)
        {
            double size = i == 1 ? icon * 1.35 : icon;
            double y = dock.Bottom - dockH * 0.19 - size;
            var r = new Rect(x, y, size, size);
            var fill = new LinearGradientBrush(colors[i].Item1, colors[i].Item2, 90);
            dc.DrawRoundedRectangle(fill, null, r, size * 0.24, size * 0.24);
            x += size + gap;
        }

        // Fine edge so the tile reads on light and dark backgrounds.
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x20)), Math.Max(0.5, s * 0.004)), shape);
    }
}
