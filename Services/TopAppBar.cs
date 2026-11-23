using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MacDock.Config;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Reserves a strip at the top of the primary monitor for the menu bar (an AppBar on the top edge),
/// so maximized windows start below it, exactly like on macOS.
///
/// SAFETY: the bar's window handle is saved to disk while registered, so a crashed or killed MacDock
/// never leaves a dead strip behind: the watchdog, the next start and "--restore" remove it.
/// </summary>
internal static class TopAppBar
{
    private static readonly object Gate = new();
    private static IntPtr _hwnd;
    private static uint _message;
    private static bool _registered;
    private static Win32.RECT _rect;
    private static int _heightPx;

    private static string StatePath => Path.Combine(ConfigService.ConfigDirectory, "menubar-state.json");

    /// <summary>Reserves <paramref name="heightPx"/> device pixels at the top for <paramref name="hwnd"/>.</summary>
    public static void Reserve(IntPtr hwnd, uint callbackMessage, int heightPx)
    {
        lock (Gate)
        {
            if (_registered && _hwnd != hwnd) RemoveLocked();
            _hwnd = hwnd;
            _message = callbackMessage;
            _heightPx = heightPx;

            if (!_registered)
            {
                Try(() => File.WriteAllText(StatePath, hwnd.ToInt64().ToString())); // persist BEFORE registering
                var reg = NewData(hwnd);
                reg.uCallbackMessage = callbackMessage;
                if (Win32.SHAppBarMessage(Win32.ABM_NEW, ref reg) == UIntPtr.Zero) return;
                _registered = true;
                _rect = default;
            }
            UpdateLocked();
        }
    }

    /// <summary>Another AppBar moved or Explorer restarted: claim the strip again.</summary>
    public static void Refresh(bool explorerRestarted = false)
    {
        lock (Gate)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (explorerRestarted) _registered = false;
            _rect = default;
            if (!_registered) Reserve(_hwnd, _message, _heightPx);
            else UpdateLocked();
        }
    }

    /// <summary>Gives the strip back. Safe from any thread, any number of times.</summary>
    public static void Remove()
    {
        lock (Gate) RemoveLocked();
    }

    /// <summary>Removes a strip left registered by a MacDock that did not exit cleanly.</summary>
    public static void RecoverFromStateFile()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                if (long.TryParse(File.ReadAllText(StatePath).Trim(), out long old) && old != 0)
                {
                    var abd = NewData(new IntPtr(old));
                    Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
                }
                File.Delete(StatePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TopAppBar recovery: {ex.Message}");
            }
        }
    }

    private static void UpdateLocked()
    {
        if (!_registered) return;
        var primary = PrimaryMonitor();
        if (primary.Right <= primary.Left) return;

        var abd = NewData(_hwnd);
        abd.uEdge = Win32.ABE_TOP;
        abd.rc = primary;
        abd.rc.Bottom = abd.rc.Top + _heightPx;
        Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref abd);
        abd.rc.Bottom = abd.rc.Top + _heightPx; // QUERYPOS may move the top; keep our height

        if (abd.rc.SameAs(_rect)) return; // unchanged: don't make every window recompute
        Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref abd);
        _rect = abd.rc;
    }

    private static void RemoveLocked()
    {
        if (_registered)
        {
            var abd = NewData(_hwnd);
            Try(() => Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd));
            _registered = false;
            _rect = default;
        }
        Try(() => { if (File.Exists(StatePath)) File.Delete(StatePath); });
    }

    private static Win32.RECT PrimaryMonitor()
    {
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        IntPtr monitor = Win32.MonitorFromPoint(new Win32.POINT(), Win32.MONITOR_DEFAULTTOPRIMARY);
        return Win32.GetMonitorInfo(monitor, ref info) ? info.rcMonitor : default;
    }

    private static Win32.APPBARDATA NewData(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>(),
        hWnd = hwnd,
    };

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { Debug.WriteLine($"TopAppBar: {ex.Message}"); }
    }
}
