using System.Windows;
using System.Windows.Media;

namespace MacDock.Controls;

/// <summary>Vector icon for the dock's Start button: four rounded squares with a blue gradient.</summary>
internal static class StartButtonIcon
{
    public static DrawingImage Create()
    {
        const double frame = 120, inset = 14, gap = 6;
        double cell = (frame - 2 * inset - gap) / 2;

        var gradient = new LinearGradientBrush(
            Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x00, 0x67, 0xC0),
            new Point(inset, inset), new Point(frame - inset, frame - inset))
        { MappingMode = BrushMappingMode.Absolute };

        var squares = new GeometryGroup();
        foreach (var (x, y) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) })
        {
            var rect = new Rect(inset + x * (cell + gap), inset + y * (cell + gap), cell, cell);
            squares.Children.Add(new RectangleGeometry(rect, 4, 4));
        }

        var group = new DrawingGroup();
        // Transparent frame keeps the logo padded like a normal app icon.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, frame, frame))));
        group.Children.Add(new GeometryDrawing(gradient, null, squares));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
