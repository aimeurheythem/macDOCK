using System.Runtime.InteropServices;
using System.Threading;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Makes a lone press of the Windows key open MacDock's Start menu instead of the Windows one.
/// Win+anything (Win+E, Win+R, Win+L, ...) is untouched.
///
/// How: a low-level keyboard hook (on its own thread, so a busy UI can never stall typing) watches
/// the Win key. On a lone tap it holds back the real Win-up, injects an unassigned key (0xE8) and then
/// the Win-up. Windows sees "Win + key", so it skips its Start menu; we raise <see cref="Tapped"/>.
/// </summary>
internal sealed class WinKeyInterceptor : IDisposable
{
    private const byte MaskKey = 0xE8;                    // unassigned virtual key
    internal static readonly UIntPtr Marker = new(0x4D44); // tags our own injected events (other code may use it too)

    private readonly Thread _thread;
    private Win32.LowLevelKeyboardProc? _proc;            // keep alive while hooked
    private uint _threadId;
    private bool _winDown;
    private bool _comboUsed;

    /// <summary>Raised on the hook thread when the Win key was tapped alone.</summary>
    public event Action? Tapped;

    /// <summary>Win+I was pressed (and kept from Windows): open MacDock's System Settings instead.</summary>
    public event Action? SettingsKey;

    /// <summary>Whether Win+I should be taken over (MacDock's System Settings replaces Windows Settings).</summary>
    public Func<bool>? TakeSettingsKey;

    private bool _swallowedI;

    public WinKeyInterceptor()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "MacDock Win key hook" };
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
        _proc = HookProc;
        IntPtr hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
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
                    hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
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

        // Only our own synthetic keys are skipped. Other injected keys must be handled: remote desktop
        // tools (AnyDesk, TeamViewer, RDP clients...) deliver the user's real key presses as injected input.
        var key = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        if (key.dwExtraInfo == Marker)
            return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        int message = wParam.ToInt32();
        bool down = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        bool up = message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
        bool isWin = key.vkCode is Win32.VK_LWIN or Win32.VK_RWIN;

        if (isWin && down)
        {
            if (!_winDown)
            {
                _winDown = true;
                _comboUsed = false;
            }
        }
        else if (isWin && up)
        {
            _winDown = false;
            if (_swallowedI)
            {
                // Win+I went to MacDock: a mask key before Win-up keeps Windows from opening Start.
                _swallowedI = false;
                byte vk = (byte)key.vkCode;
                Win32.keybd_event(MaskKey, 0, 0, Marker);
                Win32.keybd_event(MaskKey, 0, Win32.KEYEVENTF_KEYUP, Marker);
                Win32.keybd_event(vk, 0, Win32.KEYEVENTF_EXTENDEDKEY | Win32.KEYEVENTF_KEYUP, Marker);
                return new IntPtr(1);
            }
            if (!_comboUsed)
            {
                // Hold back this Win-up; send mask key + Win-up ourselves so Windows sees a combo.
                byte vk = (byte)key.vkCode;
                Win32.keybd_event(MaskKey, 0, 0, Marker);
                Win32.keybd_event(MaskKey, 0, Win32.KEYEVENTF_KEYUP, Marker);
                Win32.keybd_event(vk, 0, Win32.KEYEVENTF_EXTENDEDKEY | Win32.KEYEVENTF_KEYUP, Marker);
                ThreadPool.QueueUserWorkItem(_ => Tapped?.Invoke());
                return new IntPtr(1);
            }
        }
        else if (_winDown && key.vkCode == 0x49 /* I */ && (down || up) && TakeSettingsKey?.Invoke() == true)
        {
            // Win+I: MacDock's System Settings opens and Windows Settings never starts (no splash screen).
            _comboUsed = true;
            _swallowedI = true;
            if (down) ThreadPool.QueueUserWorkItem(_ => SettingsKey?.Invoke());
            return new IntPtr(1);
        }
        else if (_winDown && down)
        {
            _comboUsed = true; // Win+E, Win+R, ... : leave Windows in charge
        }

        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
