using System.Runtime.InteropServices;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Catches clicks on the title-bar minimize button of app windows before the app sees them, so the
/// Genie can cover the window first and minimize it underneath (gapless). When Windows minimizes the
/// window itself, it vanishes first and the effect can only start a few frames later: a visible blink.
///
/// Runs a low-level mouse hook on its own thread. Only presses in the top-right corner of a window
/// are hit-tested (WM_NCHITTEST, 40 ms timeout), so normal mouse use is untouched. Releasing the
/// button away from the minimize button cancels, exactly like Windows.
/// </summary>
internal sealed class MinimizeButtonInterceptor : IDisposable
{
    private const int HTMINBUTTON = 8;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint HitTestTimeoutMs = 40; // the whole system's mouse waits on this hook: keep it short

    private readonly Thread _thread;
    private Win32.LowLevelMouseProc? _proc; // keep alive while hooked
    private uint _threadId;
    private IntPtr _pressed;                // window whose minimize button is held down (press swallowed)

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Win32.POINT point);

    /// <summary>Raised on the hook thread when a window's minimize button was clicked (pressed and released on it).</summary>
    public event Action<IntPtr>? Clicked;

    public MinimizeButtonInterceptor()
    {
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "MacDock minimize button hook" };
        _thread.Start();
    }

    private const uint PauseMessage = 0x8001, ResumeMessage = 0x8002; // WM_APP + 1 / + 2

    /// <summary>
    /// While a game is in front the hook is removed completely, so no key press or mouse movement of the
    /// game passes through MacDock (not even for a microsecond); it comes back when the game leaves.
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, paused ? PauseMessage : ResumeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        WarmUp();
        _proc = HookProc;
        IntPtr hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) return;
        try
        {
            // A message loop is all a low-level hook needs; it also takes the pause / resume requests.
            while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == PauseMessage && hook != IntPtr.Zero)
                {
                    Win32.UnhookWindowsHookEx(hook);
                    hook = IntPtr.Zero;
                }
                else if (msg.message == ResumeMessage && hook == IntPtr.Zero)
                {
                    hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
                }
            }
        }
        finally
        {
            if (hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(hook);
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        int message = wParam.ToInt32();
        if (message is Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP)
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            if (message == Win32.WM_LBUTTONDOWN)
            {
                _pressed = IntPtr.Zero;
                if (MinimizeButtonAt(info.pt) is { } window)
                {
                    _pressed = window;
                    return new IntPtr(1); // swallow: the app must not minimize itself
                }
            }
            else if (message == Win32.WM_LBUTTONUP && _pressed != IntPtr.Zero)
            {
                var window = _pressed;
                _pressed = IntPtr.Zero;
                if (MinimizeButtonAt(info.pt) == window)
                    ThreadPool.QueueUserWorkItem(_ => Clicked?.Invoke(window));
                return new IntPtr(1); // the press was swallowed, so the release must be too
            }
        }

        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// Runs the click path once before hooking. The first call of any code compiles it and builds its
    /// native call stubs; on the first click that took longer than Windows allows a low-level hook, so
    /// Windows skipped the hook and minimized the window itself (no gapless Genie the first time).
    /// </summary>
    private static void WarmUp()
    {
        try
        {
            Win32.GetCursorPos(out var pt);
            _ = MinimizeButtonAt(pt);
            IntPtr window = WindowFromPoint(pt);
            _ = WindowEnumerator.IsTaskbarWindow(window);
            Win32.GetWindowRect(window, out _);
            _ = Win32.IsIconic(window);
            Win32.SendMessageTimeout(window, WM_NCHITTEST, IntPtr.Zero, IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, HitTestTimeoutMs, out _);
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Win32.MSLLHOOKSTRUCT>());
            try { _ = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(buffer); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch
        {
            // Warm-up is best effort.
        }
    }

    /// <summary>The app window whose minimize button is under <paramref name="pt"/>, or null.</summary>
    private static IntPtr? MinimizeButtonAt(Win32.POINT pt)
    {
        IntPtr under = WindowFromPoint(pt);
        if (under == IntPtr.Zero) return null;
        IntPtr root = Win32.GetAncestor(under, Win32.GA_ROOT);
        if (root == IntPtr.Zero || !Win32.GetWindowRect(root, out var rect)) return null;

        // Cheap geometry first: caption buttons live in the top-right corner.
        if (pt.Y > rect.Top + 64 || pt.X < rect.Right - 300) return null;
        if (Win32.IsIconic(root) || !WindowEnumerator.IsTaskbarWindow(root)) return null;

        // Ask the window what is under the cursor (what Windows itself does). Windows of elevated
        // apps refuse the message: they keep their normal minimize.
        IntPtr lParam = new((pt.Y << 16) | (pt.X & 0xFFFF));
        if (Win32.SendMessageTimeout(under, WM_NCHITTEST, IntPtr.Zero, lParam,
                Win32.SMTO_ABORTIFHUNG, HitTestTimeoutMs, out var hit) == IntPtr.Zero)
            return null;
        return hit.ToInt64() == HTMINBUTTON ? root : null;
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
