using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacDock.Controls;

/// <summary>
/// The dock's "Finder" icon, in the style of the macOS Finder: a squircle split by a face profile,
/// blue on the left and pale on the right, with two eyes and a smile across both halves.
/// Vector, so it is sharp at any size and while magnified.
/// </summary>
internal static class FinderIcon
{
    public static DrawingImage Create()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) Draw(dc, 256);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Exact-pixel bitmap of the icon (window icons).</summary>
    public static BitmapSource Render(int px)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / 256.0, px / 256.0));
            Draw(dc, 256);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Draw(DrawingContext dc, double s)
    {
        const double inset = 10;
        double size = s - 2 * inset, radius = size * 0.225;
        var tile = new Rect(inset, inset, size, size);
        var shape = new RectangleGeometry(tile, radius, radius);

        // Transparent full-size frame so the icon keeps its padding like other dock icons.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, s, s));

        dc.PushClip(shape);

        // Right half: pale, almost white blue.
        dc.DrawRectangle(Gradient(Color.FromRgb(0xF4, 0xF9, 0xFE), Color.FromRgb(0xC9, 0xE3, 0xF8)), null, tile);

        // Left half: the blue face. Its right edge is the profile of the face on the right (nose to the left).
        var left = new StreamGeometry();
        using (var g = left.Open())
        {
            g.BeginFigure(new Point(0, 0), true, true);
            g.LineTo(new Point(150, 0), true, true);
            g.BezierTo(new Point(146, 40), new Point(138, 78), new Point(130, 104), true, true);  // forehead
            g.BezierTo(new Point(122, 126), new Point(104, 144), new Point(106, 152), true, true); // nose tip
            g.BezierTo(new Point(108, 160), new Point(124, 160), new Point(132, 162), true, true); // under the nose
            g.BezierTo(new Point(130, 196), new Point(126, 226), new Point(128, s), true, true);   // chin
            g.LineTo(new Point(0, s), true, true);
        }
        left.Freeze();
        dc.DrawGeometry(Gradient(Color.FromRgb(0x5C, 0xC8, 0xFA), Color.FromRgb(0x14, 0x7C, 0xE5)), null, left);

        // Soft top sheen over both halves.
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90), null,
            new Rect(tile.X, tile.Y, tile.Width, tile.Height * 0.4));
        dc.Pop();

        // Eyes: two rounded vertical bars, one on each face.
        var ink = new SolidColorBrush(Color.FromRgb(0x17, 0x26, 0x38));
        ink.Freeze();
        dc.DrawRoundedRectangle(ink, null, new Rect(76, 74, 16, 44), 8, 8);
        dc.DrawRoundedRectangle(ink, null, new Rect(170, 74, 16, 44), 8, 8);

        // The smile runs across both faces.
        var smile = new StreamGeometry();
        using (var g = smile.Open())
        {
            g.BeginFigure(new Point(58, 172), false, false);
            g.BezierTo(new Point(96, 206), new Point(160, 210), new Point(200, 170), true, true);
        }
        smile.Freeze();
        dc.DrawGeometry(null, new Pen(ink, 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, smile);

        // Fine edge so the tile reads on light and dark backgrounds.
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x10, 0x30)), 1.5), shape);
    }

    private static LinearGradientBrush Gradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }
}
