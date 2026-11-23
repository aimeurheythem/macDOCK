using System.Text;
using System.Windows.Threading;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Detects when the foreground window is fullscreen on the primary monitor (videos, games, F11),
/// so the topmost dock can get out of the way like the real taskbar does.
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private readonly Win32.WinEventProc _proc; // keep alive while hooked
    private readonly IntPtr _hook;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<IntPtr> _ownWindows;

    public bool IsFullscreen { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsFullscreen"/> changes.</summary>
    public event Action<bool>? Changed;

    /// <summary>A game or video is fullscreen on any monitor (not only the main one).</summary>
    public bool IsGame { get; private set; }

    public event Action<bool>? GameChanged;

    public FullscreenWatcher(params IntPtr[] ownWindows)
    {
        _ownWindows = new HashSet<IntPtr>(ownWindows);
        _proc = (_, _, _, _, _, _, _) => Check();
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);

        // Also poll: apps can go fullscreen without changing the foreground window (e.g. F11).
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public void Check()
    {
        bool now = IsForegroundFullscreen(out bool anyMonitor);
        if (anyMonitor != IsGame)
        {
            IsGame = anyMonitor;
            GameChanged?.Invoke(anyMonitor);
        }
        if (now == IsFullscreen) return;
        IsFullscreen = now;
        Changed?.Invoke(now);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    private const long WS_CAPTION = 0x00C00000;

    /// <summary>Shell overlays that cover the screen for a moment (Alt+Tab, Task View, Start): not "fullscreen".</summary>
    private static readonly HashSet<string> Overlays = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow", "ForegroundStaging", "Windows.UI.Core.CoreWindow", "LockScreenBackstopFrame",
    };

    /// <summary>
    /// True for videos, browsers in F11, presentations and games (exclusive or borderless): the window in
    /// front covers the whole primary monitor, including the strips reserved for the dock and the menu
    /// bar. A normal maximized window (it has a title bar) never counts, even when it covers the screen.
    /// </summary>
    private bool IsForegroundFullscreen(out bool anyMonitor)
    {
        anyMonitor = false;
        // Exclusive-fullscreen games (Direct3D) and presentation mode: Windows says so directly.
        if (SHQueryUserNotificationState(out int state) == 0 &&
            state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
        {
            anyMonitor = true;
            return true;
        }

        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || _ownWindows.Contains(fg)) return false;
        Win32.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId)
        {
            // Spotlight and other popups over a game or video: keep the current state. A normal MacDock
            // window in front (Finder, System Settings) means nothing is fullscreen any more.
            long ex = Win32.GetWindowLongPtr(fg, -20 /* GWL_EXSTYLE */).ToInt64();
            bool popup = (ex & 0x00000080 /* WS_EX_TOOLWINDOW */) != 0 || (ex & 0x00000008 /* WS_EX_TOPMOST */) != 0;
            anyMonitor = popup && IsGame;
            return popup && IsFullscreen;
        }

        var sb = new StringBuilder(64);
        Win32.GetClassName(fg, sb, sb.Capacity);
        if (Overlays.Contains(sb.ToString())) return false;

        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST), ref info)) return false;
        if (!Win32.GetWindowRect(fg, out var r)) return false;
        var m = info.rcMonitor;
        bool coversMonitor = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        if (!coversMonitor) return false;

        // A maximized window with a title bar is a normal window, not fullscreen.
        long style = Win32.GetWindowLongPtr(fg, Win32.GWL_STYLE).ToInt64();
        bool hasTitleBar = (style & WS_CAPTION) == WS_CAPTION;
        if (Win32.IsZoomed(fg) && hasTitleBar) return false;
        anyMonitor = true; // a game on the second monitor still gets MacDock out of its way
        return (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0; // the dock and menu bar live on the main monitor
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
    }
}
