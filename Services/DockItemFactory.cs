using System.Diagnostics;
using System.IO;
using MacDock.Models;

namespace MacDock.Services;

/// <summary>Builds <see cref="DockItem"/>s (name, icon, target) from .exe or .lnk paths.</summary>
internal static class DockItemFactory
{
    private static readonly string[] SupportedExtensions = { ".exe", ".lnk" };

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>True for shell paths like "shell:AppsFolder\{AppID}" (apps pinned from the Start menu).</summary>
    public static bool IsShellPath(string path) => path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    public static DockItem? FromPinned(PinnedApp pinned)
    {
        var path = pinned.ExpandedPath();
        if (IsShellPath(path))
        {
            return new DockItem
            {
                Name = pinned.Name ?? "App",
                Path = path,
                TargetPath = pinned.Target,
                Icon = IconStyler.RoundSquareCorners(Custom(pinned.Name, pinned.Target)
                       ?? MacFolder(pinned.Name, pinned.Target ?? path)
                       ?? SystemIcons.TryRender(pinned.Name, pinned.Target, path, IconService.JumboSize)
                       ?? ShellPathIcon(path)),
                Pinned = pinned,
            };
        }

        if (!File.Exists(path) || !IsSupported(path))
            return null;

        bool isShortcut = ShortcutResolver.IsShortcut(path);
        string? target = isShortcut ? ShortcutResolver.GetTarget(path) : path;

        return new DockItem
        {
            Name = GetDisplayName(path, isShortcut),
            Path = path,
            TargetPath = target,
            Icon = IconStyler.RoundSquareCorners(Custom(GetDisplayName(path, isShortcut), target)
                   ?? MacFolder(GetDisplayName(path, isShortcut), target)
                   ?? SystemIcons.TryRender(null, target, null, IconService.JumboSize)
                   ?? IconService.GetIcon(path)),
            Pinned = pinned,
        };
    }

    /// <summary>User icon from the Icons folder, scaled to the dock's master size.</summary>
    private static System.Windows.Media.ImageSource? Custom(string? name, string? exePath) =>
        CustomIcons.TryLoad(name, exePath) is { } image
            ? IconService.NormalizeSquare(image, IconService.JumboSize, 0.92)
            : null;

    /// <summary>File Explorer shows the blue macOS folder (it opens MacDock's Finder window).</summary>
    private static System.Windows.Media.ImageSource? MacFolder(string? name, string? target)
    {
        bool explorer = (target != null && (Path.GetFileName(target).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                                            || target.Contains("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase)))
                        || string.Equals(name?.Trim(), "File Explorer", StringComparison.OrdinalIgnoreCase);
        if (!explorer) return null;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(Finder.FinderAssets.FolderIcon(), new System.Windows.Rect(0, 0, IconService.JumboSize, IconService.JumboSize));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(IconService.JumboSize, IconService.JumboSize, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Packaged apps use their real package logo; anything else the shell's icon.</summary>
    private static System.Windows.Media.ImageSource? ShellPathIcon(string path)
    {
        const string prefix = @"shell:AppsFolder\";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = path[prefix.Length..];
            if (PackagedAppIcon.IsPackagedAppId(id) && PackagedAppIcon.TryLoad(id, IconService.JumboSize) is { } logo)
                return logo;
        }
        return IconService.GetIconForParsingName(path);
    }

    /// <summary>Item for a running app that is not pinned.</summary>
    public static DockItem FromRunning(string exePath, IntPtr anyWindow)
    {
        return new DockItem
        {
            Name = GetDisplayName(exePath, isShortcut: false),
            Path = exePath,
            TargetPath = exePath,
            Icon = IconStyler.RoundSquareCorners(Custom(GetDisplayName(exePath, isShortcut: false), exePath)
                   ?? MacFolder(null, exePath)
                   ?? SystemIcons.TryRender(null, exePath, null, IconService.JumboSize)
                   ?? IconService.GetIcon(exePath) ?? IconService.GetWindowIcon(anyWindow)),
            Pinned = null,
        };
    }

    /// <summary>Shortcuts use their file name (like the Start menu); exes use their FileDescription.</summary>
    private static string GetDisplayName(string path, bool isShortcut)
    {
        if (!isShortcut)
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
            }
            catch
            {
                // Fall through to the file name.
            }
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
