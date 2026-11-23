using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Opens MacDock's System Settings whenever the Windows Settings app comes up (Win+I, the Start menu,
/// "Display settings" on the desktop...). Windows Settings still opens when MacDock itself sends the
/// user there for a page it has no pane for (<see cref="OpenInWindows"/>).
/// </summary>
internal static class WindowsSettingsRedirect
{
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_SHOW = 0x8002;
    private static IntPtr _showHook;

    private static IntPtr _hook;
    private static WinEventProc? _proc; // kept alive while hooked
    private static bool _enabled;
    private static DateTime _allowUntil;
    private static readonly HashSet<IntPtr> Allowed = new();
    private static DispatcherTimer? _watch;

    /// <summary>Turns the redirect on or off. UI thread.</summary>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled && _hook == IntPtr.Zero)
        {
            _proc = OnForeground;
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, 0 /* OUTOFCONTEXT */);
            // Also as soon as its window appears (before its splash screen has a chance to be seen).
            _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, 0);
        }
        else if (!enabled && _hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            if (_showHook != IntPtr.Zero) UnhookWinEvent(_showHook);
            _showHook = IntPtr.Zero;
        }
    }

    /// <summary>Opens a page of the real Windows Settings, on purpose (not redirected).</summary>
    public static void OpenInWindows(string uri)
    {
        _allowUntil = DateTime.UtcNow.AddSeconds(10);
        Shell.Open(uri);
    }

    private static void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (!_enabled || hwnd == IntPtr.Zero) return;
            if (ev == EVENT_OBJECT_SHOW)
            {
                if (idObject != 0 || ClassOf(hwnd) != "ApplicationFrameWindow") return;
                if (IsWindowsSettings(hwnd, out var shown) || JustStarted()) Redirect(shown);
                return;
            }
            if (!IsWindowsSettings(hwnd, out var frame))
            {
                // A UWP frame gets its content a moment after it shows: look again for a few seconds.
                if (ClassOf(hwnd) == "ApplicationFrameWindow") WatchFrame(hwnd);
                return;
            }
            Redirect(frame);
        }
        catch (Exception ex)
        {
            CrashLog.Write("WindowsSettingsRedirect", ex);
        }
    }

    private static void WatchFrame(IntPtr hwnd)
    {
        _watch?.Stop();
        var started = DateTime.UtcNow;
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _watch.Tick += (_, _) =>
        {
            try
            {
                if (!_enabled || !Win32.IsWindow(hwnd) || DateTime.UtcNow - started > TimeSpan.FromSeconds(4)) { _watch?.Stop(); return; }
                if (!IsWindowsSettings(hwnd, out var frame)) return;
                _watch?.Stop();
                Redirect(frame);
            }
            catch (Exception ex)
            {
                _watch?.Stop();
                CrashLog.Write("WindowsSettingsRedirect.Watch", ex);
            }
        };
        _watch.Start();
    }

    /// <summary>A Settings process started in the last few seconds (its frame shows before its content).</summary>
    private static bool JustStarted()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("SystemSettings"))
        {
            try { if (DateTime.Now - p.StartTime < TimeSpan.FromSeconds(4)) return true; }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    private static void Redirect(IntPtr frame)
    {
        if (Allowed.Contains(frame)) return;
        if (DateTime.UtcNow < _allowUntil) { Allowed.Add(frame); return; }
        Allowed.RemoveWhere(h => !Win32.IsWindow(h));
        // Ended, not closed: a close message while Settings is still starting up made it crash with a
        // "buffer overrun" system error. Ending the process is what Task Manager does; its frame goes with it.
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("SystemSettings"))
        {
            try { process.Kill(); }
            catch { Win32.PostMessage(frame, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); }
            finally { process.Dispose(); }
        }
        Dispatcher.CurrentDispatcher.BeginInvoke(() => SystemSettingsWindow.Open());
    }

    /// <summary>
    /// Windows MacDock's title bars must leave alone: the Settings app, and any Store-app frame whose
    /// content hasn't arrived yet. Changing a starting frame (its style, size or position) could make
    /// the app inside it stop with a "buffer overrun" system error. Any thread.
    /// </summary>
    public static bool IsHandsOff(IntPtr hwnd)
    {
        if (ClassOf(hwnd) != "ApplicationFrameWindow") return false;
        if (FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null) == IntPtr.Zero) return true; // still starting
        return IsWindowsSettings(hwnd, out _);
    }

    /// <summary>The Settings app: a UWP frame whose content window belongs to SystemSettings.exe.</summary>
    private static bool IsWindowsSettings(IntPtr hwnd, out IntPtr frame)
    {
        frame = hwnd;
        string cls = ClassOf(hwnd);
        IntPtr core = IntPtr.Zero;
        if (cls == "ApplicationFrameWindow") core = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
        else if (cls == "Windows.UI.Core.CoreWindow") core = hwnd;
        if (core == IntPtr.Zero) return false;
        GetWindowThreadProcessId(core, out uint pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, "SystemSettings", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
