using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MacDock.Services;

/// <summary>
/// A pre-blurred copy of the wallpaper strip at the bottom of the primary screen: what is behind the
/// dock. Windows 11 can only round its own blur by 8 px, so there the dock draws this frosted image
/// itself, clipped to the real macOS radius (one clean, fully rounded shape).
/// Thread-safe; call off the UI thread (decoding the wallpaper takes a moment).
/// </summary>
internal static class WallpaperFrost
{
    private const int Downscale = 8;     // the strip is kept 8x smaller: soft when stretched back
    private const int BlurPasses = 3, BlurRadius = 2;

    /// <param name="monitor">Primary monitor rectangle (device pixels).</param>
    /// <param name="stripHeight">Height of the strip, from the bottom of the monitor (device pixels).</param>
    public static BitmapSource? CreateBottomStrip(Int32Rect monitor, int stripHeight)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        if (!File.Exists(file) || monitor.Width <= 0 || monitor.Height <= 0) return null;
        try
        {
            BitmapSource image;
            using (var stream = new MemoryStream(File.ReadAllBytes(file)))
                image = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];

            // Where the wallpaper lands on the monitor (Fill, Fit, Stretch or Center).
            double iw = image.PixelWidth, ih = image.PixelHeight, mw = monitor.Width, mh = monitor.Height;
            double sx, sy;
            switch (WallpaperStyle())
            {
                case "2": sx = mw / iw; sy = mh / ih; break;                          // Stretch
                case "6": sx = sy = Math.Min(mw / iw, mh / ih); break;                // Fit
                case "0": sx = sy = 1; break;                                         // Center / Tile
                default: sx = sy = Math.Max(mw / iw, mh / ih); break;                 // Fill (default), Span
            }
            double ox = (mw - iw * sx) / 2, oy = (mh - ih * sy) / 2;

            // The strip, in image pixels.
            stripHeight = Math.Clamp(stripHeight, 1, monitor.Height);
            double top = (mh - stripHeight - oy) / sy, left = -ox / sx;
            int x0 = (int)Math.Clamp(Math.Floor(left), 0, iw - 1), y0 = (int)Math.Clamp(Math.Floor(top), 0, ih - 1);
            int x1 = (int)Math.Clamp(Math.Ceiling((mw - ox) / sx), x0 + 1, iw), y1 = (int)Math.Clamp(Math.Ceiling((mh - oy) / sy), y0 + 1, ih);
            var crop = new CroppedBitmap(image, new Int32Rect(x0, y0, x1 - x0, y1 - y0));

            int tw = Math.Max(8, monitor.Width / Downscale), th = Math.Max(4, stripHeight / Downscale);
            var small = new FormatConvertedBitmap(
                new TransformedBitmap(crop, new ScaleTransform(tw / (double)crop.PixelWidth, th / (double)crop.PixelHeight)),
                PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            small.CopyPixels(pixels, stride, 0);
            for (int i = 0; i < BlurPasses; i++)
            {
                BoxBlur(pixels, w, h, BlurRadius, horizontal: true);
                BoxBlur(pixels, w, h, BlurRadius, horizontal: false);
            }
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // opaque
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string WallpaperStyle()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("WallpaperStyle") as string ?? "10";
        }
        catch
        {
            return "10";
        }
    }

    /// <summary>One box-blur pass along rows or columns (edges clamp).</summary>
    private static void BoxBlur(byte[] p, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        var line = new int[length * 3];
        for (int l = 0; l < lines; l++)
        {
            int Index(int k) => horizontal ? (l * w + k) * 4 : (k * w + l) * 4;
            for (int k = 0; k < length; k++)
            {
                int i = Index(k);
                line[k * 3] = p[i]; line[k * 3 + 1] = p[i + 1]; line[k * 3 + 2] = p[i + 2];
            }
            for (int k = 0; k < length; k++)
            {
                int b = 0, g = 0, rr = 0, n = 0;
                for (int d = -r; d <= r; d++)
                {
                    int q = Math.Clamp(k + d, 0, length - 1) * 3;
                    b += line[q]; g += line[q + 1]; rr += line[q + 2]; n++;
                }
                int i = Index(k);
                p[i] = (byte)(b / n); p[i + 1] = (byte)(g / n); p[i + 2] = (byte)(rr / n);
            }
        }
    }
}
