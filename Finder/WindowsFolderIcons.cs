using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MacDock.Finder;

/// <summary>
/// Makes every folder in Windows (desktop, File Explorer, open/save dialogs) use the blue macOS folder.
///
/// How: the folder is written as a multi-size .ico to C:\ProgramData\MacDock, and Windows' own
/// "Shell Icons" override (HKLM\...\Explorer\Shell Icons, entries 3 = folder and 4 = open folder) is
/// pointed at it. That key is machine-wide, so Windows asks for administrator permission once.
/// Turning it off deletes the two entries, which restores the standard yellow folders.
/// </summary>
internal static class WindowsFolderIcons
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";

    public static string IconPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacDock", "MacFolder.ico");

    private static string RegistryValue => IconPath + ",0";

    /// <summary>True when Windows is currently set to use the blue folder.</summary>
    public static bool IsApplied()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue("3") is string value && string.Equals(value, RegistryValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Turns the blue folders on or off (Windows shows its permission prompt). Returns false if the
    /// prompt was declined or the change failed.
    /// </summary>
    public static async Task<bool> SetAsync(bool on)
    {
        try
        {
            if (on) WriteIcon();
            string key = $"\"HKLM\\{KeyPath}\"";
            string args = on
                ? $"/c reg add {key} /v 3 /t REG_SZ /d \"{RegistryValue}\" /f && reg add {key} /v 4 /t REG_SZ /d \"{RegistryValue}\" /f"
                : $"/c reg delete {key} /v 3 /f & reg delete {key} /v 4 /f";
            var start = new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas", // administrator: HKLM
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process == null) return false;
            await process.WaitForExitAsync();
            await Task.Run(RefreshExplorer);
            return on ? IsApplied() : !IsApplied();
        }
        catch (Win32Exception)
        {
            return false; // permission prompt declined
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Folder icons failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Keeps the icon file current (e.g. after an update) when the blue folders are on.</summary>
    public static void RefreshIconFileIfApplied()
    {
        try
        {
            if (IsApplied()) WriteIcon();
        }
        catch
        {
            // The old file still works.
        }
    }

    // ---------------- Icon file ----------------

    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    /// <summary>Writes the blue folder as a .ico with every size Windows uses.</summary>
    public static void WriteIcon() => WriteIconTo(IconPath);

    /// <summary>Writes the blue folder .ico to <paramref name="iconPath"/> (UI thread: it renders with WPF).</summary>
    public static void WriteIconTo(string iconPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        var images = Sizes.Select(px => (px, Data: px >= 256 ? Png(Render(px)) : Dib(Render(px)))).ToList();

        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);            // reserved
            w.Write((short)1);            // type: icon
            w.Write((short)images.Count); // image count
            int offset = 6 + 16 * images.Count;
            foreach (var (px, data) in images)
            {
                w.Write((byte)(px >= 256 ? 0 : px));
                w.Write((byte)(px >= 256 ? 0 : px));
                w.Write((byte)0);   // palette
                w.Write((byte)0);   // reserved
                w.Write((short)1);  // planes
                w.Write((short)32); // bits per pixel
                w.Write(data.Length);
                w.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in images) w.Write(data);
        }
        // Write next to it, then swap, so Explorer never reads a half-written file.
        string temp = iconPath + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, iconPath, overwrite: true);
    }

    private static BitmapSource Render(int px)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            // The folder art sits in a 256 box; small sizes use a little more of the square.
            double grow = px <= 32 ? 1.06 : 1.0;
            double size = px * grow, offset = (px - size) / 2;
            dc.DrawImage(FinderAssets.FolderIcon(), new Rect(offset, offset, size, size));
        }
        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Classic icon image: BITMAPINFOHEADER, bottom-up BGRA pixels, empty AND mask.</summary>
    private static byte[] Dib(BitmapSource image)
    {
        var straight = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int px = straight.PixelWidth, stride = px * 4;
        var pixels = new byte[stride * px];
        straight.CopyPixels(pixels, stride, 0);
        int maskStride = ((px + 31) / 32) * 4;

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(40);          // header size
        w.Write(px);          // width
        w.Write(px * 2);      // height (color + mask)
        w.Write((short)1);    // planes
        w.Write((short)32);   // bits
        w.Write(0);           // BI_RGB
        w.Write(stride * px + maskStride * px);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = px - 1; y >= 0; y--) w.Write(pixels, y * stride, stride);
        w.Write(new byte[maskStride * px]);
        return stream.ToArray();
    }

    // ---------------- Telling Explorer ----------------

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    private static void RefreshExplorer()
    {
        try
        {
            // Rebuild the icon cache and tell every Explorer window that icons changed.
            var ie4 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ie4uinit.exe");
            if (File.Exists(ie4))
            {
                using var p = Process.Start(new ProcessStartInfo(ie4, Environment.OSVersion.Version.Build >= 22000 ? "-show" : "-ClearIconCache")
                { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(8000);
            }
        }
        catch
        {
            // Not critical: the notification below usually suffices.
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
