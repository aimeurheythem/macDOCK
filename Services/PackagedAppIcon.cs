using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace MacDock.Services;

/// <summary>
/// Loads the real, high-resolution logo of a packaged (Store / MSIX) app straight from its package:
/// AppxManifest.xml -> Square44x44Logo -> the largest "unplated" asset on disk (e.g.
/// AppList.targetsize-256_altform-unplated.png). The shell's generic icon for these apps is a small,
/// padded rendering, which is why they looked tiny and blurry.
/// Bright single-color glyphs (old-style tiles) are put on a rounded "squircle" plate so they stay visible.
/// </summary>
internal static class PackagedAppIcon
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string familyName, ref uint count, IntPtr fullNames,
        ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint pathLength, StringBuilder? path);

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private static readonly Regex TargetSize = new(@"targetsize-(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex Scale = new(@"scale-(\d+)", RegexOptions.IgnoreCase);

    /// <summary>True for AppIDs of packaged apps ("Family_publisher!AppId").</summary>
    public static bool IsPackagedAppId(string appId) =>
        appId.Contains('!') && appId.Split('!')[0].Contains('_') && !appId.Contains('\\');

    /// <summary>Returns a square icon of <paramref name="outPx"/> pixels, or null when the package has no loose logo files.</summary>
    public static BitmapSource? TryLoad(string appId, int outPx)
    {
        try
        {
            var parts = appId.Split('!');
            if (parts.Length != 2) return null;
            foreach (var root in GetPackagePaths(parts[0]))
            {
                var manifest = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifest)) continue;
                if (FindLogo(manifest, parts[1]) is not { } logo) continue;

                var file = PickBestAsset(root, logo.Path);
                if (file == null) continue;

                var image = LoadPng(file.Value.Path);
                if (image == null) continue;

                bool bright = IsBrightGlyph(image);
                if (!file.Value.Unplated || bright)
                    return IconStyler.OnSquircle(image, PlateColor(logo.Background), outPx);
                return IconService.NormalizeSquare(image, outPx, IconStyler.ContentFill);
            }
        }
        catch
        {
            // Fall back to the shell icon.
        }
        return null;
    }

    /// <summary>
    /// The AppID ("Family_publisher!AppId") of a Store app's exe, e.g.
    /// "C:\Program Files\WindowsApps\Claude_2.3.0_x64__pzs8sxrjxfjjc\app\claude.exe" gives
    /// "Claude_pzs8sxrjxfjjc!Claude". Windows refuses to start these exes directly ("Access is denied"):
    /// they start through "shell:AppsFolder\{AppID}". Also works after the app updated (new folder).
    /// Null for anything that is not a packaged app.
    /// </summary>
    public static string? AppIdForExe(string? exePath)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            const string marker = @"\WindowsApps\";
            int at = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string rest = exePath[(at + marker.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash < 0) return null;
            string fullName = rest[..slash], relative = rest[(slash + 1)..];
            var parts = fullName.Split('_');
            if (parts.Length < 5) return null;
            string family = parts[0] + "_" + parts[^1];

            var roots = new List<string> { exePath[..(at + marker.Length)] + fullName };
            roots.AddRange(GetPackagePaths(family));
            foreach (var root in roots)
            {
                var manifest = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifest)) continue;
                var apps = XDocument.Load(manifest).Descendants().Where(e => e.Name.LocalName == "Application").ToList();
                var match = apps.FirstOrDefault(a => string.Equals((string?)a.Attribute("Executable"), relative, StringComparison.OrdinalIgnoreCase))
                            ?? apps.FirstOrDefault();
                if ((string?)match?.Attribute("Id") is { Length: > 0 } id) return family + "!" + id;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("PackagedAppIcon.AppIdForExe", ex);
        }
        return null;
    }

    private static IEnumerable<string> GetPackagePaths(string familyName)
    {
        uint count = 0, bufferLength = 0;
        if (GetPackagesByPackageFamily(familyName, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero) != ERROR_INSUFFICIENT_BUFFER
            || count == 0)
            yield break;

        IntPtr names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
        IntPtr buffer = Marshal.AllocHGlobal((int)bufferLength * 2);
        var fullNames = new List<string>();
        try
        {
            if (GetPackagesByPackageFamily(familyName, ref count, names, ref bufferLength, buffer) != 0) yield break;
            for (int i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size));
                if (name != null) fullNames.Add(name);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(buffer);
        }

        foreach (var fullName in fullNames)
        {
            uint length = 0;
            GetPackagePathByFullName(fullName, ref length, null);
            if (length == 0) continue;
            var path = new StringBuilder((int)length);
            if (GetPackagePathByFullName(fullName, ref length, path) == 0)
                yield return path.ToString();
        }
    }

    private static (string Path, string? Background)? FindLogo(string manifestPath, string appId)
    {
        var doc = XDocument.Load(manifestPath);
        var app = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Application" && (string?)e.Attribute("Id") == appId);
        var visuals = app?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
        if (visuals == null) return null;

        var logo = (string?)visuals.Attribute("Square44x44Logo") ?? (string?)visuals.Attribute("Square150x150Logo");
        return logo == null ? null : (logo, (string?)visuals.Attribute("BackgroundColor"));
    }

    /// <summary>
    /// Logos exist in many variants next to the declared file name. Prefer light-theme unplated,
    /// then unplated, then plated; then the biggest size. High-contrast variants are skipped.
    /// </summary>
    private static (string Path, bool Unplated)? PickBestAsset(string root, string logo)
    {
        var dir = Path.Combine(root, Path.GetDirectoryName(logo) ?? "");
        if (!Directory.Exists(dir)) return null;
        var baseName = Path.GetFileNameWithoutExtension(logo);
        var ext = Path.GetExtension(logo);

        (string Path, bool Unplated, int Score)? best = null;
        foreach (var file in Directory.EnumerateFiles(dir, baseName + "*" + ext))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                continue;
            var qualifiers = name.Length > baseName.Length ? name[(baseName.Length + 1)..].ToLowerInvariant() : "";
            if (qualifiers.Contains("contrast-")) continue;

            int variant = qualifiers.Contains("altform-lightunplated") ? 3
                        : qualifiers.Contains("altform-unplated") ? 2
                        : 1;
            int size = TargetSize.Match(qualifiers) is { Success: true } t ? int.Parse(t.Groups[1].Value)
                     : Scale.Match(qualifiers) is { Success: true } s ? 44 * int.Parse(s.Groups[1].Value) / 100
                     : 44;
            int score = variant * 10000 + Math.Min(size, 9999);
            if (best == null || score > best.Value.Score)
                best = (file, variant >= 2, score);
        }
        return best == null ? null : (best.Value.Path, best.Value.Unplated);
    }

    private static BitmapSource? LoadPng(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Mostly white/very light opaque pixels: would disappear on light glass.</summary>
    private static bool IsBrightGlyph(BitmapSource image)
    {
        var src = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);

        double lum = 0, sat = 0, weight = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double a = px[i + 3] / 255.0;
            if (a < 0.3) continue;
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            lum += a * (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            sat += a * (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0;
            weight += a;
        }
        if (weight < 1) return false;
        return lum / weight > 0.82 && sat / weight < 0.15;
    }

    private static Color PlateColor(string? background)
    {
        if (!string.IsNullOrWhiteSpace(background) && !background.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            try { return (Color)ColorConverter.ConvertFromString(background); }
            catch { /* named color we don't know */ }
        }
        return Color.FromRgb(0x0A, 0x84, 0xFF); // Apple system blue
    }
}

/// <summary>Shared icon geometry so every icon in the Start menu has the same visual size.</summary>
internal static class IconStyler
{
    /// <summary>Share of the canvas the icon content occupies (like macOS icon grids).</summary>
    public const double ContentFill = 0.86;

    /// <summary>Corner radius of tiles, as a share of their size (close to Apple's icon shape).</summary>
    private const double TileRadius = 0.235;

    /// <summary>
    /// Uniform rounded tile for the Start grid, from a master whose content fills the square:
    ///  - icons that are already a full square (Store, Claude...) are clipped to the rounded shape;
    ///  - free-form icons (Chrome, old Windows icons...) sit centered on a white rounded plate.
    /// Every icon ends up the same shape and size, like macOS / iPadOS.
    /// </summary>
    public static BitmapSource MakeTile(BitmapSource master, int px)
    {
        double radius = px * TileRadius;
        var rect = new Rect(0.5, 0.5, px - 1, px - 1);
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(0x1F, 0, 0, 0)), 1);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            if (Coverage(master) > 0.78)
            {
                dc.PushClip(new RectangleGeometry(rect, radius, radius));
                // Slight overscan: the icon's own (different) corner shape must not leave a light ring.
                double over = px * 0.05;
                dc.DrawImage(master, new Rect(-over, -over, px + 2 * over, px + 2 * over));
                dc.Pop();
                dc.DrawRoundedRectangle(null, outline, rect, radius, radius);
            }
            else
            {
                var plate = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFE, 0xFC), Color.FromRgb(0xF3, 0xF0, 0xEA), 90);
                dc.DrawRoundedRectangle(plate, outline, rect, radius, radius);
                double g = px * 0.64;
                dc.DrawImage(master, new Rect((px - g) / 2, (px - g) / 2, g, g));
            }
        }
        return Render(visual, px);
    }

    /// <summary>
    /// Dock icons: an icon whose artwork is a full square with sharp corners (AnyDesk, many classic
    /// Windows apps) gets the same rounded corners as macOS-style icons (Claude, Store apps).
    /// Icons that are already rounded, round (Chrome) or free-form are returned unchanged.
    /// </summary>
    public static ImageSource? RoundSquareCorners(ImageSource? icon)
    {
        if (icon is not BitmapSource source) return icon;
        try
        {
            var src = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w < 16 || h < 16) return icon;
            var px = new byte[w * h * 4];
            src.CopyPixels(px, w * 4, 0);
            byte Alpha(int x, int y) => px[(y * w + x) * 4 + 3];

            // Visible content box.
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (Alpha(x, y) > 24)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return icon;
            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            if (cw < w * 0.3 || Math.Abs(cw - ch) > Math.Max(cw, ch) * 0.08) return icon; // not a square tile

            // Square corners: the pixels just inside each corner of the box are solid.
            int d = Math.Max(1, (int)Math.Round(cw * 0.03));
            bool Solid(int x, int y) => Alpha(x, y) > 200;
            if (!(Solid(minX + d, minY + d) && Solid(maxX - d, minY + d) && Solid(minX + d, maxY - d) && Solid(maxX - d, maxY - d)))
                return icon; // already rounded or not a tile

            // And it is a filled tile, not an outline: most of the box is solid.
            int solid = 0, total = 0;
            for (int y = minY; y <= maxY; y += 2)
                for (int x = minX; x <= maxX; x += 2)
                {
                    total++;
                    if (Alpha(x, y) > 200) solid++;
                }
            if (solid < total * 0.9) return icon;

            var box = new Rect(minX, minY, cw, ch);
            double radius = Math.Min(cw, ch) * 0.225; // the same shape as the rest of the dock
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(box, radius, radius));
                dc.DrawImage(src, new Rect(0, 0, w, h));
                dc.Pop();
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)), 1),
                    new Rect(box.X + 0.5, box.Y + 0.5, box.Width - 1, box.Height - 1), radius, radius);
            }
            var result = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            result.Freeze();
            return result;
        }
        catch
        {
            return icon;
        }
    }

    /// <summary>Plain icon (no plate) centered at <paramref name="fill"/> of a square, for list rows.</summary>
    public static BitmapSource Centered(BitmapSource master, int px, double fill = 0.92)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        double size = px * fill;
        using (var dc = visual.RenderOpen())
            dc.DrawImage(master, new Rect((px - size) / 2, (px - size) / 2, size, size));
        return Render(visual, px);
    }

    /// <summary>Share of solid pixels: near 1 for square, full-bleed icons.</summary>
    private static double Coverage(BitmapSource image)
    {
        var src = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        int solid = 0;
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] > 200) solid++;
        return solid / (double)(w * h);
    }

    private static BitmapSource Render(Visual visual, int px)
    {
        var result = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    /// <summary>Draws a glyph centered on a rounded-square plate with a soft top-light gradient.</summary>
    public static BitmapSource OnSquircle(BitmapSource glyph, Color plate, int outPx)
    {
        double size = outPx * ContentFill, offset = (outPx - size) / 2;
        var rect = new Rect(offset, offset, size, size);
        double radius = size * 0.225; // close to Apple's continuous-corner icon shape

        var fill = new LinearGradientBrush(Lighten(plate, 0.18), plate, 90);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(fill, null, rect, radius, radius);
            // Glossy top highlight.
            var gloss = new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90);
            dc.DrawRoundedRectangle(gloss, null, new Rect(rect.X, rect.Y, rect.Width, rect.Height * 0.5), radius, radius);

            var glyphSquare = IconService.NormalizeSquare(glyph, outPx, 1.0);
            double g = size * 0.62;
            dc.DrawImage(glyphSquare, new Rect((outPx - g) / 2, (outPx - g) / 2, g, g));
        }

        var result = new RenderTargetBitmap(outPx, outPx, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));
}
