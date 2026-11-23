using System.Windows;
using System.Windows.Media;

namespace MacDock.Controls;

internal enum MenuBarGlyph { Logo, Wifi, Ethernet, Offline, Speaker, Battery, ControlCenter, Search, Bluetooth, Display, Chevron }

/// <summary>
/// Vector status icons for the menu bar, drawn macOS style (monochrome, thin, rounded), so they are
/// crisp at any DPI. <see cref="Level"/> is 0..1: Wi-Fi signal, volume, battery charge.
/// </summary>
internal sealed class MenuBarIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(MenuBarGlyph), typeof(MenuBarIcon),
        new FrameworkPropertyMetadata(MenuBarGlyph.Logo, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(MenuBarIcon),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedProperty = DependencyProperty.Register(
        nameof(Muted), typeof(bool), typeof(MenuBarIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(MenuBarIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush LogoBlue = CreateLogoBlue();

    private static Brush CreateLogoBlue()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(0x1C, 0x9B, 0xF0), Color.FromRgb(0x00, 0x67, 0xC0), 90);
        brush.Freeze();
        return brush;
    }

    public MenuBarGlyph Glyph { get => (MenuBarGlyph)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Muted { get => (bool)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size available) => Glyph switch
    {
        MenuBarGlyph.Battery => new Size(25, 16),
        MenuBarGlyph.Speaker => new Size(19, 16),
        MenuBarGlyph.Ethernet => new Size(20, 16),
        _ => new Size(16, 16),
    };

    protected override void OnRender(DrawingContext dc)
    {
        var fg = Foreground;
        var faint = fg.Clone();
        faint.Opacity = 0.28;
        faint.Freeze();
        double cy = RenderSize.Height / 2;

        switch (Glyph)
        {
            case MenuBarGlyph.Logo:
            {
                // Four-pane window in Windows blue, slightly rounded.
                const double s = 6.2, gap = 1.3, x0 = 1.5;
                double y0 = cy - s - gap / 2;
                for (int r = 0; r < 2; r++)
                    for (int c = 0; c < 2; c++)
                        dc.DrawRoundedRectangle(LogoBlue, null, new Rect(x0 + c * (s + gap), y0 + r * (s + gap), s, s), 1.1, 1.1);
                break;
            }
            case MenuBarGlyph.Wifi:
            case MenuBarGlyph.Offline:
            {
                // Three arcs + dot, like a fan opening upwards.
                var center = new Point(8, cy + 5.6);
                int lit = Glyph == MenuBarGlyph.Offline ? 0 : Level >= 0.67 ? 3 : Level >= 0.34 ? 2 : 1;
                dc.DrawEllipse(Glyph == MenuBarGlyph.Offline ? faint : fg, null, center, 1.55, 1.55);
                for (int i = 1; i <= 3; i++)
                {
                    double r = 1.6 + i * 3.2;
                    var pen = new Pen(i <= lit ? fg : faint, 1.75) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawGeometry(null, pen, Arc(center, r, -135, -45));
                }
                if (Glyph == MenuBarGlyph.Offline)
                    dc.DrawLine(new Pen(fg, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                        new Point(2.5, cy - 5.5), new Point(13.5, cy + 6));
                break;
            }
            case MenuBarGlyph.Ethernet:
            {
                // A desktop PC (monitor + stand), like the wired-network icon on the Windows taskbar.
                var pen = new Pen(fg, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawRoundedRectangle(null, pen, new Rect(2.2, cy - 6.3, 15.6, 10.2), 1.8, 1.8);
                dc.DrawLine(pen, new Point(10, cy + 3.9), new Point(10, cy + 6.2));
                dc.DrawLine(pen, new Point(6.6, cy + 6.6), new Point(13.4, cy + 6.6));
                break;
            }
            case MenuBarGlyph.Speaker:
            {
                var body = new StreamGeometry();
                using (var g = body.Open())
                {
                    g.BeginFigure(new Point(1, cy - 2.6), true, true);
                    g.LineTo(new Point(3.6, cy - 2.6), true, true);
                    g.LineTo(new Point(7.6, cy - 6.2), true, true);
                    g.LineTo(new Point(7.6, cy + 6.2), true, true);
                    g.LineTo(new Point(3.6, cy + 2.6), true, true);
                    g.LineTo(new Point(1, cy + 2.6), true, true);
                }
                body.Freeze();
                dc.DrawGeometry(fg, new Pen(fg, 0.8) { LineJoin = PenLineJoin.Round }, body);

                var center = new Point(7.2, cy);
                if (Muted)
                {
                    var x = new Pen(fg, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawLine(x, new Point(11, cy - 3), new Point(17, cy + 3));
                    dc.DrawLine(x, new Point(17, cy - 3), new Point(11, cy + 3));
                    break;
                }
                int waves = Level <= 0.001 ? 0 : Level < 0.34 ? 1 : Level < 0.67 ? 2 : 3;
                for (int i = 1; i <= 3; i++)
                {
                    var pen = new Pen(i <= waves ? fg : faint, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawGeometry(null, pen, Arc(center, 1.4 + i * 3.0, -48, 48));
                }
                break;
            }
            case MenuBarGlyph.Battery:
            {
                var outline = new Rect(0.75, cy - 5.25, 21, 10.5);
                dc.DrawRoundedRectangle(null, new Pen(faint, 1.2), outline, 3, 3);
                dc.DrawRoundedRectangle(faint, null, new Rect(22.6, cy - 2, 1.6, 4), 0.8, 0.8);
                double fill = Math.Clamp(Level, 0, 1) * (outline.Width - 3.4);
                if (fill > 0.6)
                {
                    var color = Level <= 0.2 && !Muted ? new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)) : fg;
                    dc.DrawRoundedRectangle(color, null, new Rect(outline.X + 1.7, outline.Y + 1.7, fill, outline.Height - 3.4), 1.6, 1.6);
                }
                if (Muted) // used as "charging": small bolt
                {
                    var bolt = new StreamGeometry();
                    using (var g = bolt.Open())
                    {
                        g.BeginFigure(new Point(12.2, cy - 6.5), true, true);
                        g.LineTo(new Point(8.2, cy + 0.8), true, true);
                        g.LineTo(new Point(11.2, cy + 0.8), true, true);
                        g.LineTo(new Point(10.2, cy + 6.5), true, true);
                        g.LineTo(new Point(14.4, cy - 0.9), true, true);
                        g.LineTo(new Point(11.4, cy - 0.9), true, true);
                    }
                    bolt.Freeze();
                    dc.DrawGeometry(Brushes.White, new Pen(fg, 0.9) { LineJoin = PenLineJoin.Round }, bolt);
                }
                break;
            }
            case MenuBarGlyph.Chevron:
            {
                var pen = new Pen(fg, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, Polyline(new Point(3.5, cy - 2), new Point(8, cy + 2.5), new Point(12.5, cy - 2)));
                break;
            }
            case MenuBarGlyph.Search:
            {
                var pen = new Pen(fg, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawEllipse(null, pen, new Point(6.8, cy - 1.4), 4.6, 4.6);
                dc.DrawLine(pen, new Point(10.2, cy + 2), new Point(14.2, cy + 6));
                break;
            }
            case MenuBarGlyph.Bluetooth:
            {
                var pen = new Pen(fg, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, Polyline(
                    new Point(4, cy - 3.6), new Point(11.5, cy + 3.4), new Point(8, cy + 6.8), new Point(8, cy - 6.8),
                    new Point(11.5, cy - 3.4), new Point(4, cy + 3.6)));
                break;
            }
            case MenuBarGlyph.Display:
            {
                var pen = new Pen(fg, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawEllipse(fg, null, new Point(8, cy), 3.2, 3.2);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    dc.DrawLine(pen, new Point(8 + Math.Cos(a) * 5.2, cy + Math.Sin(a) * 5.2), new Point(8 + Math.Cos(a) * 6.9, cy + Math.Sin(a) * 6.9));
                }
                break;
            }
            case MenuBarGlyph.ControlCenter:
            {
                // Two capsule toggles: knob left on top, knob right below.
                var pen = new Pen(fg, 1.3);
                var top = new Rect(1, cy - 6.2, 14, 5.6);
                var bottom = new Rect(1, cy + 0.6, 14, 5.6);
                dc.DrawRoundedRectangle(null, pen, top, 2.8, 2.8);
                dc.DrawRoundedRectangle(null, pen, bottom, 2.8, 2.8);
                dc.DrawEllipse(fg, null, new Point(top.Left + 2.8, top.Top + 2.8), 2.1, 2.1);
                dc.DrawEllipse(fg, null, new Point(bottom.Right - 2.8, bottom.Top + 2.8), 2.1, 2.1);
                break;
            }
        }
    }

    private static Geometry Arc(Point center, double radius, double fromDeg, double toDeg)
    {
        Point At(double deg) => new(center.X + radius * Math.Cos(deg * Math.PI / 180), center.Y + radius * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(fromDeg), false, false);
            ctx.ArcTo(At(toDeg), new Size(radius, radius), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    private static Geometry Polyline(params Point[] points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            foreach (var p in points.Skip(1)) ctx.LineTo(p, true, true);
        }
        g.Freeze();
        return g;
    }
}
