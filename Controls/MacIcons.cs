using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MacDock.Services;

/// <summary>
/// macOS-style app icons drawn as vectors (redrawn in the look of the current macOS icons, not Apple's
/// image files): Terminal, System Settings, Activity Monitor, App Store and Photos. Each draws into an
/// s x s square: a rounded "squircle" tile with a soft top light, like the macOS 26/27 Liquid Glass icons.
/// </summary>
internal static class MacIcons
{
    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush Solid(string hex) => new(Hex(hex));
    private static LinearGradientBrush Vertical(string top, string bottom) => new(Hex(top), Hex(bottom), 90);

    /// <summary>The macOS squircle (a superellipse, slightly fuller than a rounded rectangle).</summary>
    public static Geometry Squircle(double s, double inset = 0)
    {
        double x0 = inset, y0 = inset, w = s - 2 * inset;
        const int n = 96;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (int i = 0; i <= n; i++)
            {
                double t = 2 * Math.PI * i / n;
                double ct = Math.Cos(t), st = Math.Sin(t);
                // |x|^5 + |y|^5 = 1
                double x = Math.Sign(ct) * Math.Pow(Math.Abs(ct), 2.0 / 5);
                double y = Math.Sign(st) * Math.Pow(Math.Abs(st), 2.0 / 5);
                var p = new Point(x0 + w / 2 + x * w / 2, y0 + w / 2 + y * w / 2);
                if (i == 0) c.BeginFigure(p, true, true);
                else c.LineTo(p, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    private static void Base(DrawingContext dc, double s, Brush fill)
    {
        var shape = Squircle(s);
        dc.DrawGeometry(fill, null, shape);
    }

    /// <summary>Liquid Glass finish: a light top edge and a faint inner rim.</summary>
    private static void Finish(DrawingContext dc, double s, double strength = 1)
    {
        var shape = Squircle(s);
        dc.PushClip(shape);
        var light = new LinearGradientBrush(Color.FromArgb((byte)(0x40 * strength), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        dc.DrawRectangle(light, null, new Rect(0, 0, s, s * 0.45));
        dc.Pop();
        var rim = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(0x90 * strength), 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0x10, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb((byte)(0x50 * strength), 0, 0, 0), 1),
            },
        };
        dc.DrawGeometry(null, new Pen(rim, Math.Max(1, s * 0.008)), Squircle(s, s * 0.004));
    }

    // ---------------- Terminal ----------------

    /// <summary>Black screen in a graphite frame, a white "&gt;_" at the top left.</summary>
    public static void Terminal(DrawingContext dc, double s)
    {
        Base(dc, s, Vertical("#5B5B60", "#2A2A2D"));
        var screen = new Rect(s * 0.1, s * 0.1, s * 0.8, s * 0.8);
        double r = s * 0.12;
        dc.DrawRoundedRectangle(Vertical("#1B1B1D", "#0A0A0B"), new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), s * 0.006), screen, r, r);
        // Faint reflection across the glass.
        dc.PushClip(new RectangleGeometry(screen, r, r));
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0x22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90), null,
            new Rect(screen.X, screen.Y, screen.Width, screen.Height * 0.35));
        dc.Pop();

        var face = new Typeface(new FontFamily("SF Mono, Menlo, Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(">_", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, s * 0.24, Brushes.White, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var b = geometry.Bounds;
        dc.PushTransform(new TranslateTransform(screen.X + s * 0.09 - b.Left, screen.Y + s * 0.1 - b.Top));
        dc.DrawGeometry(Brushes.White, null, geometry);
        dc.Pop();
        Finish(dc, s, 0.7);
    }

    // ---------------- System Settings ----------------

    /// <summary>Silver tile with the layered gray gear (outer toothed ring, inner gear, spoked hub).</summary>
    public static void SystemSettings(DrawingContext dc, double s)
    {
        Base(dc, s, Vertical("#F2F2F5", "#C3C3C9"));
        var c = new Point(s / 2, s / 2);

        // Outer gear: dark ring with square teeth.
        dc.DrawGeometry(Vertical("#6E6E74", "#3C3C40"), null, Gear(c, s * 0.40, s * 0.345, 30, 0.5));
        dc.DrawEllipse(Vertical("#D9D9DE", "#9D9DA3"), null, c, s * 0.315, s * 0.315); // silver ring
        dc.DrawEllipse(Vertical("#4A4A4F", "#2C2C30"), null, c, s * 0.27, s * 0.27);   // dark well

        // Inner gear.
        dc.DrawGeometry(Vertical("#B9B9BF", "#7D7D84"), null, Gear(c, s * 0.235, s * 0.195, 14, 0.55));
        dc.DrawEllipse(Vertical("#5A5A60", "#323236"), null, c, s * 0.165, s * 0.165);

        // Hub with three spokes.
        var spoke = new Pen(Vertical("#D6D6DB", "#9A9AA0"), s * 0.05) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (int i = 0; i < 3; i++)
        {
            double a = -Math.PI / 2 + i * 2 * Math.PI / 3;
            dc.DrawLine(spoke, c, new Point(c.X + Math.Cos(a) * s * 0.14, c.Y + Math.Sin(a) * s * 0.14));
        }
        dc.DrawEllipse(Vertical("#E4E4E8", "#A5A5AB"), null, c, s * 0.055, s * 0.055);
        dc.DrawEllipse(Solid("#505055"), null, c, s * 0.022, s * 0.022);
        Finish(dc, s);
    }

    /// <summary>A gear outline: <paramref name="teeth"/> square teeth between radius r1 (tips) and r2 (roots).</summary>
    private static Geometry Gear(Point c, double r1, double r2, int teeth, double duty)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double step = 2 * Math.PI / teeth;
            bool first = true;
            for (int i = 0; i < teeth; i++)
            {
                double a0 = i * step, a1 = a0 + step * (1 - duty) / 2, a2 = a0 + step * (1 + duty) / 2, a3 = a0 + step;
                Point P(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
                if (first) { ctx.BeginFigure(P(r2, a0), true, true); first = false; }
                ctx.LineTo(P(r2, a1), true, true);
                ctx.LineTo(P(r1, a1 + step * 0.04), true, true);
                ctx.LineTo(P(r1, a2 - step * 0.04), true, true);
                ctx.LineTo(P(r2, a2), true, true);
                ctx.LineTo(P(r2, a3), true, true);
            }
        }
        g.Freeze();
        return g;
    }

    // ---------------- Activity Monitor ----------------

    /// <summary>A black screen with a green grid and a pulse line, in a silver frame.</summary>
    public static void ActivityMonitor(DrawingContext dc, double s)
    {
        Base(dc, s, Vertical("#ECECEF", "#B5B5BB"));
        var screen = new Rect(s * 0.12, s * 0.14, s * 0.76, s * 0.72);
        double r = s * 0.08;
        dc.DrawRoundedRectangle(Vertical("#1A1A1C", "#050506"), null, screen, r, r);
        dc.PushClip(new RectangleGeometry(screen, r, r));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x30, 0xD1, 0x58)), Math.Max(1, s * 0.006));
        for (int i = 1; i < 5; i++) dc.DrawLine(grid, new Point(screen.X + screen.Width * i / 5, screen.Y), new Point(screen.X + screen.Width * i / 5, screen.Bottom));
        for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(screen.X, screen.Y + screen.Height * i / 4), new Point(screen.Right, screen.Y + screen.Height * i / 4));
        var pulse = new StreamGeometry();
        using (var g = pulse.Open())
        {
            var pts = new[] { (0.0, 0.62), (0.22, 0.62), (0.32, 0.36), (0.44, 0.8), (0.55, 0.22), (0.66, 0.66), (0.76, 0.52), (1.0, 0.52) };
            g.BeginFigure(new Point(screen.X + screen.Width * pts[0].Item1, screen.Y + screen.Height * pts[0].Item2), false, false);
            foreach (var (x, y) in pts.Skip(1)) g.LineTo(new Point(screen.X + screen.Width * x, screen.Y + screen.Height * y), true, true);
        }
        dc.DrawGeometry(null, new Pen(Solid("#35E06A"), s * 0.035) { LineJoin = PenLineJoin.Round }, pulse);
        dc.Pop();
        Finish(dc, s);
    }

    // ---------------- App Store ----------------

    /// <summary>Blue gradient tile with the white "A" made of three sticks.</summary>
    public static void AppStore(DrawingContext dc, double s)
    {
        Base(dc, s, Vertical("#1BC5FB", "#1561F0"));
        var pen = new Pen(Brushes.White, s * 0.075) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(pen, new Point(s * 0.56, s * 0.22), new Point(s * 0.30, s * 0.70)); // left leg
        dc.DrawLine(pen, new Point(s * 0.44, s * 0.22), new Point(s * 0.72, s * 0.72)); // right leg
        dc.DrawLine(pen, new Point(s * 0.24, s * 0.58), new Point(s * 0.76, s * 0.58)); // bar
        dc.DrawLine(pen, new Point(s * 0.30, s * 0.70), new Point(s * 0.26, s * 0.77)); // foot
        Finish(dc, s);
    }

    // ---------------- Photos ----------------

    /// <summary>White tile with the eight-petal color flower.</summary>
    public static void Photos(DrawingContext dc, double s)
    {
        Base(dc, s, Vertical("#FFFFFF", "#F0F0F3"));
        var c = new Point(s / 2, s / 2);
        string[] colors = { "#F9B233", "#F28C28", "#E8483F", "#C2449B", "#7C5CC4", "#3E8EDE", "#3CB4A6", "#93C73F" };
        for (int i = 0; i < 8; i++)
        {
            double a = i * 45;
            var petal = new EllipseGeometry(new Point(c.X, c.Y - s * 0.17), s * 0.105, s * 0.17);
            var color = Hex(colors[i]);
            dc.PushTransform(new RotateTransform(a, c.X, c.Y));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xCC, color.R, color.G, color.B)), null, petal);
            dc.Pop();
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x20, 0, 0, 0)), Math.Max(1, s * 0.006)), Squircle(s, s * 0.004));
        Finish(dc, s, 0.5);
    }
}
