using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacDock.Services;

/// <summary>
/// Clean, vector-drawn replacements for built-in Windows apps whose own icons are small and dated
/// (Command Prompt, Task Manager, Control Panel...). Drawn at the exact pixel size requested, so they
/// are perfectly sharp at any size. macOS-style: gradient rounded tile, soft gloss, white symbol.
/// </summary>
internal static class SystemIcons
{
    private enum Kind { CommandPrompt, PowerShell, Terminal, TaskManager, ControlPanel, Notepad, Paint, SnippingTool, Calculator, Settings, AppStore, Photos }

    private static readonly Dictionary<string, Kind> ByExe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd.exe"] = Kind.CommandPrompt,
        ["powershell.exe"] = Kind.PowerShell,
        ["powershell_ise.exe"] = Kind.PowerShell,
        ["pwsh.exe"] = Kind.PowerShell,
        ["windowsterminal.exe"] = Kind.Terminal,
        ["wt.exe"] = Kind.Terminal,
        ["taskmgr.exe"] = Kind.TaskManager,
        ["control.exe"] = Kind.ControlPanel,
        ["notepad.exe"] = Kind.Notepad,
        ["mspaint.exe"] = Kind.Paint,
        ["snippingtool.exe"] = Kind.SnippingTool,
        ["calc.exe"] = Kind.Calculator,
        ["calculatorapp.exe"] = Kind.Calculator,
        ["systemsettings.exe"] = Kind.Settings,
        ["git-bash.exe"] = Kind.Terminal,
        ["mintty.exe"] = Kind.Terminal,
        ["winstore.app.exe"] = Kind.AppStore,
        ["microsoft.photos.exe"] = Kind.Photos,
        ["photos.exe"] = Kind.Photos,
    };

    private static readonly (string IdPart, Kind Kind)[] ByAppId =
    {
        ("Microsoft.WindowsTerminal", Kind.Terminal),
        ("Microsoft.WindowsCalculator", Kind.Calculator),
        ("windows.immersivecontrolpanel", Kind.Settings),
        ("Microsoft.WindowsNotepad", Kind.Notepad),
        ("Microsoft.Paint", Kind.Paint),
        ("Microsoft.ScreenSketch", Kind.SnippingTool),
        ("Microsoft.Windows.ControlPanel", Kind.ControlPanel),
        ("Microsoft.WindowsStore", Kind.AppStore),
        ("Microsoft.Windows.Photos", Kind.Photos),
    };

    private static readonly Dictionary<string, Kind> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Command Prompt"] = Kind.CommandPrompt,
        ["Windows PowerShell"] = Kind.PowerShell,
        ["PowerShell"] = Kind.PowerShell,
        ["Terminal"] = Kind.Terminal,
        ["Windows Terminal"] = Kind.Terminal,
        ["Task Manager"] = Kind.TaskManager,
        ["Control Panel"] = Kind.ControlPanel,
        ["Notepad"] = Kind.Notepad,
        ["Paint"] = Kind.Paint,
        ["Snipping Tool"] = Kind.SnippingTool,
        ["Calculator"] = Kind.Calculator,
        ["Settings"] = Kind.Settings,
        ["System Settings"] = Kind.Settings,
        ["Git Bash"] = Kind.Terminal,
        ["Microsoft Store"] = Kind.AppStore,
        ["App Store"] = Kind.AppStore,
        ["Photos"] = Kind.Photos,
        ["Activity Monitor"] = Kind.TaskManager,
    };

    private static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Typeface MonoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>A sharp vector icon of <paramref name="px"/> pixels if this is a known built-in app, else null.</summary>
    public static BitmapSource? TryRender(string? name, string? exePath, string? appId, int px)
    {
        var kind = Match(name, exePath, appId);
        return kind == null ? null : Render(kind.Value, px);
    }

    private static Kind? Match(string? name, string? exePath, string? appId)
    {
        if (!string.IsNullOrEmpty(exePath) && ByExe.TryGetValue(Path.GetFileName(exePath), out var k)) return k;
        if (!string.IsNullOrEmpty(appId))
            foreach (var (part, kind) in ByAppId)
                if (appId.Contains(part, StringComparison.OrdinalIgnoreCase)) return kind;
        if (!string.IsNullOrEmpty(name) && ByName.TryGetValue(name.Trim(), out k)) return k;
        return null;
    }

    // ---------------- Drawing ----------------

    private static BitmapSource Render(Kind kind, int px)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double s = px;
            switch (kind)
            {
                case Kind.CommandPrompt:
                case Kind.PowerShell:
                case Kind.Terminal:
                    MacIcons.Terminal(dc, s);
                    break;
                case Kind.TaskManager:
                    MacIcons.ActivityMonitor(dc, s);
                    break;
                case Kind.ControlPanel:
                case Kind.Settings:
                    MacIcons.SystemSettings(dc, s);
                    break;
                case Kind.AppStore:
                    MacIcons.AppStore(dc, s);
                    break;
                case Kind.Photos:
                    MacIcons.Photos(dc, s);
                    break;
                case Kind.Notepad:
                    Tile(dc, s, "#FFFFFF", "#ECECF0");
                    dc.PushClip(TileGeometry(s));
                    dc.DrawRectangle(new LinearGradientBrush(Hex("#FFE15A"), Hex("#FFC400"), 90), null, new Rect(0, 0, s, s * 0.3));
                    dc.Pop();
                    var line = new Pen(Brush("#C7C7CC"), s * 0.035) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    foreach (var y in new[] { 0.47, 0.6, 0.73 })
                        dc.DrawLine(line, new Point(s * 0.22, s * y), new Point(s * (y > 0.7 ? 0.6 : 0.78), s * y));
                    TileOutline(dc, s);
                    break;
                case Kind.Paint:
                    Tile(dc, s, "#FFB340", "#FF375F");
                    Symbol(dc, s, "\uE790", 0.52);
                    break;
                case Kind.SnippingTool:
                    Tile(dc, s, "#D17BFF", "#7D3CD0");
                    Symbol(dc, s, "\uE8C6", 0.5);
                    break;
                case Kind.Calculator:
                    Tile(dc, s, "#48484A", "#1C1C1E");
                    double r = s * 0.075;
                    for (int row = 0; row < 3; row++)
                        for (int col = 0; col < 3; col++)
                        {
                            var fill = col == 2 ? Brush("#FF9F0A") : row == 0 ? Brush("#D4D4D2") : Brush("#6B6B6F");
                            dc.DrawEllipse(fill, null, new Point(s * (0.3 + col * 0.2), s * (0.36 + row * 0.2)), r, r);
                        }
                    break;
            }
        }

        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Geometry TileGeometry(double s)
    {
        double radius = s * 0.235;
        return new RectangleGeometry(new Rect(0, 0, s, s), radius, radius);
    }

    /// <summary>Rounded tile with a vertical gradient and a soft top gloss.</summary>
    private static void Tile(DrawingContext dc, double s, string top, string bottom)
    {
        var shape = TileGeometry(s);
        dc.DrawGeometry(new LinearGradientBrush(Hex(top), Hex(bottom), 90), null, shape);
        var gloss = new LinearGradientBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90);
        dc.PushClip(shape);
        dc.DrawRectangle(gloss, null, new Rect(0, 0, s, s * 0.5));
        dc.Pop();
    }

    private static void TileOutline(DrawingContext dc, double s)
    {
        double radius = s * 0.235;
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 1),
            new Rect(0.5, 0.5, s - 1, s - 1), radius, radius);
    }

    /// <summary>"&gt;_" terminal prompt.</summary>
    private static void Prompt(DrawingContext dc, double s, Brush brush, double yOffset = 0)
    {
        var text = new FormattedText(">_", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, s * 0.42, brush, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var b = geometry.Bounds;
        dc.PushTransform(new TranslateTransform(s * 0.2 - b.Left, s * (0.5 + yOffset) - b.Top - b.Height / 2));
        dc.DrawGeometry(brush, null, geometry);
        dc.Pop();
    }

    /// <summary>A Segoe symbol, centered, as crisp vector geometry.</summary>
    private static void Symbol(DrawingContext dc, double s, string glyph, double size)
    {
        var face = new Typeface(SymbolFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, s * size, Brushes.White, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var b = geometry.Bounds;
        if (b.IsEmpty) return;
        dc.PushTransform(new TranslateTransform(s / 2 - b.Left - b.Width / 2, s / 2 - b.Top - b.Height / 2));
        dc.DrawGeometry(Brushes.White, null, geometry);
        dc.Pop();
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Brush(string hex) => new(Hex(hex));
}
