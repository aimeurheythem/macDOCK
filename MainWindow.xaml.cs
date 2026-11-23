using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MacDock.Config;
using MacDock.Controls;
using MacDock.Models;
using MacDock.Native;
using MacDock.Services;

namespace MacDock;

public partial class MainWindow : Window
{
    private const double BottomGap = 8;     // gap between dock and bottom of the work area
    private double _dockWindowWidth;

    private readonly BlurBackdrop _backdrop = new();
    private readonly DockConfig _config;
    private readonly RunningAppsService _running;
    private readonly LaunchTracker _launches;

    private readonly List<DockIconView> _pinnedViews = new();
    private readonly Dictionary<string, DockIconView> _unpinnedViews = new(PathHelper.Comparer);
    private readonly Border _separator = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
        IsHitTestVisible = false,
    };

    public MainWindow()
    {
        InitializeComponent();

        // Owned windows always stay above their owner, so the blur stays behind the icons.
        new WindowInteropHelper(this).Owner = _backdrop.Handle;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Tool window: not in Alt+Tab. No-activate: clicking the dock never steals focus.
            WindowEffects.AddExStyle(hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        };

        Loaded += (_, _) =>
        {
            InitTaskbarIntegration();
            UpdateBackdrop();
        };
        LocationChanged += (_, _) => UpdateBackdrop();
        Dock.BackgroundBoundsChanged += _ => UpdateBackdrop();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;

        AllowDrop = true;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        Drop += OnDrop;

        _config = ConfigService.Load();
        SystemSettingsWindow.Init(_config, OnSettingsChanged);
        _running = new RunningAppsService();
        _running.Changed += RefreshPanel;
        Finder.FinderWindow.WindowsChanged += () => Dispatcher.BeginInvoke(RefreshPanel);
        Finder.FinderWindow.GenieMinimize = hwnd => GenieForFinder(hwnd, minimize: true);
        Finder.FinderWindow.GenieRestore = hwnd => GenieForFinder(hwnd, minimize: false);
        _launches = new LaunchTracker(path => _running.GetWindows(path).Count > 0, () => Dock.BaseSize * 0.55);

        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            ShutdownTaskbarIntegration();
            _genie?.Dispose();
            _launches.Dispose();
            _running.Dispose();
            _backdrop.Dispose();
        };

        // Invisible until the taskbar is hidden and the dock is in its final place (see RevealDock).
        Dock.Opacity = 0;
        _backdrop.SetVisible(false);

        InitMenus();
        InitAutoHide();
        InitTrash();

        // Genie: snapshot the front window as soon as the mouse reaches the dock, so a click minimizes instantly.
        Dock.MouseEnter += (_, _) =>
        {
            if (_config.GenieEffect) Genie.SnapshotNow(WindowActivator.GetForegroundRoot());
        };
        ApplyDockAppearance();
        BuildPinnedViews();
        RefreshPanel();
    }

    // ---------------- Icons ----------------

    private void BuildPinnedViews()
    {
        _pinnedViews.Clear();
        foreach (var pinned in _config.PinnedApps)
        {
            var item = DockItemFactory.FromPinned(pinned);
            if (item != null)
                _pinnedViews.Add(CreateView(item));
        }
    }

    /// <summary>Lays out: pinned apps | separator | unpinned running apps. Reuses cached views.</summary>
    private void RefreshPanel()
    {
        var elements = new List<FrameworkElement>();
        if (_config.ShowStartButton)
            elements.Add(GetStartView());

        var pinnedTargets = new HashSet<string>(PathHelper.Comparer);
        foreach (var view in _pinnedViews)
        {
            view.IsRunning = _running.GetWindows(view.Item.TargetPath).Count > 0 || (IsFileExplorer(view.Item) && Finder.FinderWindow.AnyOpen);
            if (view.Item.TargetPath != null)
                pinnedTargets.Add(PathHelper.Normalize(view.Item.TargetPath));
            elements.Add(view);
        }

        var unpinned = _running.Apps.Where(a => !pinnedTargets.Contains(a.ExePath)).ToList();
        if (unpinned.Count > 0)
            elements.Add(_separator);

        foreach (var app in unpinned)
        {
            if (!_unpinnedViews.TryGetValue(app.ExePath, out var view))
            {
                view = CreateView(DockItemFactory.FromRunning(app.ExePath, app.Windows[0]));
                _unpinnedViews[app.ExePath] = view;
            }
            view.IsRunning = true;
            elements.Add(view);
        }

        // Drop cached views of apps that are no longer running.
        foreach (var stale in _unpinnedViews.Keys.Where(k => !unpinned.Any(a => PathHelper.Comparer.Equals(a.ExePath, k))).ToList())
            _unpinnedViews.Remove(stale);

        if (_config.ShowTrash)
        {
            elements.Add(_trashSeparator);
            elements.Add(GetTrashView());
        }

        Dock.SetItems(elements);
        Reposition(); // window width depends on the number of items
        _launches.Check();
    }

    private DockIconView CreateView(DockItem item)
    {
        var view = new DockIconView(item, Dock.BaseSize);
        view.MouseLeftButtonUp += (_, _) => OnIconClicked(view);
        view.MouseRightButtonUp += (_, e) => OnIconRightClick(view, e);
        return view;
    }

    /// <summary>
    /// Not running: launch. Running but not focused: bring its top window forward.
    /// Focused with one window: minimize. Focused with several windows: cycle to the next.
    /// </summary>
    private void OnIconClicked(DockIconView view)
    {
        var item = view.Item;
        if (item.Kind == DockItemKind.StartButton)
        {
            ToggleSpotlight(); // Finder: search apps, folders and files, like macOS Spotlight
            return;
        }
        if (item.Kind == DockItemKind.Trash)
        {
            RecycleBin.Open();
            return;
        }
        if (IsFileExplorer(item))
        {
            // File Explorer opens MacDock's Finder window (macOS look) instead of Windows' Explorer.
            try { Finder.FinderWindow.ShowFromDock(); }
            catch (Exception ex)
            {
                CrashLog.Write("Finder", ex);
                Launch(item); // never leave the user without a file browser
            }
            return;
        }

        var windows = _running.GetWindows(item.TargetPath);
        if (windows.Count == 0)
        {
            // Ignore repeated clicks while the app is still starting.
            if (!_launches.IsLaunching(view) && Launch(item))
                _launches.Begin(view);
            return;
        }

        int focused = IndexOf(windows, WindowActivator.GetForegroundRoot());
        if (focused < 0)
            ActivateWithGenie(view, WindowEnumerator.TopMost(windows));
        else if (windows.Count == 1)
            MinimizeWithGenie(view, windows[0]);
        else
            ActivateWithGenie(view, windows[(focused + 1) % windows.Count]);
    }

    // ---------------- Genie effect ----------------

    private GenieEffect? _genie;

    private GenieEffect Genie => _genie ??= new GenieEffect(() => VisualTreeHelper.GetDpi(this).DpiScaleX, IconRectForWindow);

    /// <summary>Dock icon (screen DIPs) of the app that owns <paramref name="hwnd"/>; used for title-bar minimizes.</summary>
    private Rect? IconRectForWindow(IntPtr hwnd)
    {
        var app = _running.Apps.FirstOrDefault(a => a.Windows.Contains(hwnd));
        if (app == null) return null;
        DockIconView? view = _pinnedViews.FirstOrDefault(v => PathHelper.AreSame(v.Item.TargetPath, app.ExePath));
        if (view == null) _unpinnedViews.TryGetValue(app.ExePath, out view);
        if (view == null || PresentationSource.FromVisual(view) == null) return null;
        return IconScreenRect(view);
    }

    /// <summary>Turns the Genie effect (and Windows' own minimize animation) on/off to match the setting.</summary>
    private void ApplyGenieSetting()
    {
        if (_config.GenieEffect) _ = Genie;
        else if (_genie != null)
        {
            _genie.Dispose();
            _genie = null;
        }
    }

    private async void MinimizeWithGenie(DockIconView view, IntPtr hwnd)
    {
        if (_config.GenieEffect && await Genie.MinimizeAsync(hwnd, IconScreenRect(view))) return;
        WindowActivator.Minimize(hwnd);
    }

    private async void ActivateWithGenie(DockIconView view, IntPtr hwnd)
    {
        if (_config.GenieEffect && Win32.IsIconic(hwnd) && await Genie.RestoreAsync(hwnd, IconScreenRect(view))) return;
        WindowActivator.Activate(hwnd);
    }

    /// <summary>The icon image's current (possibly magnified) rectangle in screen DIPs.</summary>
    private Rect IconScreenRect(DockIconView view)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var topLeft = view.PointToScreen(new Point(0, 0));
        return new Rect(topLeft.X / scale, topLeft.Y / scale, view.IconSize, view.IconSize);
    }

    /// <summary>Genie for MacDock's own Finder windows: into / out of the File Explorer icon.</summary>
    private async Task<bool> GenieForFinder(IntPtr hwnd, bool minimize)
    {
        if (!_config.GenieEffect) return false;
        var view = _pinnedViews.FirstOrDefault(v => IsFileExplorer(v.Item));
        if (view == null || PresentationSource.FromVisual(view) == null) return false;
        var icon = IconScreenRect(view);
        return minimize ? await Genie.MinimizeAsync(hwnd, icon) : await Genie.RestoreAsync(hwnd, icon);
    }

    /// <summary>The dock's File Explorer item (explorer.exe, pinned directly or through a shortcut).</summary>
    private static bool IsFileExplorer(DockItem item) =>
        item.Kind == DockItemKind.App &&
        ((item.TargetPath != null && string.Equals(Path.GetFileName(item.TargetPath), "explorer.exe", StringComparison.OrdinalIgnoreCase))
         || item.Path.Contains("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase));

    private static int IndexOf(IReadOnlyList<IntPtr> list, IntPtr value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    private static bool Launch(DockItem item)
    {
        if (SystemSettingsWindow.IsSettingsExe(item.TargetPath ?? item.Path) || SystemSettingsWindow.IsSettingsExe(item.Path))
        {
            SystemSettingsWindow.OpenFromShortcut();
            return true;
        }
        // Store apps (Claude, Slack, WhatsApp... pinned by their exe): Windows refuses to start the exe
        // directly, so they start through their AppID, like the Start menu does. A running app that sits
        // in the tray (no window) shows its window again this way too.
        if (PackagedAppIcon.AppIdForExe(item.TargetPath ?? item.Path) is { } appId && LaunchAppId(appId)) return true;
        string target = item.TargetPath ?? item.Path;
        // Steam in the tray: its own link brings the main window back reliably (a second steam.exe
        // sometimes only bounced in the dock and showed nothing).
        if (Path.GetFileName(target).Equals("steam.exe", StringComparison.OrdinalIgnoreCase) && Process.GetProcessesByName("steam").Length > 0)
        {
            try
            {
                Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) { CrashLog.Write("Launch steam://open/main", ex); }
        }
        try
        {
            // Started from its own folder, as Explorer and the Start menu do: some apps (launchers, games,
            // tray apps asked to show themselves again) only work right that way.
            var start = new ProcessStartInfo(item.Path) { UseShellExecute = true };
            try
            {
                string? folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) start.WorkingDirectory = folder;
            }
            catch { /* not a file path (a link, a URI) */ }
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Launch {item.Path}", ex);
            return false;
        }
    }

    private static bool LaunchAppId(string appId)
    {
        try
        {
            Process.Start(new ProcessStartInfo(@"shell:AppsFolder\" + appId) { UseShellExecute = true });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + appId) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write($"Launch {appId}", ex);
                return false;
            }
        }
    }

    // ---------------- Drag & drop pinning ----------------

    private static string[] GetDroppedApps(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return Array.Empty<string>();
        return files.Where(f => File.Exists(f) && DockItemFactory.IsSupported(f)).ToArray();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (IsOverTrash(e))
            // Link, not Move: a Move result can make Explorer delete the source itself. We recycle (undoable).
            e.Effects = GetDroppedFiles(e).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;
        else
            e.Effects = GetDroppedApps(e).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (TryDropOnTrash(e))
        {
            e.Handled = true;
            return;
        }

        var files = GetDroppedApps(e);
        if (files.Length == 0) return;

        int insertAt = GetConfigInsertIndex(e.GetPosition(Dock).X);
        foreach (var file in files)
        {
            if (IsAlreadyPinned(file)) continue;
            _config.PinnedApps.Insert(insertAt++, new PinnedApp { Path = file });
        }

        ConfigService.Save(_config);
        BuildPinnedViews();
        RefreshPanel();
        e.Handled = true;
    }

    /// <summary>Maps a drop X position (in Dock coordinates) to an index in the config list.</summary>
    private int GetConfigInsertIndex(double x)
    {
        foreach (var view in _pinnedViews)
        {
            double center = Canvas.GetLeft(view) + view.IconSize / 2;
            if (x < center && view.Item.Pinned is { } pinned)
                return _config.PinnedApps.IndexOf(pinned);
        }
        return _config.PinnedApps.Count;
    }

    private bool IsAlreadyPinned(string path) =>
        _config.PinnedApps.Any(p => PathHelper.AreSame(p.Path, path));

    // ---------------- Positioning ----------------

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.WorkArea)) return;
        Dispatcher.BeginInvoke(() =>
        {
            // Someone (usually Explorer) reset the work area: re-apply ours off the UI thread.
            if (TaskbarManager.IsHidden) Task.Run(TaskbarManager.Enforce);
            Reposition();
        });
    }

    /// <summary>Sizes the window for the magnified dock and centers it at the bottom of the work area.</summary>
    private void Reposition()
    {
        // With the taskbar hidden, the work area excludes the dock's own strip: use the full screen.
        var area = TaskbarManager.IsHidden
            ? new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight)
            : SystemParameters.WorkArea;
        // Grow-only, with room for a few more icons: resizing a transparent window makes it flash,
        // so apps opening/closing must never resize it.
        double needed = Dock.RequiredWidth;
        if (needed > _dockWindowWidth || _dockWindowWidth > area.Width)
            _dockWindowWidth = Math.Min(area.Width, needed + 6 * (Dock.BaseSize + 2 * Dock.Spacing));
        Width = _dockWindowWidth;
        Height = Dock.RequiredHeight;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - BottomGap;
        UpdateBackdrop();
    }

    /// <summary>Puts the blur window exactly behind the dock background (device pixels).</summary>
    private void UpdateBackdrop()
    {
        if (PresentationSource.FromVisual(Dock) == null) return;
        var bg = Dock.BackgroundBounds;
        if (bg.IsEmpty || bg.Width <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        Point topLeft = Dock.PointToScreen(bg.TopLeft); // includes the auto-hide slide offset
        int x = (int)Math.Round(topLeft.X), y = (int)Math.Round(topLeft.Y);
        int w = (int)Math.Round(bg.Width * dpi.DpiScaleX), h = (int)Math.Round(bg.Height * dpi.DpiScaleY);
        _bgScreenRect = new Win32.RECT(x, y, x + w, y + h);
        _backdrop.Place(x, y, w, h, (int)Math.Round(Dock.BackgroundCornerRadius * dpi.DpiScaleX));
        PlaceFrost();
    }
}
