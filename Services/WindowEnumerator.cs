using System.Text;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>Finds the top-level windows the Windows taskbar would show, and their executables.</summary>
internal static class WindowEnumerator
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>Taskbar windows in Z-order (topmost first) with their executable path.</summary>
    public static List<(IntPtr Hwnd, string ExePath)> GetTaskbarWindows()
    {
        var result = new List<(IntPtr, string)>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (IsTaskbarWindow(hwnd) && GetExePath(hwnd) is { } exe)
                result.Add((hwnd, PathHelper.Normalize(exe)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Simplified version of the taskbar's own rules.</summary>
    public static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd)) return false;

        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & Win32.WS_EX_NOACTIVATE) != 0) return false;
            if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return false;
        }

        if (Win32.GetWindowTextLength(hwnd) == 0) return false;
        // Invisible stand-in windows some apps keep around (zero size): clicking the dock must not "activate" those.
        if (!Win32.IsIconic(hwnd) && Win32.GetWindowRect(hwnd, out var rect) && (rect.Right - rect.Left < 4 || rect.Bottom - rect.Top < 4)) return false;
        if (IsCloaked(hwnd)) return false; // other virtual desktops, suspended UWP apps
        if (ExcludedClasses.Contains(GetClassName(hwnd))) return false;

        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != OwnProcessId;
    }

    /// <summary>Returns the topmost window (in Z-order) among the given ones.</summary>
    public static IntPtr TopMost(IReadOnlyList<IntPtr> windows)
    {
        var set = new HashSet<IntPtr>(windows);
        IntPtr found = IntPtr.Zero;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!set.Contains(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found != IntPtr.Zero ? found : windows[0];
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? GetExePath(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);

        // UWP apps are hosted by ApplicationFrameHost; the real app owns the CoreWindow child.
        if (GetClassName(hwnd) == "ApplicationFrameWindow")
        {
            uint hostPid = pid;
            Win32.EnumChildWindows(hwnd, (child, _) =>
            {
                Win32.GetWindowThreadProcessId(child, out uint childPid);
                if (childPid == hostPid) return true;
                pid = childPid;
                return false;
            }, IntPtr.Zero);
        }

        return GetProcessPath(pid);
    }

    private static string? GetProcessPath(uint pid)
    {
        IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Win32.QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }
}
