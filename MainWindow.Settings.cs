using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MacDock.Config;
using MacDock.Services;

namespace MacDock;

/// <summary>Applying settings (live), the startup reveal, and the settings window.</summary>
public partial class MainWindow
{
    private DispatcherTimer? _behaviorDebounce;
    private bool _revealed;

    /// <summary>Sizes and colors: cheap, applied immediately.</summary>
    private void ApplyDockAppearance()
    {
        Dock.BaseSize = _config.IconSize;
        Dock.MaxSize = Math.Max(_config.MagnifiedSize, _config.IconSize);

        var color = SettingsWindow.TryParseColor(_config.DockColor, out var c) ? c : Colors.White;
        Dock.SetBackgroundTint(color, _config.DockOpacity);
        _menuBar?.SetAppearance(color, _config.DockOpacity);
        Dock.ResetSizes();
    }

    /// <summary>
    /// Taskbar hiding, reserved strip, auto-hide. Changing the work area broadcasts a message to
    /// every window (slow, can block), so it runs on a background thread.
    /// </summary>
    private async void ApplyBehavior()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            if (_config.HideTaskbar)
            {
                int reserve = ReservePixels();
                var hwnd = _hwnd;
                await Task.Run(() => TaskbarManager.Hide(reserve, hwnd, AppBarMessage));
                TaskbarManager.StartWatching();
            }
            else
            {
                TaskbarManager.StopWatching();
                await Task.Run(TaskbarManager.Restore);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("ApplyBehavior", ex);
        }

        SetAutoHide(_config.AutoHide);
        Reposition();
        RevealDock();
    }

    /// <summary>
    /// The dock starts invisible; once the taskbar is gone and the dock is in its final place,
    /// it fades in once. No slide: moving the layered dock and the blur window together during
    /// startup can drift by a frame and look jagged.
    /// </summary>
    private void RevealDock()
    {
        if (_revealed) return;
        _revealed = true;

        _backdrop.SetVisible(true);
        Dock.Opacity = 1;

        if (_config.AutoHide)
        {
            _hideOffset = _hideTarget = HiddenOffset;
            _dockShift.Y = _hideOffset;
            UpdateBackdrop();
            return;
        }

        _hideOffset = _hideTarget = 0;
        _dockShift.Y = 0;
        UpdateBackdrop();
        Dock.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    /// <summary>Safety net: show the dock even if the taskbar step hangs (e.g. a frozen app delays the broadcast).</summary>
    private void ScheduleRevealFallback()
    {
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            if (_revealed) return;
            Reposition();
            RevealDock();
        };
        fallback.Start();
    }

    private void OnSettingsChanged()
    {
        ApplyDockAppearance();
        Reposition();
        bool startShown = _startView != null && Dock.Children.Contains(_startView);
        bool trashShown = _trashView != null && Dock.Children.Contains(_trashView);
        if (_config.ShowStartButton != startShown || _config.ShowTrash != trashShown)
        {
            RefreshPanel();
            UpdateTrashIcon();
        }
        ApplyWinKeySetting();
        ApplyGenieSetting();
        ApplyWindowButtonsSetting();
        ApplyFolderSettings();
        ApplyCursorSetting();
        WindowsSettingsRedirect.SetEnabled(_config.MacSystemSettings);
        ApplyMenuBarSetting();

        // Changing the work area broadcasts to every window: debounce while a slider is dragged.
        _behaviorDebounce ??= CreateDebounce();
        _behaviorDebounce.Stop();
        _behaviorDebounce.Start();
    }

    private DispatcherTimer CreateDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyBehavior();
        };
        return timer;
    }

    // ---------------- Mac window buttons on every app ----------------

    private MacWindowButtons? _windowButtons;

    private bool? _cursorsApplied;

    /// <summary>macOS mouse pointers on or off (only when the setting changed: it rewrites the system scheme).</summary>
    private void ApplyCursorSetting()
    {
        if (_cursorsApplied == _config.MacCursors) return;
        _cursorsApplied = _config.MacCursors;
        try
        {
            if (_config.MacCursors) MacCursors.Apply();
            else MacCursors.Restore();
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacCursors", ex);
        }
    }

    private void ApplyWindowButtonsSetting()
    {
        if (_config.MacWindowButtons && _windowButtons == null)
        {
            try { _windowButtons = new MacWindowButtons(MinimizeFromWindowButtons); }
            catch (Exception ex) { CrashLog.Write("MacWindowButtons", ex); }
            Task.Run(EditorTitleBars.Apply); // VS Code & co. use Windows' title bar, so they get the full Mac one
        }
        else if (!_config.MacWindowButtons && _windowButtons != null)
        {
            _windowButtons.Dispose();
            _windowButtons = null;
            Task.Run(EditorTitleBars.Restore);
        }
    }

    /// <summary>The yellow light on any app window: into its dock icon with the Genie effect when possible.</summary>
    private void MinimizeFromWindowButtons(IntPtr hwnd) => Dispatcher.BeginInvoke(new Action(async () =>
    {
        try
        {
            if (_config.GenieEffect && IconRectForWindow(hwnd) is { } icon && await Genie.MinimizeAsync(hwnd, icon)) return;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Genie from window buttons", ex);
        }
        Native.Win32.PostMessage(hwnd, Native.Win32.WM_SYSCOMMAND, new IntPtr(Native.Win32.SC_MINIMIZE), IntPtr.Zero);
    }));

    // ---------------- Folders: blue on the desktop, opened in Finder ----------------

    private Finder.ExplorerRedirect? _explorerRedirect;
    private bool? _blueDesktopApplied;

    private void ApplyFolderSettings()
    {
        if (_blueDesktopApplied != _config.BlueDesktopFolders)
        {
            _blueDesktopApplied = _config.BlueDesktopFolders;
            Finder.DesktopFolderIcons.Set(_config.BlueDesktopFolders);
        }

        if (_config.FinderOpensFolders && _explorerRedirect == null)
        {
            try
            {
                _explorerRedirect = new Finder.ExplorerRedirect(Dispatcher,
                    (path, select) => Finder.FinderWindow.OpenFolder(path, select.Count > 0 ? select : null),
                    Finder.FinderWindow.ExtractFromDesktop,
                    () => Finder.FinderWindow.OpenFolder(Finder.FinderAssets.Home));
            }
            catch (Exception ex)
            {
                CrashLog.Write("ExplorerRedirect", ex);
            }
        }
        else if (!_config.FinderOpensFolders && _explorerRedirect != null)
        {
            _explorerRedirect.Dispose();
            _explorerRedirect = null;
        }
    }

    // ---------------- Blue folders everywhere (one-time offer) ----------------

    private void OfferMacFolders()
    {
        Finder.WindowsFolderIcons.RefreshIconFileIfApplied(); // keep the icon current after an update
        if (_config.FolderIconsOffered) return;
        _config.FolderIconsOffered = true;
        ConfigService.Save(_config);
        if (Finder.WindowsFolderIcons.IsApplied()) return;

        var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        later.Tick += async (_, _) =>
        {
            later.Stop();
            var answer = MessageBox.Show(
                "Make every folder in Windows blue, like on a Mac?\n\n" +
                "Folders on the desktop, in File Explorer and in open/save windows will use the macOS folder.\n" +
                "Windows will ask for permission once. You can turn this off any time in MacDock Settings.",
                "MacDock", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) await Finder.WindowsFolderIcons.SetAsync(true);
        };
        later.Start();
    }

    /// <summary>Once, a little after the first start: offer the Windows speed-ups (the user picks each one).</summary>
    private void OfferPerformance()
    {
        if (_config.PerformanceOffered) return;
        _config.PerformanceOffered = true;
        ConfigService.Save(_config);
        var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            var answer = MessageBox.Show(
                "Make Windows faster and smoother?\n\n" +
                "MacDock can turn off ads, background apps and game recording, speed up menus, and remove preinstalled apps you don't use. " +
                "You choose each change, and every one can be turned back.",
                "MacDock", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) SystemSettingsWindow.Open("performance");
        };
        later.Start();
    }

    /// <summary>Once: MacDock's macOS-style wallpaper goes on the desktop (the previous one is remembered).</summary>
    private async void ApplyMacWallpaper()
    {
        MacWallpapers.FollowTheme();
        if (_config.MacWallpaperApplied) return;
        _config.MacWallpaperApplied = true;
        if (MacWallpapers.CurrentDesign() == null) _config.PreviousWallpaper = WindowsSettings.Wallpaper;
        ConfigService.Save(_config);
        try { await MacWallpapers.ApplyAsync("ribbons"); }
        catch (Exception ex) { CrashLog.Write("ApplyMacWallpaper", ex); }
    }

    private void OpenSettings() => SystemSettingsWindow.Open("dock");
}
