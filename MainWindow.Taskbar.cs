using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacDock.Native;
using MacDock.Services;
using Microsoft.Win32;

namespace MacDock;

/// <summary>Taskbar hiding, panic hotkey, fullscreen handling.</summary>
public partial class MainWindow
{
    private const int PanicHotkeyId = 0x4D44; // "MD"
    private const uint VK_T = 0x54;
    private const double ReserveExtra = 4;    // DIPs of breathing room above the dock for maximized windows

    private static readonly uint TaskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");

    /// <summary>Callback message Explorer uses to notify our AppBar.</summary>
    private static readonly uint AppBarMessage = Win32.RegisterWindowMessage("MacDock.AppBarMessage");

    private IntPtr _hwnd;
    private FullscreenWatcher? _fullscreen;

    /// <summary>Called once the window handle exists and the window is loaded.</summary>
    private void InitTaskbarIntegration()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // Panic hotkey: Ctrl+Alt+Shift+T restores the taskbar and closes the dock.
        Win32.RegisterHotKey(_hwnd, PanicHotkeyId,
            Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, VK_T);

        ScheduleRevealFallback();
        ApplyBehavior(); // reveals the dock when the taskbar step is done

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _fullscreen = new FullscreenWatcher(_hwnd, _backdrop.Handle);
        _fullscreen.Changed += fullscreen =>
        {
            SetDockVisible(!fullscreen);
            _menuBar?.SetFullscreen(fullscreen);
        };
        _fullscreen.GameChanged += game => GameMode.Set(game);
        ApplyMenuBarSetting();
        InitFrost();

        PreloadStartMenu();
        ApplyWinKeySetting();
        ApplyGenieSetting();
        WindowsSettingsRedirect.SetEnabled(_config.MacSystemSettings);
        // The rest is spread out so the dock and menu bar appear smoothly first: each heavier job runs
        // on its own, once the screen has settled (title bars, folder icons, pointers, the wallpaper).
        WhenIdle(0.8, ApplyWindowButtonsSetting);
        WhenIdle(2.5, ApplyFolderSettings);
        WhenIdle(3.5, ApplyCursorSetting);
        WhenIdle(2.0, ApplyMacWallpaper);
        WhenIdle(6, OfferMacFolders);
        WhenIdle(6, OfferPerformance);
        WhenIdle(25, MemoryTrim.Now); // start-up leftovers (icons, pictures) go back to Windows
        WhenIdle(30, MemoryTrim.StartGuard);
    }

    /// <summary>Runs <paramref name="action"/> after <paramref name="seconds"/>, at a moment the app has nothing else to do.</summary>
    private void WhenIdle(double seconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { action(); }
            catch (Exception ex) { CrashLog.Write("WhenIdle", ex); }
        };
        timer.Start();
    }

    private void ShutdownTaskbarIntegration()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _winKeys?.Dispose();
        _winKeys = null;
        _windowButtons?.Dispose();
        _windowButtons = null;
        _explorerRedirect?.Dispose();
        _explorerRedirect = null;
        _menuBar?.Close();
        _menuBar = null;
        _fullscreen?.Dispose();
        if (_hwnd != IntPtr.Zero) Win32.UnregisterHotKey(_hwnd, PanicHotkeyId);
        TaskbarManager.StopWatching();
        TaskbarManager.Restore();
    }

    /// <summary>Height reserved at the bottom of the screen (device pixels) so maximized windows don't go under the dock.</summary>
    private int ReservePixels()
    {
        if (_config.AutoHide) return 0; // auto-hidden dock overlaps windows, like macOS
        var dpi = VisualTreeHelper.GetDpi(this);
        return (int)Math.Ceiling((Dock.DockHeight + BottomGap + ReserveExtra) * dpi.DpiScaleY);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == PanicHotkeyId)
        {
            handled = true;
            Panic();
        }
        else if (msg == AppBarMessage && AppBarMessage != 0)
        {
            if (wParam.ToInt32() == Win32.ABN_POSCHANGED)
                Task.Run(TaskbarManager.OnAppBarPositionChanged);
            handled = true;
        }
        else if (msg == TaskbarCreatedMessage && TaskbarCreatedMessage != 0)
        {
            // Explorer restarted: new taskbar, and our AppBar registration is gone. Redo both.
            if (TaskbarManager.IsHidden)
            {
                TaskbarManager.StartWatching();
                Task.Run(TaskbarManager.OnExplorerRestarted);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Emergency exit: restore the taskbar first, then quit.</summary>
    private static void Panic()
    {
        TopAppBar.Remove();
        TaskbarManager.Restore();
        Application.Current.Shutdown();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (TaskbarManager.IsHidden)
            {
                int reserve = ReservePixels();
                var hwnd = _hwnd;
                await Task.Run(() => TaskbarManager.Hide(reserve, hwnd, AppBarMessage));
            }
            Reposition();
        });
    }

    // ---------------- Menu bar ----------------

    private MenuBarWindow? _menuBar;

    /// <summary>Shows or removes the macOS-style menu bar to match the setting.</summary>
    private void ApplyMenuBarSetting()
    {
        if (_config.ShowMenuBar && _menuBar == null)
        {
            _menuBar = new MenuBarWindow();
            _menuBar.SearchRequested += ToggleSpotlight;
            _menuBar.Show();
            ApplyDockAppearance(); // the bar uses the dock's glass
            if (_fullscreen?.IsFullscreen == true) _menuBar.SetFullscreen(true);
        }
        else if (!_config.ShowMenuBar && _menuBar != null)
        {
            _menuBar.Close(); // also gives the reserved strip back
            _menuBar = null;
        }
    }

    /// <summary>Hides the dock while a fullscreen app is in front (like the real taskbar).</summary>
    private void SetDockVisible(bool visible)
    {
        if (visible)
        {
            Show();
            if (_revealed) _backdrop.SetVisible(true);
            UpdateBackdrop();
        }
        else
        {
            _backdrop.SetVisible(false);
            Hide();
        }
    }
}
