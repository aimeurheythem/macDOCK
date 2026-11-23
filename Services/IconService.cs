using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Extracts sharp, high-resolution icons through the Shell (IShellItemImageFactory),
/// falling back to ExtractAssociatedIcon when the Shell call fails.
/// </summary>
internal static class IconService
{
    /// <summary>Default pixel size requested from the Shell (jumbo). WPF downscales with high quality.</summary>
    public const int JumboSize = 256;

    public static ImageSource? GetIcon(string path, int size = JumboSize)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return null;

        return GetIconForParsingName(path, size) ?? TryAssociatedIcon(path);
    }

    /// <summary>Icon for any shell parsing name: a file path or e.g. "shell:AppsFolder\{AppID}".</summary>
    /// <summary>Shell image for a path: a thumbnail (photos, videos, documents) or, failing that, its icon.</summary>
    public static BitmapSource? GetThumbnail(string path, int size)
    {
        Win32.IShellItemImageFactory? factory = null;
        try
        {
            Guid iid = Win32.IID_IShellItemImageFactory;
            if (Win32.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                return null;
            return FromFactory(factory, size, Win32.SIIGBF.ResizeToFit | Win32.SIIGBF.BiggerSizeOk)
                   ?? FromFactory(factory, size);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    public static ImageSource? GetIconForParsingName(string parsingName, int size = JumboSize)
    {
        Win32.IShellItemImageFactory? factory = null;
        try
        {
            Guid iid = Win32.IID_IShellItemImageFactory;
            if (Win32.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                return null;
            return FromFactory(factory, size);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Renders an icon from a shell item's image factory (frozen, usable from any thread).</summary>
    public static BitmapSource? FromFactory(Win32.IShellItemImageFactory factory, int size, Win32.SIIGBF flags = Win32.SIIGBF.IconOnly)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new Win32.SIZE(size, size), flags, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            return HBitmapToSource(hbm);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) Win32.DeleteObject(hbm);
        }
    }

    /// <summary>
    /// Copies a 32bpp HBITMAP into a WPF bitmap while keeping the alpha channel
    /// (Imaging.CreateBitmapSourceFromHBitmap drops it).
    /// </summary>
    private static BitmapSource? HBitmapToSource(IntPtr hbm)
    {
        if (Win32.GetObject(hbm, Marshal.SizeOf<Win32.BITMAP>(), out var bmp) == 0)
            return null;

        int w = bmp.bmWidth, h = bmp.bmHeight;
        if (w <= 0 || h <= 0) return null;

        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };

        int stride = w * 4;
        var pixels = new byte[stride * h];
        IntPtr hdc = Win32.CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (Win32.GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref header, Win32.DIB_RGB_COLORS) == 0)
                return null;
        }
        finally
        {
            Win32.DeleteDC(hdc);
        }

        // Analyse alpha: legacy icons may have no alpha at all; detect premultiplied vs straight alpha.
        bool anyAlpha = false, straight = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a != 0) anyAlpha = true;
            if (pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a) straight = true;
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            straight = true;
        }

        var format = straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
        BitmapSource source = BitmapSource.Create(w, h, 96, 96, format, null, pixels, stride);
        source = CropIfPadded(source, pixels, w, h);
        source.Freeze();
        return source;
    }

    /// <summary>
    /// Apps without a big icon come back as a small icon centered in a big transparent frame.
    /// Crop to the visible content (as a square) so every icon fills its slot.
    /// </summary>
    /// <summary>
    /// Bounding box of the icon's real content. Ignores faint pixels and a thin outer frame:
    /// Windows draws small legacy icons centered in a 256px frame with a faint 1px border, which
    /// would otherwise make the "content" look like it fills the whole frame.
    /// </summary>
    private static (int MinX, int MinY, int MaxX, int MaxY) ContentBounds(byte[] pixels, int w, int h)
    {
        // Solid pixels only (the faint frame and soft shadows are below 50% alpha)...
        const byte minAlpha = 128;
        int margin = w >= 64 ? 3 : 0;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = margin; y < h - margin; y++)
        {
            int row = y * w * 4;
            for (int x = margin; x < w - margin; x++)
            {
                if (pixels[row + x * 4 + 3] <= minAlpha) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return (minX, minY, maxX, maxY);

        // ...then grow a little so anti-aliased edges are not cut off.
        int pad = Math.Max(1, (int)(Math.Max(maxX - minX, maxY - minY) * 0.04));
        return (Math.Max(0, minX - pad), Math.Max(0, minY - pad), Math.Min(w - 1, maxX + pad), Math.Min(h - 1, maxY + pad));
    }

    private static BitmapSource CropIfPadded(BitmapSource source, byte[] pixels, int w, int h)
    {
        var (minX, minY, maxX, maxY) = ContentBounds(pixels, w, h);
        if (maxX < 0) return source;

        int contentW = maxX - minX + 1, contentH = maxY - minY + 1;
        int side = Math.Max(contentW, contentH);
        if (side >= w * 0.6) return source; // already fills the frame

        // Square crop centered on the content, clamped inside the bitmap.
        int cx = minX + contentW / 2, cy = minY + contentH / 2;
        int x0 = Math.Clamp(cx - side / 2, 0, w - side);
        int y0 = Math.Clamp(cy - side / 2, 0, h - side);
        return new CroppedBitmap(source, new Int32Rect(x0, y0, side, side));
    }

    /// <summary>
    /// Makes icons look consistent in a grid: trims transparent padding to the visible content,
    /// then draws it centered at <paramref name="fill"/> of a square <paramref name="outPx"/> bitmap
    /// with high-quality downscaling. Must run on an STA thread (the catalog thread is STA).
    /// </summary>
    public static BitmapSource NormalizeSquare(BitmapSource source, int outPx, double fill = 0.9)
    {
        var src = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var pixels = new byte[w * h * 4];
        src.CopyPixels(pixels, w * 4, 0);

        var (minX, minY, maxX, maxY) = ContentBounds(pixels, w, h);

        var crop = new Int32Rect(0, 0, w, h);
        int contentSide = maxX < 0 ? 0 : Math.Max(maxX - minX + 1, maxY - minY + 1);
        if (contentSide >= 8) // tiny = no real content found: keep the whole image
        {
            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            int side = Math.Max(cw, ch);
            int x0 = Math.Clamp(minX + cw / 2 - side / 2, 0, Math.Max(0, w - side));
            int y0 = Math.Clamp(minY + ch / 2 - side / 2, 0, Math.Max(0, h - side));
            crop = new Int32Rect(x0, y0, Math.Min(side, w), Math.Min(side, h));
        }

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        double size = outPx * fill, offset = (outPx - size) / 2;
        using (var dc = visual.RenderOpen())
            dc.DrawImage(new CroppedBitmap(src, crop), new Rect(offset, offset, size, size));

        var result = new RenderTargetBitmap(outPx, outPx, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    /// <summary>High-quality resize of a square icon to exactly <paramref name="px"/> pixels (no cropping).</summary>
    public static BitmapSource Resize(BitmapSource source, int px, double dpi = 96)
    {
        // Shrinking: area averaging (every target pixel = the exact average of the source pixels it
        // covers). WPF's own scaler, used in one big step (256 -> 48 px), skips pixels and leaves
        // jagged, "pixelated" edges. Enlarging (rare) keeps WPF's smooth scaler.
        if (source.PixelWidth > px && source.PixelHeight > px) return AreaDownsample(source, px, dpi);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, px * 96 / dpi, px * 96 / dpi));
        var result = new RenderTargetBitmap(px, px, dpi, dpi, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private static BitmapSource AreaDownsample(BitmapSource source, int px, double dpi)
    {
        var src = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int sw = src.PixelWidth, sh = src.PixelHeight, sstride = sw * 4;
        var s = new byte[sstride * sh];
        src.CopyPixels(s, sstride, 0);

        var d = new byte[px * px * 4];
        double fx = sw / (double)px, fy = sh / (double)px;
        var acc = new double[4];
        for (int ty = 0; ty < px; ty++)
        {
            double y0 = ty * fy, y1 = y0 + fy;
            for (int tx = 0; tx < px; tx++)
            {
                double x0 = tx * fx, x1 = x0 + fx;
                acc[0] = acc[1] = acc[2] = acc[3] = 0;
                double area = 0;
                for (int sy = (int)y0; sy < Math.Min(sh, (int)Math.Ceiling(y1)); sy++)
                {
                    double wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    if (wy <= 0) continue;
                    int row = sy * sstride;
                    for (int sx = (int)x0; sx < Math.Min(sw, (int)Math.Ceiling(x1)); sx++)
                    {
                        double w = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        if (w <= 0) continue;
                        int i = row + sx * 4;
                        acc[0] += s[i] * w; acc[1] += s[i + 1] * w; acc[2] += s[i + 2] * w; acc[3] += s[i + 3] * w;
                        area += w;
                    }
                }
                int o = (ty * px + tx) * 4;
                for (int c = 0; c < 4; c++) d[o + c] = (byte)Math.Clamp(Math.Round(acc[c] / area), 0, 255);
            }
        }
        var result = BitmapSource.Create(px, px, dpi, dpi, PixelFormats.Pbgra32, null, d, px * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Icon a window advertises (WM_GETICON, then its class icon). Used when the exe is unreadable.</summary>
    public static ImageSource? GetWindowIcon(IntPtr hwnd)
    {
        try
        {
            IntPtr hIcon = IntPtr.Zero;
            foreach (int kind in new[] { Win32.ICON_BIG, Win32.ICON_SMALL2 })
            {
                Win32.SendMessageTimeout(hwnd, Win32.WM_GETICON, new IntPtr(kind), IntPtr.Zero,
                    Win32.SMTO_ABORTIFHUNG, 100, out hIcon);
                if (hIcon != IntPtr.Zero) break;
            }
            if (hIcon == IntPtr.Zero)
                hIcon = Win32.GetClassLongPtr(hwnd, Win32.GCLP_HICON);
            if (hIcon == IntPtr.Zero) return null;

            // The window owns this HICON: copy it, never destroy it.
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryAssociatedIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }
}
