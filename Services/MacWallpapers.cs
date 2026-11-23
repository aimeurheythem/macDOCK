using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacDock.Services;

/// <summary>
/// MacDock's own 4K wallpapers in the style of macOS (flowing ribbons, rolling hills, layered waves,
/// soft glow), each in a light and a dark version. They are drawn by MacDock itself (Apple's wallpapers
/// can't be shipped with it), saved as JPEG in %LOCALAPPDATA%\MacDock\Wallpapers, and, like macOS's
/// dynamic wallpapers, switch between light and dark with the system theme.
/// </summary>
internal static class MacWallpapers
{
    public sealed record Design(string Id, string Name);

    public static readonly Design[] Designs =
    {
        new("ribbons", "Ribbons"), new("hills", "Hills"), new("waves", "Waves"), new("glow", "Glow"),
    };

    private const int Width = 3840, Height = 2160;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDock", "Wallpapers");

    public static string PathFor(string id, bool dark) => Path.Combine(Folder, $"{id}-{(dark ? "dark" : "light")}.jpg");

    /// <summary>A small copy (480 px wide) for System Settings, so it never decodes a 4K picture to show a tile.</summary>
    public static string ThumbFor(string id, bool dark) => Path.Combine(Folder, "thumbs", $"{id}-{(dark ? "dark" : "light")}.jpg");

    private static bool Extract(string id, bool dark, string file)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Wallpapers/{id}-{(dark ? "dark" : "light")}.jpg"));
            if (info == null) return false;
            using (var source = info.Stream)
            using (var target = File.Create(file + ".tmp"))
                source.CopyTo(target);
            File.Move(file + ".tmp", file, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacWallpapers.Extract", ex);
            return false;
        }
    }

    private static void MakeThumb(string file, string thumb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = 480;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(thumb);
            encoder.Save(stream);
        }
        catch (Exception ex) { CrashLog.Write("MacWallpapers.Thumb", ex); }
    }

    private static Task? _making;
    private static readonly object Gate = new();

    /// <summary>Draws the wallpapers that don't exist yet (a few seconds, on a background thread).</summary>
    public static Task EnsureAsync()
    {
        lock (Gate)
        {
            if (Designs.All(d => File.Exists(PathFor(d.Id, true)) && File.Exists(PathFor(d.Id, false)) && File.Exists(ThumbFor(d.Id, true)) && File.Exists(ThumbFor(d.Id, false)))) return Task.CompletedTask;
            if (_making is { IsCompleted: false }) return _making;
            var done = new TaskCompletionSource();
            var thread = new Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    foreach (var design in Designs)
                        foreach (bool dark in new[] { true, false })
                        {
                            string file = PathFor(design.Id, dark);
                            // Shipped inside MacDock (copied out in an instant); drawn only if that ever fails.
                            if (!File.Exists(file) && !Extract(design.Id, dark, file)) Render(design.Id, dark, file);
                            string thumb = ThumbFor(design.Id, dark);
                            if (!File.Exists(thumb)) MakeThumb(file, thumb);
                        }
                }
                catch (Exception ex) { CrashLog.Write("MacWallpapers.Ensure", ex); }
                finally { done.TrySetResult(); }
            }) { IsBackground = true, Name = "MacDock wallpapers", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return _making = done.Task;
        }
    }

    /// <summary>Makes <paramref name="id"/> the desktop picture (its light or dark version, by the current theme).</summary>
    public static async Task ApplyAsync(string id)
    {
        // The one picture needed is copied out first and shown right away; the rest follow in the background.
        bool dark = WindowsSettings.IsDark;
        string file = PathFor(id, dark);
        if (!File.Exists(file)) await Task.Run(() => { Directory.CreateDirectory(Folder); Extract(id, dark, file); });
        if (!File.Exists(file)) await EnsureAsync();
        if (File.Exists(file)) await WindowsSettings.SetWallpaperSmoothAsync(file, 0 /* fill */);
        _ = EnsureAsync();
    }

    /// <summary>The design of the current wallpaper when it is one of these, else null.</summary>
    public static string? CurrentDesign()
    {
        string? current = WindowsSettings.Wallpaper;
        if (current == null || !current.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) return null;
        string name = Path.GetFileNameWithoutExtension(current);
        int dash = name.LastIndexOf('-');
        return dash > 0 ? name[..dash] : null;
    }

    private static bool _following;

    /// <summary>From now on, one of these wallpapers swaps to its light / dark version with the system theme.</summary>
    public static void FollowTheme()
    {
        if (_following) return;
        _following = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (CurrentDesign() is not { } id) return;
            string want = PathFor(id, WindowsSettings.IsDark);
            if (!string.Equals(WindowsSettings.Wallpaper, want, StringComparison.OrdinalIgnoreCase) && File.Exists(want))
                _ = WindowsSettings.SetWallpaperSmoothAsync(want, 0);
        };
    }

    // ---------------- Drawing ----------------

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    private static Color Rgb(uint rgb, byte alpha = 255) => Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static void Render(string id, bool dark, string file)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            switch (id)
            {
                case "ribbons": Ribbons(dc, dark); break;
                case "hills": Hills(dc, dark); break;
                case "waves": Waves(dc, dark); break;
                default: Glow(dc, dark); break;
            }
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        // A whisper of noise, so the smooth gradients don't band into visible steps.
        int stride = Width * 4;
        var pixels = new byte[stride * Height];
        bitmap.CopyPixels(pixels, stride, 0);
        uint seed = 2463534242;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            int n = (int)(seed % 5) - 2;
            pixels[i] = (byte)Math.Clamp(pixels[i] + n, 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] + n, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] + n, 0, 255);
            pixels[i + 3] = 255;
        }
        var final = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 94 };
        encoder.Frames.Add(BitmapFrame.Create(final));
        string temp = file + ".tmp";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, file, overwrite: true);
    }

    private static Brush Linear(double angle, params (uint Color, double At)[] stops)
    {
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        double r = angle * Math.PI / 180;
        brush.StartPoint = new Point(0.5 - Math.Cos(r) / 2, 0.5 - Math.Sin(r) / 2);
        brush.EndPoint = new Point(0.5 + Math.Cos(r) / 2, 0.5 + Math.Sin(r) / 2);
        foreach (var (color, at) in stops) brush.GradientStops.Add(new GradientStop(C(color), at));
        brush.Freeze();
        return brush;
    }

    private static Brush Radial(uint inner, uint outer)
    {
        var brush = new RadialGradientBrush(C(inner), C(outer));
        brush.Freeze();
        return brush;
    }

    private static void Background(DrawingContext dc, Brush brush) => dc.DrawRectangle(brush, null, new Rect(0, 0, Width, Height));

    private static void Blob(DrawingContext dc, double x, double y, double rx, double ry, uint color)
    {
        uint clear = color & 0x00FFFFFF;
        dc.DrawEllipse(Radial(color, clear), null, new Point(x * Width, y * Height), rx * Width, ry * Width);
    }

    /// <summary>A curved band from left to right: the area between a Bezier curve and the same curve moved down.</summary>
    private static Geometry Band(Point p0, Point p1, Point p2, Point p3, double thickness)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            var d = new Vector(0, thickness);
            c.BeginFigure(p0, true, true);
            c.BezierTo(p1, p2, p3, true, true);
            c.LineTo(p3 + d, true, true);
            c.BezierTo(p2 + d, p1 + d, p0 + d, true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The area under a smooth curve through <paramref name="ys"/> (fractions of the height), down to the bottom.</summary>
    private static Geometry Under(double[] ys, double lift = 0)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            int n = ys.Length - 1;
            double step = (Width + 400.0) / n;
            Point P(int i) => new(-200 + i * step, ys[i] * Height - lift);
            c.BeginFigure(new Point(-200, Height + 10), true, true);
            c.LineTo(P(0), true, true);
            for (int i = 0; i < n; i++)
            {
                var a = P(i);
                var b = P(i + 1);
                c.BezierTo(new Point(a.X + step * 0.5, a.Y), new Point(b.X - step * 0.5, b.Y), b, true, true);
            }
            c.LineTo(new Point(Width + 200, Height + 10), true, true);
        }
        g.Freeze();
        return g;
    }

    // Sequoia-like: luminous ribbons sweeping up across the screen.
    private static void Ribbons(DrawingContext dc, bool dark)
    {
        Background(dc, dark
            ? Linear(60, (0xFF070B1F, 0), (0xFF141A44, 0.45), (0xFF2A0F3D, 1))
            : Linear(60, (0xFFFBEFF3, 0), (0xFFEDE6FA, 0.5), (0xFFDDEBFB, 1)));
        if (dark) { Blob(dc, 0.2, 0.85, 0.5, 0.35, 0x553B2CFF); Blob(dc, 0.85, 0.2, 0.45, 0.3, 0x40C03ACF); }
        else { Blob(dc, 0.15, 0.8, 0.5, 0.35, 0x66FFC4DD); Blob(dc, 0.85, 0.2, 0.45, 0.3, 0x55B9D7FF); }

        uint[] darkColors = { 0xFF2F6BFF, 0xFF6A4DFF, 0xFFB44BE0, 0xFFFF5FA8, 0xFF27C3D8, 0xFF4E8BFF };
        uint[] lightColors = { 0xFF7EA8FF, 0xFFA78BFA, 0xFFF0A6E8, 0xFFFFA8C5, 0xFF8EE3EF, 0xFF9DB8FF };
        var colors = dark ? darkColors : lightColors;
        for (int i = 0; i < 6; i++)
        {
            double o = i * 0.105;
            var p0 = new Point(-0.15 * Width, (1.05 - o) * Height);
            var p1 = new Point(0.28 * Width, (0.62 - o * 0.9 + (i % 2) * 0.06) * Height);
            var p2 = new Point(0.62 * Width, (1.02 - o * 1.25) * Height);
            var p3 = new Point(1.15 * Width, (0.30 - o * 0.85) * Height);
            double thickness = Height * (0.075 + 0.018 * i);
            uint a = colors[i], b = colors[(i + 2) % colors.Length];
            // A soft glow (wider, faint copies), then the ribbon itself.
            for (int j = 4; j >= 1; j--)
            {
                double extra = thickness * 0.22 * j;
                dc.PushOpacity(dark ? 0.07 : 0.09);
                dc.DrawGeometry(Linear(0, (a, 0), (b, 1)), null, Band(p0 - new Vector(0, extra / 2), p1 - new Vector(0, extra / 2), p2 - new Vector(0, extra / 2), p3 - new Vector(0, extra / 2), thickness + extra));
                dc.Pop();
            }
            dc.PushOpacity(dark ? 0.78 : 0.72);
            dc.DrawGeometry(Linear(0, (a, 0), ((a & 0x00FFFFFF) | 0xE0000000, 0.5), (b, 1)), null, Band(p0, p1, p2, p3, thickness));
            dc.Pop();
            // The bright edge along the top of each ribbon.
            dc.PushOpacity(dark ? 0.35 : 0.5);
            dc.DrawGeometry(Linear(0, (0x00FFFFFF, 0), (0xCCFFFFFF, 0.5), (0x00FFFFFF, 1)), null, Band(p0, p1, p2, p3, Height * 0.004));
            dc.Pop();
        }
    }

    // Sonoma-like: rolling hills fading into haze under a glowing sky.
    private static void Hills(DrawingContext dc, bool dark)
    {
        Background(dc, dark
            ? Linear(90, (0xFF060B1E, 0), (0xFF1A2550, 0.55), (0xFF3B3F78, 1))
            : Linear(90, (0xFF8FB8F2, 0), (0xFFD9DDF5, 0.5), (0xFFFAD5B8, 1)));
        if (dark) Blob(dc, 0.7, 0.36, 0.28, 0.2, 0x669FB4FF);
        else Blob(dc, 0.68, 0.42, 0.32, 0.22, 0xEEFFF1D9);

        uint[] darkLayers = { 0xFF3A4A80, 0xFF2D3C6E, 0xFF22305C, 0xFF19244A, 0xFF111A38, 0xFF0A1128 };
        uint[] lightLayers = { 0xFFC9B9E6, 0xFFA8B6E0, 0xFF7FA6C9, 0xFF5D9AA8, 0xFF3F8580, 0xFF2A6658 };
        var layers = dark ? darkLayers : lightLayers;
        var random = new Random(dark ? 11 : 7);
        for (int k = 0; k < layers.Length; k++)
        {
            double baseY = 0.46 + k * 0.085;
            var ys = new double[7];
            for (int i = 0; i < ys.Length; i++) ys[i] = baseY + (random.NextDouble() - 0.5) * (0.09 - k * 0.008);
            var top = layers[k];
            uint bottom = Darken(top, dark ? 0.35 : 0.25);
            dc.DrawGeometry(Linear(90, (top, 0), (bottom, 1)), null, Under(ys));
        }
        dc.DrawRectangle(Linear(90, (dark ? 0x00000000u : 0x00FFFFFFu, 0.4), (dark ? 0x30000000u : 0x18FFFFFFu, 1)), null, new Rect(0, 0, Width, Height));
    }

    // Big Sur-like: layered waves with soft shadows, warm at the bottom.
    private static void Waves(DrawingContext dc, bool dark)
    {
        Background(dc, dark
            ? Linear(90, (0xFF070A24, 0), (0xFF141A46, 1))
            : Linear(90, (0xFFFDE7DA, 0), (0xFFF6CFE0, 1)));
        uint[] darkPalette = { 0xFF1E3A8A, 0xFF3730A3, 0xFF6D28D9, 0xFFA21CAF, 0xFFDB2777, 0xFFEA580C, 0xFFF59E0B };
        uint[] lightPalette = { 0xFFA5B4FC, 0xFFC4B5FD, 0xFFE9A8F5, 0xFFFDA4C4, 0xFFFDB5A0, 0xFFFDCB8A, 0xFFFCE18A };
        var palette = dark ? darkPalette : lightPalette;
        var random = new Random(dark ? 5 : 3);
        for (int k = 0; k < palette.Length; k++)
        {
            double baseY = 0.22 + k * 0.11;
            var ys = new double[6];
            double phase = random.NextDouble() * Math.PI * 2;
            for (int i = 0; i < ys.Length; i++) ys[i] = baseY + Math.Sin(phase + i * 1.3) * 0.045;
            // Shadow cast on the layer behind: faint copies, a little higher each.
            for (int j = 1; j <= 6; j++)
            {
                dc.PushOpacity(dark ? 0.06 : 0.035);
                dc.DrawGeometry(new SolidColorBrush(Colors.Black), null, Under(ys, lift: j * 7));
                dc.Pop();
            }
            uint c = palette[k];
            dc.DrawGeometry(Linear(20, (Lighten(c, 0.12), 0), (c, 0.55), (Darken(c, 0.12), 1)), null, Under(ys));
        }
    }

    // Tahoe-like: big soft blooms of color on a deep (or bright) field.
    private static void Glow(DrawingContext dc, bool dark)
    {
        Background(dc, dark
            ? Linear(45, (0xFF05060F, 0), (0xFF0D1024, 1))
            : Linear(45, (0xFFF6F7FC, 0), (0xFFEEF0F8, 1)));
        if (dark)
        {
            Blob(dc, 0.25, 0.35, 0.42, 0.42, 0xCC2B59FF);
            Blob(dc, 0.72, 0.30, 0.38, 0.38, 0xB08B3DFF);
            Blob(dc, 0.60, 0.80, 0.45, 0.40, 0xA0FF3D9A);
            Blob(dc, 0.15, 0.90, 0.30, 0.30, 0x9019C5D6);
            Blob(dc, 0.92, 0.75, 0.25, 0.25, 0x80FF8A3D);
        }
        else
        {
            Blob(dc, 0.25, 0.35, 0.42, 0.42, 0xAA7AA2FF);
            Blob(dc, 0.72, 0.30, 0.38, 0.38, 0x99C4A3FF);
            Blob(dc, 0.60, 0.80, 0.45, 0.40, 0x99FFA3CF);
            Blob(dc, 0.15, 0.90, 0.30, 0.30, 0x8888E3EE);
            Blob(dc, 0.92, 0.75, 0.25, 0.25, 0x88FFC48A);
        }
    }

    private static uint Darken(uint argb, double by)
    {
        var c = C(argb);
        return 0xFF000000u | ((uint)(c.R * (1 - by)) << 16) | ((uint)(c.G * (1 - by)) << 8) | (uint)(c.B * (1 - by));
    }

    private static uint Lighten(uint argb, double by)
    {
        var c = C(argb);
        return 0xFF000000u | ((uint)(c.R + (255 - c.R) * by) << 16) | ((uint)(c.G + (255 - c.G) * by) << 8) | (uint)(c.B + (255 - c.B) * by);
    }
}
