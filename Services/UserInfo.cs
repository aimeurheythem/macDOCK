using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MacDock.Services;

/// <summary>
/// Current user's name and picture. The picture is the one chosen in MacDock's System Settings (a photo
/// or a macOS-style monogram), else the Windows account picture, else a monogram made from the name.
/// </summary>
internal static class UserInfo
{
    public static string UserName => Environment.UserName;

    /// <summary>The picture chosen in MacDock (Users &amp; Groups), saved as a 512 px PNG.</summary>
    public static string AvatarFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "avatar.png");

    /// <summary>The monogram colors macOS offers (top and bottom of the gradient).</summary>
    public static readonly (uint Top, uint Bottom)[] MonogramColors =
    {
        (0xA1A1A6, 0x6E6E73), (0x6AB0FF, 0x1E6FE0), (0xB98CFF, 0x7A3FE0), (0xFF8FC8, 0xE0458E),
        (0xFF8A80, 0xE0403A), (0xFFC163, 0xF08A1C), (0x9BE07A, 0x3FA84A), (0x7DE3E0, 0x1FA3B0),
    };

    /// <summary>Raised (on the UI thread) when the picture changes, so every place showing it refreshes.</summary>
    public static event Action? PictureChanged;

    /// <summary>The user's picture; never null (falls back to a monogram).</summary>
    public static ImageSource? LoadAccountPicture(int decodePixels = 96)
    {
        if (Load(AvatarFile, decodePixels) is { } own) return own;
        if (WindowsPicture(decodePixels) is { } windows) return windows;
        try { return Monogram(Initials(), 0, Math.Max(64, decodePixels)); }
        catch { return null; }
    }

    private static ImageSource? WindowsPicture(int decodePixels)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return null;
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
            if (key == null) return null;
            foreach (var name in new[] { "Image192", "Image208", "Image240", "Image96", "Image448" })
                if (key.GetValue(name) is string path && Load(path, decodePixels) is { } image) return image;
        }
        catch { }
        return null;
    }

    private static ImageSource? Load(string path, int decodePixels)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = decodePixels;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    /// <summary>One or two letters from the user's name ("Heythem Aimeur" gives "HA").</summary>
    public static string Initials()
    {
        string name = WindowsSettings.FullUserName();
        var parts = name.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        string letters = parts.Length >= 2 ? $"{parts[0][0]}{parts[^1][0]}" : name.Length > 0 ? name[..1] : "?";
        return letters.ToUpper(CultureInfo.CurrentCulture);
    }

    /// <summary>A macOS monogram: white initials on a round color gradient.</summary>
    public static BitmapSource Monogram(string letters, int colorIndex, int pixels)
    {
        var (top, bottom) = MonogramColors[Math.Clamp(colorIndex, 0, MonogramColors.Length - 1)];
        Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double r = pixels / 2.0;
            dc.DrawEllipse(new LinearGradientBrush(C(top), C(bottom), 90), null, new Point(r, r), r, r);
            var text = new FormattedText(letters, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(MacFonts.Text, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), pixels * (letters.Length > 1 ? 0.4 : 0.48),
                Brushes.White, 1.0);
            dc.DrawText(text, new Point(r - text.Width / 2, r - text.Height / 2));
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Saves a photo (cropped to a centered square) as the user's picture.</summary>
    public static void SetPhoto(string file)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.UriSource = new Uri(file);
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.EndInit();
        int side = Math.Min(source.PixelWidth, source.PixelHeight);
        var square = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        BitmapSource result = side > 512 ? new TransformedBitmap(square, new ScaleTransform(512.0 / side, 512.0 / side)) : square;
        Save(result);
    }

    public static void SetMonogram(int colorIndex) => Save(Monogram(Initials(), colorIndex, 512));

    /// <summary>Back to the Windows account picture (or the default monogram).</summary>
    public static void ResetPicture()
    {
        try { if (File.Exists(AvatarFile)) File.Delete(AvatarFile); } catch { }
        PictureChanged?.Invoke();
    }

    private static void Save(BitmapSource image)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AvatarFile)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(AvatarFile)) encoder.Save(stream);
        PictureChanged?.Invoke();
    }
}
