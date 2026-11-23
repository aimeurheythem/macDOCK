using MacDock.Models;

namespace MacDock.Config;

/// <summary>Root object of %APPDATA%\MacDock\config.json.</summary>
public sealed class DockConfig
{
    public List<PinnedApp> PinnedApps { get; set; } = new();

    /// <summary>Hide the Windows taskbar while MacDock runs (restored on exit).</summary>
    public bool HideTaskbar { get; set; } = true;

    /// <summary>Icon size in DIPs when not magnified.</summary>
    public double IconSize { get; set; } = 48;

    /// <summary>Size of the hovered icon. Equal to <see cref="IconSize"/> means magnification off.</summary>
    public double MagnifiedSize { get; set; } = 96;

    /// <summary>Dock background tint, "#RRGGBB".</summary>
    public string DockColor { get; set; } = "#FFFFFF";

    /// <summary>Opacity of the background tint, 0..1 (the blur is always behind it).</summary>
    public double DockOpacity { get; set; } = 0.25;

    /// <summary>Slide the dock off-screen until the mouse touches the bottom edge.</summary>
    public bool AutoHide { get; set; }

    /// <summary>Show the Start button as the first dock icon.</summary>
    public bool ShowStartButton { get; set; } = true;

    /// <summary>macOS "Genie" effect when minimizing / restoring windows from the dock.</summary>
    public bool GenieEffect { get; set; } = true;

    /// <summary>macOS-style menu bar across the top of the screen (app menu, Wi-Fi, sound, clock...).</summary>
    public bool ShowMenuBar { get; set; } = true;

    /// <summary>Show the Trash as the last dock icon.</summary>
    public bool ShowTrash { get; set; } = true;

    /// <summary>A lone press of the Windows key opens MacDock's Start menu instead of the Windows one.</summary>
    public bool WinKeyOpensStart { get; set; } = true;

    /// <summary>macOS red / yellow / green window buttons on every app window (over Windows' own buttons).</summary>
    public bool MacWindowButtons { get; set; } = true;

    /// <summary>The one-time offer to make every Windows folder blue has been shown.</summary>
    public bool FolderIconsOffered { get; set; }

    /// <summary>Folders on the desktop get the blue macOS folder (their own icon, so Windows 10 shows no yellow preview).</summary>
    public bool BlueDesktopFolders { get; set; } = true;

    /// <summary>Folders open in MacDock's Finder instead of File Explorer (desktop, Win+E, "show in folder"...).</summary>
    public bool FinderOpensFolders { get; set; } = true;

    /// <summary>macOS-style mouse pointers (arrow, hand, text, spinning wheel...) for the whole system.</summary>
    public bool MacCursors { get; set; } = true;

    /// <summary>The Windows Settings app opens MacDock's macOS-style System Settings instead.</summary>
    public bool MacSystemSettings { get; set; } = true;

    /// <summary>The one-time offer to make Windows faster (Gaming &amp; Performance) has been shown.</summary>
    public bool PerformanceOffered { get; set; }

    /// <summary>MacDock's macOS-style wallpaper has been put on the desktop once (the user's own is kept in <see cref="PreviousWallpaper"/>).</summary>
    public bool MacWallpaperApplied { get; set; }

    /// <summary>The wallpaper the user had before MacDock's, to put it back from System Settings.</summary>
    public string? PreviousWallpaper { get; set; }

    /// <summary>Apps pinned in the Start menu. Null = not set up yet (defaults are chosen on first open).</summary>
    public List<PinnedApp>? StartPins { get; set; }

    public const double MinIconSize = 32, MaxIconSize = 96, MaxMagnifiedSize = 160;

    /// <summary>Clamps values edited by hand in config.json into safe ranges.</summary>
    public void Normalize()
    {
        IconSize = Math.Clamp(IconSize, MinIconSize, MaxIconSize);
        MagnifiedSize = Math.Clamp(MagnifiedSize, IconSize, MaxMagnifiedSize);
        DockOpacity = Math.Clamp(DockOpacity, 0, 1);
        if (string.IsNullOrWhiteSpace(DockColor)) DockColor = "#FFFFFF";
    }

    public static DockConfig CreateDefault() => new()
    {
        PinnedApps =
        {
            new PinnedApp { Path = @"C:\Windows\explorer.exe" },
            new PinnedApp { Path = @"C:\Program Files\Google\Chrome\Application\chrome.exe" },
            new PinnedApp { Path = @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe" },
            new PinnedApp { Path = @"%LOCALAPPDATA%\Programs\antigravity\Antigravity.exe" },
        },
    };
}
