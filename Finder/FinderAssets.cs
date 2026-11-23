using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MacDock.Services;
using Microsoft.Win32;

namespace MacDock.Finder;

/// <summary>Shared look and helpers of the Finder windows: folder icons, file kinds, sorting, theme.</summary>
internal static class FinderAssets
{
    public static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // ---------------- Theme ----------------

    /// <summary>Windows "app mode" is dark.</summary>
    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- Known folders ----------------

    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Desktop { get; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string Documents { get; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static string Pictures { get; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static string Music { get; } = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public static string Videos { get; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public static string Downloads { get; } = KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")) ?? Path.Combine(Home, "Downloads");
    public static string Applications { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Symbol (Segoe Fluent Icons) for a well-known folder, or null.</summary>
    public static string? FolderGlyph(string path)
    {
        if (Same(path, Desktop)) return "";
        if (Same(path, Documents)) return "";
        if (Same(path, Downloads)) return "";
        if (Same(path, Pictures)) return "";
        if (Same(path, Music)) return "";
        if (Same(path, Videos)) return "";
        if (Same(path, Home)) return "";
        if (Same(path, Applications)) return "";
        return null;
    }

    public static bool Same(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder name as Finder shows it: drives as "Local Disk (C:)", the profile as the user name.</summary>
    public static string DisplayName(string path)
    {
        if (path == FinderWindow.RecentsLocation) return "Recents";
        var root = Path.GetPathRoot(path);
        if (root != null && Same(root, path)) return DriveLabel(root);
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    public static string DriveLabel(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            string letter = root.TrimEnd('\\');
            string label = drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.VolumeLabel
                : drive.DriveType == DriveType.Removable ? "USB Drive"
                : drive.DriveType == DriveType.Network ? "Network Drive"
                : drive.DriveType == DriveType.CDRom ? "CD Drive"
                : "Local Disk";
            return $"{label} ({letter})";
        }
        catch
        {
            return root;
        }
    }

    // ---------------- macOS-style folder icons ----------------

    private static readonly ConcurrentDictionary<string, DrawingImage> FolderIcons = new();

    /// <summary>The light-blue macOS folder, with the folder's symbol embossed for well-known folders.</summary>
    public static DrawingImage FolderIcon(string? path = null)
    {
        string glyph = path == null ? "" : FolderGlyph(path) ?? "";
        return FolderIcons.GetOrAdd(glyph, CreateFolderIcon);
    }

    private static DrawingImage CreateFolderIcon(string glyph)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 256, 256));

            // Back panel with the tab.
            var back = Geometry.Parse("M 18,62 L 18,52 Q 18,36 34,36 L 92,36 Q 104,36 111,45 L 118,54 L 222,54 Q 238,54 238,70 L 238,204 Q 238,220 222,220 L 34,220 Q 18,220 18,204 Z");
            dc.DrawGeometry(Gradient(Color.FromRgb(0x38, 0x9E, 0xEC), Color.FromRgb(0x1C, 0x7E, 0xD8)), null, back);
            // Shadow the front panel casts on the back one.
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0x38, 0x00, 0x30, 0x80), Color.FromArgb(0, 0x00, 0x30, 0x80), 90), null, new Rect(18, 66, 220, 12));

            // Front panel: bright, glassy blue (the newer macOS folder).
            var front = new RectangleGeometry(new Rect(18, 76, 220, 144), 16, 16);
            var frontFill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x92, 0xD7, 0xFD), 0),
                    new GradientStop(Color.FromRgb(0x62, 0xBF, 0xF8), 0.45),
                    new GradientStop(Color.FromRgb(0x3B, 0xA4, 0xF0), 1),
                },
            };
            frontFill.Freeze();
            dc.DrawGeometry(frontFill, null, front);
            // Glass rim: bright along the top edge, fading down the sides.
            var rim = new LinearGradientBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 90);
            dc.DrawGeometry(null, new Pen(rim, 2.2), new RectangleGeometry(new Rect(19.5, 77.5, 217, 141), 15, 15));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0x08, 0x50, 0xA8)), 1.2), front);

            if (glyph.Length > 0)
            {
                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(SymbolFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 64,
                    new SolidColorBrush(Color.FromArgb(0xB8, 0x17, 0x78, 0xCC)), 1);
                dc.DrawText(text, new Point(128 - text.Width / 2, 150 - text.Height / 2));
            }
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static LinearGradientBrush Gradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    // ---------------- File kinds ----------------

    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    private const uint SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>"PNG File", "Application", "Microsoft Word Document"... (cached per extension).</summary>
    public static string TypeName(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (ext.Length == 0) return "Document";
        return TypeNames.GetOrAdd(ext, e =>
        {
            try
            {
                var info = new SHFILEINFO();
                SHGetFileInfo("file" + e, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
                if (!string.IsNullOrWhiteSpace(info.szTypeName)) return info.szTypeName;
            }
            catch
            {
                // Fall through.
            }
            return e.TrimStart('.').ToUpperInvariant() + " File";
        });
    }

    // ---------------- Sorting ----------------

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Natural order, like Finder: "file 2" before "file 10".</summary>
    public static int NaturalCompare(string a, string b)
    {
        try { return StrCmpLogicalW(a, b); }
        catch { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
    }

    // ---------------- Icons ----------------

    private static readonly HashSet<string> UniqueIconTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".cur", ".msc", ".scr",
    };

    private static readonly HashSet<string> ThumbnailTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".jfif", ".avif",
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".webm", ".pdf", ".psd", ".svg",
    };

    public static bool HasThumbnail(string path) => ThumbnailTypes.Contains(Path.GetExtension(path));

    private static readonly ConcurrentDictionary<string, ImageSource> TypeIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icon or thumbnail for a file (runs on the loader thread).</summary>
    public static ImageSource? LoadFileIcon(string path, int px, bool thumbnails)
    {
        string ext = Path.GetExtension(path);
        if (thumbnails && ThumbnailTypes.Contains(ext))
        {
            var thumb = IconService.GetThumbnail(path, px);
            if (thumb != null) return thumb;
        }
        if (UniqueIconTypes.Contains(ext) || ext.Length == 0)
            return IconService.GetIcon(path, px);

        string key = ext + "|" + px;
        if (TypeIcons.TryGetValue(key, out var cached)) return cached;
        var icon = IconService.GetIcon(path, px);
        if (icon != null) TypeIcons[key] = icon;
        return icon;
    }
}

/// <summary>
/// Loads file icons and thumbnails on one background STA thread (shell image APIs are COM) and hands
/// them to the UI as they arrive, so folders open instantly and fill in their icons.
/// </summary>
internal sealed class IconLoader : IDisposable
{
    private readonly BlockingCollection<(FileEntry Entry, int Generation, int Px, bool Thumbs)> _queue = new();
    private readonly Dispatcher _ui;
    private int _generation;

    public IconLoader(Dispatcher ui)
    {
        _ui = ui;
        var thread = new Thread(Run) { IsBackground = true, Name = "Finder icons", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Drops every pending request (the folder changed).</summary>
    public void Reset() => Interlocked.Increment(ref _generation);

    public void Request(FileEntry entry, int px, bool thumbnails)
    {
        if (entry.IsDirectory)
        {
            entry.Icon = FinderAssets.FolderIcon(entry.FullPath);
            return;
        }
        if (!_queue.IsAddingCompleted) _queue.Add((entry, _generation, px, thumbnails));
    }

    private void Run()
    {
        try
        {
            foreach (var (entry, generation, px, thumbs) in _queue.GetConsumingEnumerable())
            {
                if (generation != Volatile.Read(ref _generation)) continue;
                ImageSource? icon = null;
                try { icon = FinderAssets.LoadFileIcon(entry.FullPath, px, thumbs); }
                catch { /* keep the placeholder */ }
                if (icon == null) continue;
                if (icon.CanFreeze && !icon.IsFrozen) icon.Freeze();
                _ui.BeginInvoke(() => entry.Icon = icon, DispatcherPriority.Background);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
