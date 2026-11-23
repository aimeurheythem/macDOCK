using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MacDock.Config;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Replaces the taskbar the cooperative way, so Explorer never fights us over the work area:
///   1. the taskbar is switched to auto-hide (it stops reserving space) and its windows are hidden;
///   2. the dock registers as an AppBar at the bottom edge, so Explorer itself shrinks the work area.
/// Explorer recomputes the work area once per change; maximized windows resize once, no flicker loop.
///
/// SAFETY: the original taskbar state is saved to disk before anything changes, so it can be restored
/// on exit, on crash (watchdog process / next start) or with "MacDock.exe --restore".
/// All public methods are thread-safe and idempotent.
/// </summary>
internal static class TaskbarManager
{
    private static readonly object Gate = new();
    private static TaskbarState? _state;

    private static IntPtr _appBarHwnd;
    private static uint _appBarMessage;
    private static bool _appBarRegistered;
    private static Win32.RECT _appBarRect;
    private static int _reservePx;

    private static Win32.WinEventProc? _showHookProc; // keep alive while hooked
    private static IntPtr _showHook;

    public static bool IsHidden { get; private set; }

    private static string StatePath => Path.Combine(ConfigService.ConfigDirectory, "taskbar-state.json");

    // ---------------- Hide / restore ----------------

    /// <summary>
    /// Hides the taskbars and reserves <paramref name="reservePx"/> device pixels at the bottom of the
    /// primary monitor through an AppBar owned by <paramref name="appBarHwnd"/> (0 = reserve nothing).
    /// </summary>
    public static void Hide(int reservePx, IntPtr appBarHwnd, uint appBarMessage)
    {
        lock (Gate)
        {
            if (!IsHidden)
            {
                _state = new TaskbarState
                {
                    OriginalTaskbarState = GetTaskbarState(),
                    AppBarHwnd = appBarHwnd.ToInt64(),
                };
                WriteState(_state); // persist BEFORE changing anything
                IsHidden = true;
            }

            _appBarHwnd = appBarHwnd;
            _appBarMessage = appBarMessage;
            _reservePx = reservePx;

            SetTaskbarsVisible(false);
            EnsureTaskbarAutoHide();
            SetTaskbarsVisible(false); // Explorer may show it while switching to auto-hide
            UpdateAppBar();
        }
    }

    /// <summary>Explorer restarted: our AppBar registration died with it. Re-register and re-hide (no restore flash).</summary>
    public static void OnExplorerRestarted()
    {
        lock (Gate)
        {
            if (!IsHidden) return;
            _appBarRegistered = false;
            _appBarRect = default;
            SetTaskbarsVisible(false);
            EnsureTaskbarAutoHide();
            SetTaskbarsVisible(false);
            UpdateAppBar();
        }
    }

    /// <summary>Explorer says AppBar positions changed (ABN_POSCHANGED): re-claim our strip.</summary>
    public static void OnAppBarPositionChanged()
    {
        lock (Gate)
        {
            if (!IsHidden) return;
            _appBarRect = default; // force a fresh SETPOS
            UpdateAppBar();
        }
    }

    /// <summary>Re-hides taskbars Explorer showed again and keeps auto-hide on. Never touches the work area.</summary>
    public static void Enforce()
    {
        lock (Gate)
        {
            if (!IsHidden) return;
            foreach (var hwnd in FindTaskbars())
                if (Win32.IsWindowVisible(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_HIDE);
            EnsureTaskbarAutoHide();
        }
    }

    /// <summary>Removes our AppBar, restores the taskbar's auto-hide setting and shows it. Safe from any thread.</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            if (!IsHidden) return;
            IsHidden = false;
            Try(RemoveAppBar);
            if (_state?.OriginalTaskbarState is int original) Try(() => SetTaskbarState(original));
            Try(() => SetTaskbarsVisible(true));
            Try(DeleteState);
        }
    }

    /// <summary>
    /// Restores from the state file left by a process that did not exit cleanly.
    /// With <paramref name="force"/>, also shows the taskbars when there is no state file.
    /// </summary>
    public static void RecoverFromStateFile(bool force)
    {
        lock (Gate)
        {
            var saved = ReadState();
            if (saved == null && !force) return;

            if (saved != null)
            {
                // Explorer still lists the dead dock's AppBar: remove it by its old handle.
                if (saved.AppBarHwnd != 0) Try(() => RemoveAppBarByHandle(new IntPtr(saved.AppBarHwnd)));
                if (saved.OriginalTaskbarState is int original) Try(() => SetTaskbarState(original));
                // Files from older versions only have work areas.
                if (saved.WorkAreas.Count > 0) Try(() => RestoreWorkAreas(saved.WorkAreas));
            }
            Try(() => SetTaskbarsVisible(true));
            Try(DeleteState);
        }
    }

    // ---------------- Watching (UI thread) ----------------

    /// <summary>Re-hides a taskbar the instant Explorer shows it (e.g. when the Start menu opens).</summary>
    public static void StartWatching()
    {
        StopWatching();
        IntPtr tray = Win32.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        Win32.GetWindowThreadProcessId(tray, out uint explorerPid);

        _showHookProc = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject != Win32.OBJID_WINDOW || idChild != 0 || !IsHidden) return;
            if (IsTaskbarWindow(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_HIDE);
        };
        _showHook = Win32.SetWinEventHook(Win32.EVENT_OBJECT_SHOW, Win32.EVENT_OBJECT_SHOW, IntPtr.Zero,
            _showHookProc, explorerPid, 0, Win32.WINEVENT_OUTOFCONTEXT);
    }

    public static void StopWatching()
    {
        if (_showHook != IntPtr.Zero) Win32.UnhookWinEvent(_showHook);
        _showHook = IntPtr.Zero;
        _showHookProc = null;
    }

    // ---------------- AppBar ----------------

    private static Win32.APPBARDATA NewAppBarData(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>(),
        hWnd = hwnd,
    };

    private static void UpdateAppBar()
    {
        if (_reservePx <= 0 || _appBarHwnd == IntPtr.Zero)
        {
            RemoveAppBar();
            return;
        }

        if (!_appBarRegistered)
        {
            var reg = NewAppBarData(_appBarHwnd);
            reg.uCallbackMessage = _appBarMessage;
            if (Win32.SHAppBarMessage(Win32.ABM_NEW, ref reg) == UIntPtr.Zero) return;
            _appBarRegistered = true;
            _appBarRect = default;
        }

        var monitor = GetMonitors().FirstOrDefault(m => m.Primary).Monitor;
        if (monitor.Right <= monitor.Left) return;

        var abd = NewAppBarData(_appBarHwnd);
        abd.uEdge = Win32.ABE_BOTTOM;
        abd.rc = monitor;
        abd.rc.Top = abd.rc.Bottom - _reservePx;
        Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - _reservePx; // QUERYPOS may move the bottom; keep our height

        if (abd.rc.SameAs(_appBarRect)) return;   // unchanged: don't make Explorer recompute
        Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref abd);
        _appBarRect = abd.rc;
    }

    private static void RemoveAppBar()
    {
        if (!_appBarRegistered) return;
        RemoveAppBarByHandle(_appBarHwnd);
        _appBarRegistered = false;
        _appBarRect = default;
    }

    private static void RemoveAppBarByHandle(IntPtr hwnd)
    {
        var abd = NewAppBarData(hwnd);
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
    }

    // ---------------- Taskbar ----------------

    private static int? GetTaskbarState()
    {
        IntPtr tray = Win32.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;
        var abd = NewAppBarData(tray);
        return (int)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref abd).ToUInt32();
    }

    private static void SetTaskbarState(int state)
    {
        IntPtr tray = Win32.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        var abd = NewAppBarData(tray);
        abd.lParam = new IntPtr(state);
        Win32.SHAppBarMessage(Win32.ABM_SETSTATE, ref abd);
    }

    /// <summary>An auto-hide taskbar reserves no space, so only our AppBar shapes the work area.</summary>
    private static void EnsureTaskbarAutoHide()
    {
        if (GetTaskbarState() is int state && (state & Win32.ABS_AUTOHIDE) == 0)
            SetTaskbarState(state | Win32.ABS_AUTOHIDE);
    }

    private static List<IntPtr> FindTaskbars()
    {
        var list = new List<IntPtr>();
        IntPtr main = Win32.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);

        IntPtr secondary = IntPtr.Zero;
        while ((secondary = Win32.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            list.Add(secondary);

        // Windows 7-style separate Start orb (does not exist on Windows 10, harmless there).
        IntPtr startOrb = Win32.FindWindow("Button", "Start");
        if (startOrb != IntPtr.Zero) list.Add(startOrb);
        return list;
    }

    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        var cls = sb.ToString();
        return cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
    }

    private static void SetTaskbarsVisible(bool visible)
    {
        foreach (var hwnd in FindTaskbars())
            Win32.ShowWindow(hwnd, visible ? Win32.SW_SHOWNA : Win32.SW_HIDE);
    }

    // ---------------- Monitors / legacy work-area restore ----------------

    private static List<(Win32.RECT Monitor, Win32.RECT Work, bool Primary)> GetMonitors()
    {
        var result = new List<(Win32.RECT, Win32.RECT, bool)>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(hMon, ref info))
                result.Add((info.rcMonitor, info.rcWork, (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void RestoreWorkAreas(List<SavedWorkArea> saved)
    {
        var monitors = GetMonitors();
        foreach (var s in saved)
        {
            if (!monitors.Any(m => m.Monitor.SameAs(s.MonitorRect))) continue;
            var r = s.WorkRect;
            Win32.SystemParametersInfo(Win32.SPI_SETWORKAREA, 0, ref r, Win32.SPIF_SENDCHANGE);
        }
    }

    // ---------------- State file ----------------

    private static void WriteState(TaskbarState state)
    {
        Try(() =>
        {
            Directory.CreateDirectory(ConfigService.ConfigDirectory);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        });
    }

    private static TaskbarState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var json = File.ReadAllText(StatePath);
            if (json.TrimStart().StartsWith('[')) // older versions saved only the work areas
                return new TaskbarState { WorkAreas = JsonSerializer.Deserialize<List<SavedWorkArea>>(json) ?? new() };
            return JsonSerializer.Deserialize<TaskbarState>(json) ?? new TaskbarState();
        }
        catch
        {
            return new TaskbarState(); // unreadable: still show the taskbars
        }
    }

    private static void DeleteState()
    {
        if (File.Exists(StatePath)) File.Delete(StatePath);
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { Debug.WriteLine($"TaskbarManager: {ex.Message}"); }
    }

    /// <summary>What to put back after a crash.</summary>
    public sealed class TaskbarState
    {
        /// <summary>Taskbar ABM_GETSTATE value before MacDock changed it (auto-hide bit).</summary>
        public int? OriginalTaskbarState { get; set; }

        /// <summary>Dock window handle used for the AppBar, to unregister it if the dock died.</summary>
        public long AppBarHwnd { get; set; }

        /// <summary>Only present in state files written by older versions.</summary>
        public List<SavedWorkArea> WorkAreas { get; set; } = new();
    }

    /// <summary>JSON-friendly copy of one monitor's rectangles (legacy state files).</summary>
    public sealed class SavedWorkArea
    {
        public int[] Monitor { get; set; } = Array.Empty<int>();
        public int[] Work { get; set; } = Array.Empty<int>();

        internal Win32.RECT MonitorRect => ToRect(Monitor);
        internal Win32.RECT WorkRect => ToRect(Work);
        private static Win32.RECT ToRect(int[] a) => a.Length == 4 ? new Win32.RECT(a[0], a[1], a[2], a[3]) : default;
    }
}
