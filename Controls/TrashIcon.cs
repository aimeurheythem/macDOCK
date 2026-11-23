using System.Windows;
using System.Windows.Media;

namespace MacDock.Controls;

/// <summary>
/// Original trash can drawn as vectors to match the glass dock: a frosted, slightly translucent
/// bin with soft vertical ribs and a bright lip. The "full" variant shows crumpled paper.
/// </summary>
internal static class TrashIcon
{
    private static DrawingImage? _empty, _full;

    public static DrawingImage Empty => _empty ??= Create(full: false);
    public static DrawingImage Full => _full ??= Create(full: true);

    private static DrawingImage Create(bool full)
    {
        var group = new DrawingGroup();

        // Transparent 120x120 frame keeps the same padding as normal app icons.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 120, 120))));

        // Soft contact shadow.
        group.Children.Add(new GeometryDrawing(
            new RadialGradientBrush(Color.FromArgb(0x40, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)),
            null, new EllipseGeometry(new Point(60, 107), 30, 4)));

        if (full) AddPaper(group);

        // Body: tapered, rounded bottom, frosted glass.
        var body = Geometry.Parse("M 28,38 L 92,38 L 86.5,99 Q 85.8,106 78.5,106 L 41.5,106 Q 34.2,106 33.5,99 Z");
        var glass = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xF0, 0xE4, 0xEA, 0xF2), 0.00),
                new GradientStop(Color.FromArgb(0xD9, 0xFA, 0xFC, 0xFF), 0.30),
                new GradientStop(Color.FromArgb(0xC7, 0xDC, 0xE3, 0xEC), 0.70),
                new GradientStop(Color.FromArgb(0xEB, 0xC9, 0xD2, 0xDE), 1.00),
            },
        };
        var bodyStroke = new Pen(new SolidColorBrush(Color.FromArgb(0x59, 0x5A, 0x67, 0x78)), 1.2);
        group.Children.Add(new GeometryDrawing(glass, bodyStroke, body));

        // Vertical shading: light from above, a bit darker towards the bottom.
        var shade = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45),
                new GradientStop(Color.FromArgb(0x1F, 0x30, 0x40, 0x55), 1.0),
            },
        };
        group.Children.Add(new GeometryDrawing(shade, null, body));

        // Ribs, following the taper.
        var ribPen = new Pen(new SolidColorBrush(Color.FromArgb(0x4D, 0x6B, 0x7A, 0x8E)), 3.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        var ribLight = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), 1.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        foreach (var (top, bottom) in new[] { (45.0, 48.5), (60.0, 60.0), (75.0, 71.5) })
        {
            group.Children.Add(new GeometryDrawing(null, ribPen, new LineGeometry(new Point(top, 50), new Point(bottom, 96))));
            group.Children.Add(new GeometryDrawing(null, ribLight, new LineGeometry(new Point(top + 2.2, 51), new Point(bottom + 2.2, 95))));
        }

        // Specular streak on the left side of the glass.
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)), null,
            Geometry.Parse("M 33,44 L 37,44 L 39.5,96 Q 38,98 36.5,96 Z")));

        // Lip: bright rounded rim.
        var lip = new RectangleGeometry(new Rect(24, 32, 72, 10), 5, 5);
        var lipFill = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(Color.FromRgb(0xE9, 0xEE, 0xF4), 0.55),
                new GradientStop(Color.FromRgb(0xC3, 0xCC, 0xD8), 1.0),
            },
        };
        group.Children.Add(new GeometryDrawing(lipFill, new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x5A, 0x67, 0x78)), 1), lip));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Crumpled paper sticking out of the bin.</summary>
    private static void AddPaper(DrawingGroup group)
    {
        var paperFill = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE6, 0xE9, 0xEE), 90);
        var paperPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x8A, 0x94, 0xA3)), 1) { LineJoin = PenLineJoin.Round };
        var crease = new Pen(new SolidColorBrush(Color.FromArgb(0x59, 0x8A, 0x94, 0xA3)), 0.8);

        // Back sheet (tilted page).
        group.Children.Add(new GeometryDrawing(paperFill, paperPen,
            Geometry.Parse("M 62,36 L 66,12 L 88,16 L 84,38 Z")));
        group.Children.Add(new GeometryDrawing(null, crease, Geometry.Parse("M 68,18 L 84,21 M 67,24 L 82,27")));

        // Crumpled balls.
        group.Children.Add(new GeometryDrawing(paperFill, paperPen,
            Geometry.Parse("M 34,36 Q 31,26 39,21 L 46,24 Q 52,18 58,25 Q 62,31 58,36 Z")));
        group.Children.Add(new GeometryDrawing(null, crease, Geometry.Parse("M 40,25 L 45,31 L 52,26 M 46,31 L 47,36")));

        group.Children.Add(new GeometryDrawing(paperFill, paperPen,
            Geometry.Parse("M 52,37 Q 50,28 57,26 L 63,29 Q 70,27 72,33 L 71,37 Z")));
        group.Children.Add(new GeometryDrawing(null, crease, Geometry.Parse("M 57,30 L 62,34 L 67,30")));
    }
}
