using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacDock.Services;

/// <summary>
/// Tells whether the wallpaper behind the (transparent) menu bar is dark, so the bar can use white
/// text like macOS does on dark wallpapers, and black text on light ones.
///
/// The menu bar strip is reserved, so only the wallpaper is ever behind it. Windows keeps a copy of the
/// current wallpaper in %APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper (also for slideshows and
/// Spotlight); its top rows are what sits behind the bar. Solid-color desktops use the desktop color.
/// </summary>
internal static class WallpaperTone
{
    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    private const int COLOR_DESKTOP = 1;

    /// <summary>Average perceived brightness (0..1) of the strip behind the menu bar. Call off the UI thread.</summary>
    public static double TopBrightness() => Brightness(0, 0.035);

    /// <summary>
    /// Average brightness (0..1) of a horizontal band of the wallpaper, from <paramref name="top"/> to
    /// <paramref name="bottom"/> (fractions of the height). The small decoded copy is cached until the
    /// wallpaper file changes, so this is fast after the first call.
    /// </summary>
    public static double Brightness(double top, double bottom)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        try
        {
            if (File.Exists(file)) return FromImage(file, top, bottom);
        }
        catch
        {
            // Fall back to the desktop color.
        }
        uint c = GetSysColor(COLOR_DESKTOP); // 0x00BBGGRR
        return Luminance((byte)c, (byte)(c >> 8), (byte)(c >> 16));
    }

    private static readonly object CacheGate = new();
    private static DateTime _cachedStamp;
    private static byte[]? _cachedPixels;
    private static int _cachedW, _cachedH;

    private static double FromImage(string file, double top, double bottom)
    {
        byte[] pixels;
        int w, h;
        lock (CacheGate)
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_cachedPixels == null || stamp != _cachedStamp)
            {
                BitmapSource image;
                using (var stream = new MemoryStream(File.ReadAllBytes(file)))
                    image = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                // Small copy: plenty to average, cheap to read.
                double scale = 320.0 / image.PixelWidth;
                var small = new FormatConvertedBitmap(new TransformedBitmap(image, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);
                _cachedW = small.PixelWidth;
                _cachedH = small.PixelHeight;
                _cachedPixels = new byte[_cachedW * 4 * _cachedH];
                small.CopyPixels(_cachedPixels, _cachedW * 4, 0);
                _cachedStamp = stamp;
            }
            pixels = _cachedPixels;
            w = _cachedW;
            h = _cachedH;
        }

        int y0 = Math.Clamp((int)(h * top), 0, h - 1);
        int y1 = Math.Clamp((int)Math.Ceiling(h * bottom), y0 + 1, h);
        double sum = 0;
        int count = 0;
        for (int y = y0; y < y1; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                sum += Luminance(pixels[i + 2], pixels[i + 1], pixels[i]);
                count++;
            }
        return count == 0 ? 0.5 : sum / count;
    }

    private static double Luminance(byte r, byte g, byte b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
}
