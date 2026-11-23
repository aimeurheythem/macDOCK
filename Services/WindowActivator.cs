using MacDock.Native;

namespace MacDock.Services;

/// <summary>Brings windows to the front or minimizes them.</summary>
internal static class WindowActivator
{
    public static void Activate(IntPtr hwnd)
    {
        if (Win32.IsIconic(hwnd))
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        if (Win32.SetForegroundWindow(hwnd)) return;

        // Foreground lock fallback: a synthetic Alt tap lets this process set the foreground window.
        Win32.keybd_event(Win32.VK_MENU, 0, 0, UIntPtr.Zero);
        Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Win32.SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Brings one of OUR windows to the front even when another app owns the foreground (e.g. the
    /// Start menu opened by the Windows key): briefly share input state with the foreground thread.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        IntPtr fg = Win32.GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, out _);
        uint me = Win32.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Win32.AttachThreadInput(me, fgThread, true);
        try
        {
            Win32.BringWindowToTop(hwnd);
            if (!Win32.SetForegroundWindow(hwnd))
            {
                // Foreground lock still on: a synthetic Alt tap (tagged, so our Win key hook ignores it)
                // counts as user input and unlocks SetForegroundWindow for this process.
                Win32.keybd_event(Win32.VK_MENU, 0, 0, WinKeyInterceptor.Marker);
                Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, WinKeyInterceptor.Marker);
                Win32.SetForegroundWindow(hwnd);
            }
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(me, fgThread, false);
        }
    }

    public static void Minimize(IntPtr hwnd) => Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);

    /// <summary>Politely asks a window to close (same as clicking its X), so apps can prompt to save.</summary>
    public static void Close(IntPtr hwnd) => Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The top-level window that owns the current foreground window (dialogs map to their app window).</summary>
    public static IntPtr GetForegroundRoot()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = Win32.GetAncestor(fg, Win32.GA_ROOTOWNER);
        return root != IntPtr.Zero ? root : fg;
    }
}
