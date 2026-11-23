using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MacDock.Controls;

/// <summary>
/// Layered "liquid glass" panel drawn on top of the real DWM blur (from the backdrop window):
///   1. tint fill   - very light, slightly brighter at the top (glass catches light from above)
///   2. sheen       - soft white gloss fading down from the top edge, faint glint at the bottom
///   3. rim light   - thin bright edge, strongest on top, catching light like a glass lip
///   4. outline     - hairline dark edge so the glass still reads on bright wallpapers
///   5. shadow      - soft lift under the panel
/// </summary>
public sealed class LiquidGlassSurface : Grid
{
    private readonly Border _frost = new() { IsHitTestVisible = false };
    private ImageBrush? _frostBrush;
    private readonly Border _fill;
    private readonly Border _sheen;
    private readonly Border _rim;
    private readonly Border _outline;

    public LiquidGlassSurface()
    {
        _fill = new Border
        {
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.22,
                RenderingBias = RenderingBias.Performance,
            },
        };

        _sheen = new Border
        {
            Margin = new Thickness(1),
            IsHitTestVisible = false,
            Background = Gradient(vertical: true,
                (0.00, 0x47), (0.30, 0x14), (0.55, 0x00), (0.90, 0x00), (1.00, 0x1A)),
        };

        _rim = new Border
        {
            BorderThickness = new Thickness(1.2),
            IsHitTestVisible = false,
            BorderBrush = Gradient(vertical: true,
                (0.00, 0xF2), (0.18, 0x80), (0.50, 0x26), (0.85, 0x40), (1.00, 0x99)),
        };

        _outline = new Border
        {
            Margin = new Thickness(-0.6),
            BorderThickness = new Thickness(0.6),
            IsHitTestVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x29, 0x00, 0x00, 0x00)),
        };

        RenderOptions.SetBitmapScalingMode(_frost, BitmapScalingMode.Linear); // smooth upscale = soft blur
        Children.Add(_frost);
        Children.Add(_fill);
        Children.Add(_sheen);
        Children.Add(_rim);
        Children.Add(_outline);

        SetTint(Colors.White, 0.22);
        SetCornerRadius(22);
    }

    /// <summary>
    /// Frosted wallpaper drawn inside the glass (Windows 11, where the system blur can't follow the
    /// round corners). <paramref name="viewport"/> places the whole blurred strip in this panel's
    /// coordinates, so the frost stays fixed to the screen while the panel moves and resizes.
    /// </summary>
    public void SetFrost(ImageSource? image, Rect viewport)
    {
        if (image == null)
        {
            _frost.Background = null;
            _frostBrush = null;
            return;
        }
        if (_frostBrush == null || !ReferenceEquals(_frostBrush.ImageSource, image))
        {
            _frostBrush = new ImageBrush(image) { ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            _frost.Background = _frostBrush;
        }
        _frostBrush.Viewport = viewport;
    }

    public void SetCornerRadius(double radius)
    {
        _frost.CornerRadius = new CornerRadius(radius);
        _fill.CornerRadius = new CornerRadius(radius);
        _rim.CornerRadius = new CornerRadius(radius);
        _sheen.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
        _outline.CornerRadius = new CornerRadius(radius + 0.6);
    }

    /// <summary>Tint color and strength (0..1). Top of the glass is a bit brighter than the bottom.</summary>
    public void SetTint(Color color, double opacity)
    {
        double a = Math.Clamp(opacity, 0, 1) * 255;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)Math.Round(a), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb((byte)Math.Round(a * 0.65), color.R, color.G, color.B), 1),
            },
        };
        brush.Freeze();
        // Never fully transparent: alpha 0 pixels would let clicks fall through the dock.
        _fill.Background = a < 2
            ? new SolidColorBrush(Color.FromArgb(2, color.R, color.G, color.B))
            : brush;
    }

    private static LinearGradientBrush Gradient(bool vertical, params (double Offset, byte Alpha)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = vertical ? new Point(0, 1) : new Point(1, 0),
        };
        foreach (var (offset, alpha) in stops)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF), offset));
        brush.Freeze();
        return brush;
    }
}
