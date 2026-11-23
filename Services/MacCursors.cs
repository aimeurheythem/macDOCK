using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MacDock.Services;

/// <summary>
/// macOS-style mouse pointers for the whole system: the black arrow with a white edge, the pointing hand,
/// the thin text beam, the spinning color wheel for "busy", resize arrows and the rest. Drawn here (vector
/// shapes, several sizes so they stay sharp at any display scaling), written as .cur / .ani files, and set
/// as the user's pointer scheme. The previous scheme is saved and put back when the option is turned off
/// or MacDock is uninstalled.
/// </summary>
internal static class MacCursors
{
    private const string CursorsKey = @"Control Panel\Cursors";
    private static readonly int[] Sizes = { 32, 40, 48, 64, 96, 128 };
    private static readonly int[] AniSizes = { 32, 40, 48, 64 };

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDock", "Cursors");
    private static string BackupFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "cursors-backup.json");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
    private const uint SPI_SETCURSORS = 0x0057, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

    /// <summary>Registry value name of each pointer role, and how it is drawn.</summary>
    private static readonly (string Role, string File, Func<Frame[]> Make)[] Roles =
    {
        ("Arrow", "arrow.cur", () => new[] { Still(DrawArrow, 3, 2) }),
        ("Hand", "hand.cur", () => new[] { Still(DrawHand, 12.6, 3) }),
        ("IBeam", "ibeam.cur", () => new[] { Still(DrawBeam, 16, 16) }),
        ("Wait", "busy.ani", () => Spinner(withArrow: false)),
        ("AppStarting", "working.ani", () => Spinner(withArrow: true)),
        ("Help", "help.cur", () => new[] { Still(DrawHelp, 3, 2) }),
        ("No", "no.cur", () => new[] { Still(DrawNo, 16, 16) }),
        ("Crosshair", "cross.cur", () => new[] { Still(DrawCross, 16, 16) }),
        ("SizeWE", "size-we.cur", () => new[] { Still(dc => DrawResize(dc, 0), 16, 16) }),
        ("SizeNS", "size-ns.cur", () => new[] { Still(dc => DrawResize(dc, 90), 16, 16) }),
        ("SizeNWSE", "size-nwse.cur", () => new[] { Still(dc => DrawResize(dc, 45), 16, 16) }),
        ("SizeNESW", "size-nesw.cur", () => new[] { Still(dc => DrawResize(dc, -45), 16, 16) }),
        ("SizeAll", "move.cur", () => new[] { Still(DrawMove, 16, 16) }),
        ("UpArrow", "up.cur", () => new[] { Still(DrawUp, 16, 3) }),
    };

    // ---------------- Apply / restore ----------------

    /// <summary>Writes the pointer files and makes them the user's pointers. Must run on a UI (STA) thread.</summary>
    public static void Apply()
    {
        Directory.CreateDirectory(Folder);
        var paths = new Dictionary<string, string>();
        foreach (var (role, file, make) in Roles)
        {
            string path = Path.Combine(Folder, file);
            var frames = make();
            File.WriteAllBytes(path, frames.Length == 1 ? CurFile(frames[0]) : AniFile(frames, jiffies: 4));
            paths[role] = path;
        }

        using var key = Registry.CurrentUser.CreateSubKey(CursorsKey);
        if (!File.Exists(BackupFile))
        {
            // Keep what the user had (only the first time: a re-apply must not save our own scheme as "theirs").
            var backup = new Dictionary<string, string?>();
            foreach (var name in paths.Keys.Append("").Append("Scheme Source"))
                backup[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup));
        }
        foreach (var (role, path) in paths) key.SetValue(role, path, RegistryValueKind.ExpandString);
        key.SetValue("", "MacDock");
        key.SetValue("Scheme Source", 1, RegistryValueKind.DWord);
        Refresh();
    }

    /// <summary>Puts the user's previous pointers back.</summary>
    public static void Restore()
    {
        if (!File.Exists(BackupFile)) return;
        var backup = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(BackupFile)) ?? new();
        using (var key = Registry.CurrentUser.CreateSubKey(CursorsKey))
        {
            foreach (var (name, value) in backup)
            {
                if (name == "Scheme Source")
                {
                    if (int.TryParse(value, out int source)) key.SetValue(name, source, RegistryValueKind.DWord);
                    else key.DeleteValue(name, false);
                }
                else if (value == null) key.DeleteValue(name, false);
                else key.SetValue(name, value, name.Length == 0 ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
        }
        File.Delete(BackupFile);
        Refresh();
    }

    private static void Refresh() => SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

    // ---------------- Drawing (on a 32 x 32 grid, scaled to each size) ----------------

    private sealed record Frame(Action<DrawingContext> Draw, double HotX, double HotY);

    private static Frame Still(Action<DrawingContext> draw, double hotX, double hotY) => new(draw, hotX, hotY);

    private static readonly Brush Black = Frozen(Color.FromRgb(0, 0, 0));
    private static readonly Brush White = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush Shadow = Frozen(Color.FromArgb(0x48, 0, 0, 0));
    private static readonly Brush SoftShadow = Frozen(Color.FromArgb(0x30, 0, 0, 0));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen Line(Brush brush, double width) => new(brush, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    /// <summary>A shape the macOS way: soft shadow, white edge, black (or given) body.</summary>
    private static void Shape(DrawingContext dc, Geometry shape, Brush? body = null, Brush? edge = null, double edgeWidth = 1.6)
    {
        dc.PushTransform(new TranslateTransform(0.35, 0.9));
        dc.DrawGeometry(Shadow, Line(Shadow, edgeWidth + 0.8), shape);
        dc.Pop();
        dc.DrawGeometry(edge ?? White, Line(edge ?? White, edgeWidth * 2), shape);
        dc.DrawGeometry(body ?? Black, null, shape);
    }

    private static Geometry Shape(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static readonly Geometry ArrowShape = Shape("M 3,2 L 3,19.6 L 7.1,15.8 L 9.9,22.2 L 12.6,21.1 L 9.9,14.9 L 15.3,14.9 Z");

    private static void DrawArrow(DrawingContext dc) => Shape(dc, ArrowShape, edgeWidth: 1.35);

    private static void DrawHand(DrawingContext dc)
    {
        // Pointing hand: index finger up, three folded fingers, thumb, palm (white, black outline).
        Geometry Round(double x, double y, double w, double h, double r) => new RectangleGeometry(new Rect(x, y, w, h), r, r);
        Geometry hand = Round(10.8, 3, 3.6, 15, 1.8);
        foreach (var part in new[]
                 {
                     Round(14.2, 10.2, 3.5, 9, 1.75), Round(17.5, 11, 3.4, 8.5, 1.7), Round(20.7, 12.4, 3.2, 7.4, 1.6),
                     Round(10.8, 14.5, 13.1, 12.5, 3.6),
                 })
            hand = new CombinedGeometry(GeometryCombineMode.Union, hand, part);
        var thumb = new RectangleGeometry(new Rect(5.4, 15.4, 7.6, 3.4), 1.7, 1.7) { Transform = new RotateTransform(42, 11.6, 18.5) };
        hand = new CombinedGeometry(GeometryCombineMode.Union, hand, thumb);
        hand.Freeze();

        dc.PushTransform(new TranslateTransform(0.35, 0.9));
        dc.DrawGeometry(SoftShadow, Line(SoftShadow, 1.2), hand);
        dc.Pop();
        dc.DrawGeometry(White, Line(Black, 1.15), hand);
        var crease = Line(Black, 0.9);
        dc.DrawLine(crease, new Point(14.3, 13.8), new Point(14.3, 17.2));
        dc.DrawLine(crease, new Point(17.6, 14.4), new Point(17.6, 17.6));
        dc.DrawLine(crease, new Point(20.8, 15.6), new Point(20.8, 18.2));
    }

    private static void DrawBeam(DrawingContext dc)
    {
        var beam = Shape("M 12.5,5.5 Q 15,5.5 16,7.2 Q 17,5.5 19.5,5.5 M 16,7.2 L 16,24.8 M 12.5,26.5 Q 15,26.5 16,24.8 Q 17,26.5 19.5,26.5 M 14,16 L 18,16");
        dc.DrawGeometry(null, Line(White, 3.6), beam);
        dc.DrawGeometry(null, Line(Black, 1.4), beam);
    }

    private static void DrawHelp(DrawingContext dc)
    {
        DrawArrow(dc);
        var bubble = new EllipseGeometry(new Point(21.5, 21), 6, 6);
        Shape(dc, bubble, Frozen(Color.FromRgb(0x1E, 0x7C, 0xF2)), edgeWidth: 1.1);
        var text = new FormattedText("?", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 9, White, 1.0);
        dc.DrawText(text, new Point(21.5 - text.Width / 2, 21 - text.Height / 2));
    }

    private static void DrawNo(DrawingContext dc)
    {
        var ring = new EllipseGeometry(new Point(16, 16), 8.5, 8.5);
        var slash = Shape("M 10,22 L 22,10");
        dc.DrawGeometry(null, Line(White, 6), ring);
        dc.DrawGeometry(null, Line(White, 6), slash);
        dc.DrawGeometry(null, Line(Black, 2.6), ring);
        dc.DrawGeometry(null, Line(Black, 2.6), slash);
    }

    private static void DrawCross(DrawingContext dc)
    {
        var cross = Shape("M 16,4 L 16,13 M 16,19 L 16,28 M 4,16 L 13,16 M 19,16 L 28,16");
        dc.DrawGeometry(null, Line(White, 3.2), cross);
        dc.DrawGeometry(null, Line(Black, 1.2), cross);
    }

    private static readonly Geometry DoubleArrow = Shape("M 4,16 L 10,10 L 10,14 L 22,14 L 22,10 L 28,16 L 22,22 L 22,18 L 10,18 L 10,22 Z");

    private static void DrawResize(DrawingContext dc, double angle)
    {
        dc.PushTransform(new RotateTransform(angle, 16, 16));
        Shape(dc, DoubleArrow, edgeWidth: 1.25);
        dc.Pop();
    }

    private static void DrawMove(DrawingContext dc)
    {
        var shape = Shape("M 16,3 L 21,8.5 L 17.6,8.5 L 17.6,14.4 L 23.5,14.4 L 23.5,11 L 29,16 L 23.5,21 L 23.5,17.6 L 17.6,17.6 L 17.6,23.5 L 21,23.5 L 16,29 L 11,23.5 L 14.4,23.5 L 14.4,17.6 L 8.5,17.6 L 8.5,21 L 3,16 L 8.5,11 L 8.5,14.4 L 14.4,14.4 L 14.4,8.5 L 11,8.5 Z");
        Shape(dc, shape, edgeWidth: 1.2);
    }

    private static void DrawUp(DrawingContext dc) =>
        Shape(dc, Shape("M 16,3 L 23,11 L 18.2,11 L 18.2,26 L 13.8,26 L 13.8,11 L 9,11 Z"), edgeWidth: 1.25);

    // ---------------- The spinning wheel ----------------

    private static readonly Color[] WheelColors =
    {
        Color.FromRgb(0x2F, 0x7B, 0xF6), Color.FromRgb(0x2E, 0xB8, 0xF0), Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0xA6, 0xD8, 0x2E),
        Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0xFF, 0x2D, 0x8B),
        Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0x5E, 0x5C, 0xE6),
    };

    private const int SpinFrames = 12;

    private static Frame[] Spinner(bool withArrow)
    {
        var frames = new Frame[SpinFrames];
        for (int i = 0; i < SpinFrames; i++)
        {
            double turn = i * 360.0 / SpinFrames;
            frames[i] = withArrow
                ? new Frame(dc => { DrawArrow(dc); DrawWheel(dc, new Point(21.5, 22), 5.6, turn); }, 3, 2)
                : new Frame(dc => DrawWheel(dc, new Point(16, 16), 10.5, turn), 16, 16);
        }
        return frames;
    }

    private static void DrawWheel(DrawingContext dc, Point c, double r, double turn)
    {
        dc.DrawEllipse(Shadow, null, new Point(c.X + 0.3, c.Y + 0.8), r + 0.9, r + 0.9);
        dc.DrawEllipse(White, null, c, r + 0.8, r + 0.8);
        int n = WheelColors.Length;
        for (int k = 0; k < n; k++)
        {
            double a0 = (turn + k * 360.0 / n) * Math.PI / 180, a1 = (turn + (k + 1) * 360.0 / n) * Math.PI / 180;
            var slice = new StreamGeometry();
            using (var g = slice.Open())
            {
                g.BeginFigure(c, true, true);
                g.LineTo(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), true, false);
                // A swirl: each slice bends a little, like the macOS wheel.
                g.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                g.QuadraticBezierTo(new Point(c.X + r * 0.55 * Math.Cos(a1 - 0.5), c.Y + r * 0.55 * Math.Sin(a1 - 0.5)), c, true, false);
            }
            slice.Freeze();
            dc.DrawGeometry(Frozen(WheelColors[k]), null, slice);
        }
        // Glassy shine on top.
        var shine = new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF))
        {
            Center = new Point(0.4, 0.3), GradientOrigin = new Point(0.4, 0.25), RadiusX = 0.6, RadiusY = 0.5,
        };
        shine.Freeze();
        dc.DrawEllipse(shine, null, c, r, r);
    }

    // ---------------- File formats ----------------

    /// <summary>One frame at one size: 32-bit BGRA, straight alpha.</summary>
    private static byte[] Pixels(Frame frame, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
            frame.Draw(dc);
            dc.Pop();
        }
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        straight.CopyPixels(pixels, size * 4, 0);
        return pixels;
    }

    /// <summary>A .cur file holding the frame at every size (Windows picks the one for the display scaling).</summary>
    private static byte[] CurFile(Frame frame) => CurFile(frame, Sizes);

    private static byte[] CurFile(Frame frame, int[] sizes)
    {
        var images = sizes.Select(s => (Size: s, Data: Dib(Pixels(frame, s), s))).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)2); // cursor
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)Math.Round(frame.HotX * size / 32.0));
            w.Write((ushort)Math.Round(frame.HotY * size / 32.0));
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
        return ms.ToArray();
    }

    /// <summary>Icon-style bitmap: header (double height), bottom-up BGRA rows, then an empty AND mask.</summary>
    private static byte[] Dib(byte[] bgra, int size)
    {
        int maskStride = (size + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);
        w.Write(size);
        w.Write(size * 2);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0);
        w.Write(size * size * 4 + maskStride * size);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = size - 1; y >= 0; y--) w.Write(bgra, y * size * 4, size * 4);
        w.Write(new byte[maskStride * size]);
        return ms.ToArray();
    }

    /// <summary>An animated .ani file (RIFF "ACON"): every frame is a complete .cur.</summary>
    private static byte[] AniFile(Frame[] frames, int jiffies)
    {
        // Animated pointers only load with frames up to 64 px (like Windows' own busy wheel).
        var curs = frames.Select(f => CurFile(f, AniSizes)).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Chunk(string id, byte[] data)
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes(id));
            w.Write(data.Length);
            w.Write(data);
            if (data.Length % 2 == 1) w.Write((byte)0);
        }

        using var anih = new MemoryStream();
        using (var a = new BinaryWriter(anih, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            a.Write(36);            // header size
            a.Write(curs.Count);    // frames
            a.Write(curs.Count);    // steps
            a.Write(0); a.Write(0);  // width, height (taken from the frames)
            a.Write(32); a.Write(1); // bit count, planes
            a.Write(jiffies);       // 1/60 s per step
            a.Write(1);             // AF_ICON: frames are icon/cursor data
        }

        using var fram = new MemoryStream();
        using (var f = new BinaryWriter(fram, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            f.Write(System.Text.Encoding.ASCII.GetBytes("fram"));
            foreach (var cur in curs)
            {
                f.Write(System.Text.Encoding.ASCII.GetBytes("icon"));
                f.Write(cur.Length);
                f.Write(cur);
                if (cur.Length % 2 == 1) f.Write((byte)0);
            }
        }

        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0); // size, filled below
        w.Write(System.Text.Encoding.ASCII.GetBytes("ACON"));
        Chunk("anih", anih.ToArray());
        Chunk("LIST", fram.ToArray());
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        return bytes;
    }
}
