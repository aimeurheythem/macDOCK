using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// macOS title bars on every app window.
///
/// Two kinds of windows:
///  - Windows that let Windows draw their title bar (Notepad, Git Bash, most classic apps): Windows' own
///    buttons and icon are switched off for real (the window's system menu style is removed, and put back
///    when MacDock stops), and a full macOS title bar covers the caption: traffic lights on the left,
///    the window title centered, in the color of the window's content.
///  - Windows that draw their own title bar (Chrome, Brave, VS Code, Electron and Store apps): their
///    buttons are part of the app's own picture, so the traffic lights are drawn exactly over them,
///    on the title bar's color.
///
/// Every title bar is a small layered window owned by its app window: Windows keeps it right above that
/// window and hides it with the window (minimize, other virtual desktop). All positions are in physical
/// pixels (per-monitor DPI aware threads), so the bars sit right on every monitor whatever its scaling.
///
/// Threads: window events are handled on one thread of their own, and every title bar runs on its own
/// small UI thread: an owned window shares input with its owner, so a frozen app can only ever stall its
/// own title bar, never the others or the dock.
/// </summary>
internal sealed class MacWindowButtons : IDisposable
{
    // ---------------- Win32 ----------------

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B, EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;

    internal const long WS_CHILD = 0x40000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
    internal const long WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    internal const uint GA_ROOT = 2;
    internal const int DWMWA_CAPTION_BUTTON_BOUNDS = 5, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    internal static readonly IntPtr PerMonitorV2 = new(-4);

    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    // ---------------- State ----------------

    private readonly Action<IntPtr> _minimize;
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private readonly Dictionary<IntPtr, TitleBarHost> _bars = new();
    private readonly List<IntPtr> _hooks = new();
    private Win32.WinEventProc? _proc; // keep alive while hooked
    private DispatcherTimer? _sweep;
    private static readonly bool SelfElevated = IsElevated((uint)Environment.ProcessId);

    /// <param name="minimize">Minimizes a window (called on any thread): the dock does it with the Genie effect.</param>
    public MacWindowButtons(Action<IntPtr> minimize)
    {
        _minimize = minimize;
        CaptionStyle.RestoreLeftovers(); // after a crash: give windows their buttons and menus back first
        GlobalMenus.RestoreLeftovers();
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            SetThreadDpiAwarenessContext(PerMonitorV2); // physical pixels everywhere, on every monitor
            _dispatcher = Dispatcher.CurrentDispatcher;
            try { Start(); }
            catch (Exception ex) { CrashLog.Write("MacWindowButtons", ex); }
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "MacDock title bars" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(3000);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { CaptionStyle.RestoreAll(); GlobalMenus.RestoreAll(); };
    }

    private void Start()
    {
        _proc = OnEvent;
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
        _locationHook = Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_NAMECHANGE);
        // In a game, Windows reports every mouse movement as a location change (a thousand a second with
        // a gaming mouse): that hook goes away while a game is fullscreen, and comes back after it.
        GameMode.Changed += on => _dispatcher?.BeginInvoke(() =>
        {
            if (on && _locationHook != IntPtr.Zero) { Win32.UnhookWinEvent(_locationHook); _hooks.Remove(_locationHook); _locationHook = IntPtr.Zero; }
            else if (!on && _locationHook == IntPtr.Zero) { _locationHook = Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_NAMECHANGE); Sweep(); }
        });
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);

        _sweep = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _sweep.Tick += (_, _) => { if (!GameMode.Active) Sweep(); };
        _sweep.Start();
        Sweep();
    }

    private IntPtr _locationHook;

    private IntPtr Hook(uint min, uint max)
    {
        IntPtr hook = Win32.SetWinEventHook(min, max, IntPtr.Zero, _proc!, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
        return hook;
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != Win32.OBJID_WINDOW || idChild != 0) return;
        try
        {
            switch (ev)
            {
                case EVENT_OBJECT_LOCATIONCHANGE:
                    if (_bars.TryGetValue(hwnd, out var moved)) moved.Post(b => b.Update());
                    break;
                case EVENT_OBJECT_NAMECHANGE:
                    if (_bars.TryGetValue(hwnd, out var renamed)) renamed.Post(b => b.TitleChanged());
                    break;
                case EVENT_SYSTEM_FOREGROUND:
                    var root = Win32.GetAncestor(hwnd, GA_ROOT);
                    if (!_bars.ContainsKey(root) && root != IntPtr.Zero && IsEligible(root)) Add(root);
                    foreach (var host in _bars.Values)
                    {
                        bool active = host.Target == root;
                        host.Post(b => b.SetActive(active));
                    }
                    if (_bars.TryGetValue(root, out var front)) front.Post(b => { b.Update(); b.SampleColorSoon(); });
                    break;
                case EVENT_SYSTEM_MOVESIZESTART:
                    if (_bars.TryGetValue(hwnd, out var dragged)) dragged.Post(b => b.SetMoving(true));
                    break;
                case EVENT_SYSTEM_MINIMIZESTART:
                case EVENT_OBJECT_HIDE:
                case EVENT_OBJECT_CLOAKED:
                    if (_bars.TryGetValue(hwnd, out var gone)) gone.Post(b => b.Hide());
                    break;
                case EVENT_OBJECT_DESTROY:
                    Remove(hwnd);
                    break;
                case EVENT_SYSTEM_MINIMIZEEND:
                case EVENT_OBJECT_SHOW:
                case EVENT_OBJECT_UNCLOAKED:
                case EVENT_SYSTEM_MOVESIZEEND:
                    if (_bars.TryGetValue(hwnd, out var back))
                    {
                        bool ended = ev == EVENT_SYSTEM_MOVESIZEEND;
                        back.Post(b => { if (ended) b.SetMoving(false); else b.Update(); b.SampleColorSoon(); });
                    }
                    else if (ev != EVENT_SYSTEM_MOVESIZEEND && IsEligible(hwnd)) Add(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacWindowButtons.OnEvent", ex);
        }
    }

    /// <summary>Once a second: pick up new windows, drop gone ones, catch anything the events missed.</summary>
    private void Sweep()
    {
        try
        {
            var seen = new HashSet<IntPtr>();
            Win32.EnumWindows((hwnd, _) =>
            {
                if (_bars.ContainsKey(hwnd) ? IsStillEligible(hwnd) : IsEligible(hwnd))
                {
                    seen.Add(hwnd);
                    if (!_bars.ContainsKey(hwnd)) Add(hwnd);
                }
                return true;
            }, IntPtr.Zero);

            foreach (var hwnd in _bars.Keys.Where(h => !seen.Contains(h)).ToList())
            {
                // Minimized or briefly hidden windows keep their bar (hidden); gone ones lose it.
                if (!Win32.IsWindow(hwnd)) Remove(hwnd);
                else _bars[hwnd].Post(b => b.Hide());
            }
            var fg = Win32.GetAncestor(Win32.GetForegroundWindow(), GA_ROOT);
            foreach (var host in _bars.Values)
            {
                if (!seen.Contains(host.Target)) continue;
                bool active = host.Target == fg;
                host.Post(b => { b.SetActive(active); b.Update(); });
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacWindowButtons.Sweep", ex);
        }
    }

    private void Add(IntPtr hwnd)
    {
        bool active = Win32.GetAncestor(Win32.GetForegroundWindow(), GA_ROOT) == hwnd;
        var host = new TitleBarHost(hwnd, _minimize);
        _bars[hwnd] = host;
        host.Post(b => { b.SetActive(active); b.Update(); });
    }

    private void Remove(IntPtr hwnd)
    {
        if (_bars.Remove(hwnd, out var host)) host.Close();
    }

    // ---------------- Which windows ----------------

    /// <summary>App windows with caption buttons, on screen, not full screen, not ours, not admin apps (Windows blocks us from controlling those).</summary>
    internal static bool IsEligible(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd) || !WindowEnumerator.IsTaskbarWindow(hwnd)) return false;
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        if ((style & WS_CHILD) != 0 || (style & (WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) == 0) return false;
        if (WindowsSettingsRedirect.IsHandsOff(hwnd)) return false;
        return IsOnScreenNormal(hwnd, style);
    }

    /// <summary>Same as <see cref="IsEligible"/> for a window we already gave a title bar (its own
    /// system menu may be switched off by us, which is fine).</summary>
    private static bool IsStillEligible(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd) || !WindowEnumerator.IsTaskbarWindow(hwnd)) return false;
        if (WindowsSettingsRedirect.IsHandsOff(hwnd)) return false;
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        return IsOnScreenNormal(hwnd, style);
    }

    private static bool IsOnScreenNormal(IntPtr hwnd, long style)
    {
        if (!TryGetFrame(hwnd, out var frame)) return false;
        int w = frame.Right - frame.Left, h = frame.Bottom - frame.Top;
        if (w < 260 || h < 120) return false;
        if (IsFullscreen(hwnd, frame, style)) return false;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (!SelfElevated && ElevatedCache.GetOrAdd(pid, IsElevated)) return false;
        return true;
    }

    internal static bool TryGetFrame(IntPtr hwnd, out Win32.RECT frame)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf<Win32.RECT>()) == 0 && frame.Right > frame.Left)
            return true;
        return Win32.GetWindowRect(hwnd, out frame);
    }

    internal static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    internal static bool TryGetMonitor(IntPtr hwnd, out Win32.RECT monitor)
    {
        monitor = default;
        IntPtr handle = Win32.MonitorFromWindow(hwnd, 2);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (handle == IntPtr.Zero || !Win32.GetMonitorInfo(handle, ref info)) return false;
        monitor = info.rcMonitor;
        return true;
    }

    internal static bool TryGetWork(IntPtr hwnd, out Win32.RECT work)
    {
        work = default;
        IntPtr handle = Win32.MonitorFromWindow(hwnd, 2);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (handle == IntPtr.Zero || !Win32.GetMonitorInfo(handle, ref info)) return false;
        work = info.rcWork;
        return true;
    }

    internal static bool IsFullscreen(IntPtr hwnd, Win32.RECT frame, long style)
    {
        if (!TryGetMonitor(hwnd, out var m)) return false;
        bool covers = frame.Left <= m.Left && frame.Top <= m.Top && frame.Right >= m.Right && frame.Bottom >= m.Bottom;
        if (!covers) return false;
        if (!Win32.IsZoomed(hwnd) || (style & WS_CAPTION) != WS_CAPTION) return true;
        // Maximized with a caption style: a plain maximized window only covers the work area (below the
        // menu bar, above the dock). Covering the whole monitor anyway means a player or game went
        // fullscreen while keeping its caption style (Windows 11 Media Player, some Electron apps).
        // On a monitor with nothing reserved, work area and monitor are the same: there it stays "maximized".
        return TryGetWork(hwnd, out var work) && (work.Left > m.Left || work.Top > m.Top || work.Right < m.Right || work.Bottom < m.Bottom);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> ElevatedCache = new();

    private static bool IsElevated(uint pid)
    {
        IntPtr process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return true; // protected: treat as out of reach
        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return true;
            try
            {
                return GetTokenInformation(token, 20 /* TokenElevation */, out int elevated, sizeof(int), out _) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public void Dispose()
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() =>
        {
            _sweep?.Stop();
            foreach (var hook in _hooks) Win32.UnhookWinEvent(hook);
            _hooks.Clear();
            foreach (var host in _bars.Values.ToList()) host.Close();
            _bars.Clear();
            CaptionStyle.RestoreAll();
            GlobalMenus.RestoreAll();
            dispatcher.InvokeShutdown();
        });
    }
}

/// <summary>
/// Switches Windows' own caption buttons off on windows that get a full macOS title bar (removes the
/// system menu style, which removes the buttons and the icon), and puts them back. The list is kept on
/// disk so a crash never leaves windows without their buttons.
/// </summary>
internal static class CaptionStyle
{
    private const uint SWP_FLAGS = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020 /* FRAMECHANGED */ | 0x0200 | 0x4000 /* ASYNC */;
    private static readonly object Gate = new();
    private static readonly HashSet<IntPtr> Stripped = new();
    private static readonly string StateFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "caption-buttons-off.txt");

    public static void Strip(IntPtr hwnd)
    {
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        if ((style & MacWindowButtons.WS_SYSMENU) == 0) return;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style & ~MacWindowButtons.WS_SYSMENU));
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FLAGS);
        lock (Gate)
        {
            if (Stripped.Add(hwnd)) Save();
        }
    }

    public static void Restore(IntPtr hwnd)
    {
        lock (Gate)
        {
            if (!Stripped.Remove(hwnd)) return;
            Save();
        }
        Give(hwnd);
    }

    public static void RestoreAll()
    {
        List<IntPtr> all;
        lock (Gate)
        {
            all = Stripped.ToList();
            Stripped.Clear();
            Save();
        }
        foreach (var hwnd in all) Give(hwnd);
    }

    public static void RestoreLeftovers()
    {
        try
        {
            if (!System.IO.File.Exists(StateFile)) return;
            foreach (var line in System.IO.File.ReadAllLines(StateFile))
                if (long.TryParse(line, out long value)) Give(new IntPtr(value));
            System.IO.File.Delete(StateFile);
        }
        catch (Exception ex)
        {
            CrashLog.Write("CaptionStyle.RestoreLeftovers", ex);
        }
    }

    private static void Give(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd)) return;
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        if ((style & MacWindowButtons.WS_SYSMENU) != 0 || (style & MacWindowButtons.WS_CAPTION) != MacWindowButtons.WS_CAPTION) return;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style | MacWindowButtons.WS_SYSMENU));
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FLAGS);
    }

    private static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StateFile)!);
            if (Stripped.Count == 0) { if (System.IO.File.Exists(StateFile)) System.IO.File.Delete(StateFile); return; }
            System.IO.File.WriteAllLines(StateFile, Stripped.Select(h => h.ToInt64().ToString()));
        }
        catch { /* best effort */ }
    }
}

/// <summary>Runs one title bar on its own UI thread (see <see cref="MacWindowButtons"/> for why).</summary>
internal sealed class TitleBarHost
{
    public IntPtr Target { get; }
    private Dispatcher? _dispatcher;
    private TitleBar? _bar;
    private volatile bool _closed;

    public TitleBarHost(IntPtr target, Action<IntPtr> minimize)
    {
        Target = target;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MacWindowButtons.SetThreadDpiAwarenessContext(MacWindowButtons.PerMonitorV2);
            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set(); // the window is made below, as the first queued job
            _dispatcher.BeginInvoke(() =>
            {
                try { _bar = new TitleBar(target, minimize, Close); }
                catch (Exception ex) { CrashLog.Write("TitleBar", ex); }
            }, DispatcherPriority.Send);
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "MacDock title bar" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(2000);
    }

    /// <summary>Runs <paramref name="action"/> on the title bar's thread (never blocks the caller).</summary>
    public void Post(Action<TitleBar> action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null || _closed) return;
        dispatcher.BeginInvoke(() =>
        {
            if (_bar == null || _closed) return;
            try { action(_bar); }
            catch (Exception ex) { CrashLog.Write("TitleBar.Post", ex); }
        }, DispatcherPriority.Send);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        var dispatcher = _dispatcher;
        dispatcher?.BeginInvoke(() =>
        {
            try { _bar?.Destroy(); } catch { /* already gone with its owner */ }
            dispatcher.InvokeShutdown();
        });
    }
}

/// <summary>The macOS title bar (or just the traffic lights) of one app window.</summary>
internal sealed class TitleBar
{
    // ---------------- Win32 ----------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwnd; public uint hover; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WM_DESTROY = 0x0002, WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021, WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_MOUSELEAVE = 0x02A3;
    private const uint WM_CAPTURECHANGED = 0x0215, WM_DPICHANGED = 0x02E0;
    private const uint WM_NCLBUTTONDOWN = 0x00A1, WM_NCHITTEST = 0x0084;
    private const int HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20, HTHELP = 21;
    private const int SC_CLOSE = 0xF060;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private const int SM_CXDRAG = 68, SM_CYDRAG = 69;

    private const string ClassName = "MacDockTitleBar";
    private static readonly WndProc Proc = StaticProc; // one delegate for the whole process, never collected
    private static readonly object ClassGate = new();
    private static bool _registered;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, TitleBar> Instances = new();

    // ---------------- State ----------------

    private enum Kind { Unknown, Full, Strip }

    public IntPtr Target { get; }
    private readonly Action<IntPtr> _minimize;
    private readonly Action _closeHost;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _watch, _sampleTimer;
    private Kind _kind;
    private int _classifyTries;
    private bool _active, _hover, _shown, _tracking, _dragArmed;
    private int _pressed = -1;
    private string _title = "";
    private Color _fill = Color.FromRgb(0xF6, 0xF6, 0xF6);
    private Color? _edge;
    private bool _hasFill;

    // Placement, physical pixels.
    private int _x, _y, _w, _h;
    private double _scale = 1;
    private Rect[] _lights = Array.Empty<Rect>();

    // Lights mode: where the app's own buttons are, from the frame's top right corner.
    private int _fromRight, _fromTop, _areaWidth, _areaHeight;
    private bool? _probedZoomed;
    private uint _probedDpi;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int FromRight, int FromTop, int Width, int Height)> AreaCache = new();
    private string? _exeName;

    // Apps that draw their own title bar: the lights go top-left (_hwnd), and a second window (_patch),
    // filled with the title bar's color, hides the app's own buttons on the right.
    private IntPtr _patch;
    private int _px, _py, _pw, _ph;
    private Color? _patchColor;
    private POINT _downScreen;

    // Drawing surfaces.
    private readonly Surface _surface = new(), _patchSurface = new();

    public TitleBar(IntPtr target, Action<IntPtr> minimize, Action closeHost)
    {
        Target = target;
        _minimize = minimize;
        _closeHost = closeHost;
        Register();
        // Owned by the app window (parent of a popup = owner): Windows keeps it right above that window
        // and hides it when the window is minimized.
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "MacDock title bar",
            WS_POPUP, -32000, -32000, 1, 1, target, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        Instances[_hwnd] = this;

        _sampleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _sampleTimer.Tick += (_, _) => { _sampleTimer.Stop(); Safe(SampleColor); };
        // Safety net: whatever events were missed, the bar never stays where its window is not.
        _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) }; // events do the real work; this is only a safety net
        _watch.Tick += (_, _) => { if (!GameMode.Active) Safe(Update); };
        _watch.Start();
        ReadTitle();
    }

    /// <summary>A title bar problem is logged, never allowed to take the dock down.</summary>
    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { CrashLog.Write("TitleBar", ex); }
    }

    private static void Register()
    {
        lock (ClassGate)
        {
            if (_registered) return;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0x0008 /* CS_DBLCLKS */,
                lpfnWndProc = Proc,
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _registered = true;
        }
    }

    /// <summary>
    /// Windows 11 rounds every window's corners and draws a 1 px border around it. Under a macOS title
    /// bar that leaves a notch at each top corner and a line between bar and window, so while the window
    /// has a bar its corners are square and its border is off; both go back to normal afterwards.
    /// (Windows 10 has neither and ignores these settings.)
    /// </summary>
    private void Windows11Frame(bool square)
    {
        if (Environment.OSVersion.Version.Build < 22000 || !Win32.IsWindow(Target)) return;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
        int corners = square ? 1 /* DONOTROUND */ : 0 /* DEFAULT */;
        int border = square ? unchecked((int)0xFFFFFFFE) /* NONE */ : unchecked((int)0xFFFFFFFF) /* DEFAULT */;
        MacWindowButtons.DwmSetWindowAttribute(Target, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        MacWindowButtons.DwmSetWindowAttribute(Target, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    public void Destroy()
    {
        _watch.Stop();
        _sampleTimer.Stop();
        Windows11Frame(square: false);
        if (_kind == Kind.Full)
        {
            CaptionStyle.Restore(Target);
            GlobalMenus.Restore(Target);
        }
        GiveRoomBack();
        Instances.TryRemove(_hwnd, out _);
        DestroyWindow(_hwnd);
        _surface.Free();
        if (_patch != IntPtr.Zero)
        {
            Instances.TryRemove(_patch, out _);
            DestroyWindow(_patch);
            _patchSurface.Free();
        }
    }

    private IntPtr CreateBarWindow() =>
        CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "MacDock title bar",
            WS_POPUP, -32000, -32000, 1, 1, Target, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

    // ---------------- State changes ----------------

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Render();
        if (active) SampleColorSoon(); // apps often change their title bar color with focus
    }

    public void TitleChanged()
    {
        if (ReadTitle()) Render();
    }

    private bool ReadTitle()
    {
        var text = new System.Text.StringBuilder(512);
        InternalGetWindowText(Target, text, text.Capacity); // never waits on the app
        string title = text.ToString();
        if (title == _title) return false;
        _title = title;
        return true;
    }

    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        ShowWindow(_hwnd, 0);
        if (_patch != IntPtr.Zero) ShowWindow(_patch, 0);
    }

    // ---------------- Placement ----------------

    /// <summary>Follows the window: position, size, kind, visibility.</summary>
    public void Update()
    {
        if (_dragging) return; // the drag places window and bar itself
        if (!Win32.IsWindow(Target)) { _closeHost(); return; }
        if (Win32.IsIconic(Target) || !Win32.IsWindowVisible(Target) || MacWindowButtons.IsCloaked(Target) ||
            !MacWindowButtons.TryGetFrame(Target, out var frame))
        {
            Hide();
            return;
        }
        long style = Win32.GetWindowLongPtr(Target, Win32.GWL_STYLE).ToInt64();
        if (MacWindowButtons.IsFullscreen(Target, frame, style)) { Hide(); return; }

        uint dpi = MacWindowButtons.GetDpiForWindow(Target);
        if (dpi == 0) dpi = 96;
        _scale = dpi / 96.0;
        if (_kind == Kind.Unknown && !Classify(frame, style)) { Hide(); return; }

        int x, y, w, h;
        if (_kind == Kind.Full)
        {
            if ((style & MacWindowButtons.WS_SYSMENU) != 0) CaptionStyle.Strip(Target); // the app may have put it back
            GlobalMenus.Detach(Target); // its File / Edit / View menus go to the top bar, like on a Mac
            int bottom = CaptionBottom();
            y = frame.Top;
            if (Win32.IsZoomed(Target) && MacWindowButtons.TryGetMonitor(Target, out var monitor)) y = Math.Max(y, monitor.Top);
            x = frame.Left;
            w = frame.Right - frame.Left;
            h = bottom - y;
            if (h < S(16) || h > S(80)) { Hide(); return; }
        }
        else
        {
            bool zoomed = Win32.IsZoomed(Target);
            if (_probedZoomed != zoomed || _probedDpi != dpi) Probe(frame, zoomed, dpi);
            // A macOS title bar on top of the window, so the app's own tabs and buttons stay untouched
            // below it. When there is no room above (maximized, snapped, at the top of the screen), the
            // window is moved down to make it.
            int barHeight = S(StripHeight);
            if (!_moving && MacWindowButtons.TryGetWork(Target, out var work))
            {
                // Maximized: these apps keep themselves glued to the screen's top edge, so instead the
                // window fills the screen below the bar as a normal window (what zoom does on a Mac).
                if (zoomed)
                {
                    // Apps that put themselves back to maximized right away are left alone (no tug of war).
                    if (_fillRefusals < 2)
                    {
                        if (_filled && DateTime.UtcNow - _filledAt < TimeSpan.FromSeconds(3)) _fillRefusals++;
                        Fill(work);
                    }
                    Hide();
                    return;
                }
                if (_filled && FixFill(frame)) { Hide(); return; }
                if (frame.Top - barHeight < work.Top - 1 && frame.Bottom - (work.Top + barHeight) > S(200) &&
                    MakeRoom(frame, work.Top + barHeight - frame.Top, work))
                {
                    Hide(); // placed when it has moved
                    return;
                }
            }
            x = frame.Left;
            y = frame.Top - barHeight + StripOverlap; // over the window's 1 px top border: no seam
            w = frame.Right - frame.Left;
            h = barHeight;
            // The app's own buttons on the right, covered in its title bar's color.
            int px = frame.Right - _fromRight, pw = _areaWidth;
            int py = frame.Top + _fromTop, ph = _areaHeight;
            if (_patch == IntPtr.Zero)
            {
                _patch = CreateBarWindow();
                if (_patch != IntPtr.Zero) Instances[_patch] = this;
            }
            if (_patch != IntPtr.Zero)
            {
                bool patchResized = pw != _pw || ph != _ph;
                bool patchMoved = px != _px || py != _py;
                _px = px; _py = py; _pw = pw; _ph = ph;
                if (patchResized || !_shown) RenderPatch(force: true);
                else if (patchMoved) SetWindowPos(_patch, IntPtr.Zero, px, py, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }

        bool resized = w != _w || h != _h;
        bool moved = x != _x || y != _y;
        _x = x; _y = y; _w = w; _h = h;
        if (resized || !_shown) Render();
        else if (moved) SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        if (!_shown)
        {
            _shown = true;
            Windows11Frame(square: true);
            PlaceAboveOwner(_hwnd, show: true);
            if (_patch != IntPtr.Zero) PlaceAboveOwner(_patch, show: true);
            SampleColorSoon();
        }
        else
        {
            KeepAboveOwnerOnly(_hwnd);
            if (_patch != IntPtr.Zero) KeepAboveOwnerOnly(_patch);
            if (!_hasFill && !_sampleTimer.IsEnabled) SampleColor(); // no color yet (the spot was covered): keep trying
        }
    }

    /// <summary>Height of the title bar on top of apps that draw their own, in DIPs (as tall as Windows' caption).</summary>
    private const double StripHeight = 30;

    /// <summary>The bar reaches this many pixels into the window, over the 1 px border Windows 10 draws on its top edge.</summary>
    private const int StripOverlap = 1;

    private bool _moving;
    private DateTime _roomTriedAt;
    private Win32.RECT _roomTriedFor;

    /// <summary>The user is dragging or resizing the window: don't move it under their hand.</summary>
    public void SetMoving(bool moving)
    {
        _moving = moving;
        if (moving && _filled) { _filled = false; _beforeFill = null; } // dragged away: a normal window again, like on a Mac
        if (!moving) Update();
    }

    /// <summary>
    /// Moves the window down by <paramref name="by"/> pixels (keeping its bottom on screen) so the title
    /// bar fits above it. False when it was already tried for this exact position (an app that refuses to
    /// move is left alone, never fought).
    /// </summary>
    private bool MakeRoom(Win32.RECT frame, int by, Win32.RECT work)
    {
        if (frame.SameAs(_roomTriedFor) && DateTime.UtcNow - _roomTriedAt < TimeSpan.FromSeconds(5)) return false;
        _roomTriedFor = frame;
        _roomTriedAt = DateTime.UtcNow;
        if (!Win32.GetWindowRect(Target, out var window)) return false;
        int top = window.Top + by;
        int bottom = window.Bottom;
        // Maximized and snapped windows keep their bottom; others move as a whole unless that pushes them off screen.
        bool keepBottom = Win32.IsZoomed(Target) || frame.Bottom + by > work.Bottom;
        if (!keepBottom) bottom += by;
        const uint flags = 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */ | 0x0200 /* NOOWNERZORDER */ | 0x4000 /* ASYNCWINDOWPOS */;
        SetWindowPos(Target, IntPtr.Zero, window.Left, top, window.Right - window.Left, bottom - top, flags);
        return true;
    }

    // ---------------- Zoom for apps with their own title bar ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public Win32.RECT normal; }
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    private bool _filled;
    private Win32.RECT _fillFrame;
    private DateTime _filledAt;
    private WINDOWPLACEMENT? _beforeFill;

    /// <summary>Makes the window fill the screen below the title bar, as a normal window. Remembers where it
    /// was (for maximized windows, where they go when restored), so zoom can put it back.</summary>
    private int _fillRefusals;

    private void Fill(Win32.RECT work)
    {
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(Target, ref placement)) return;
        if (!_filled) _beforeFill = placement;
        _fillFrame = new Win32.RECT { Left = work.Left, Top = work.Top + S(StripHeight), Right = work.Right, Bottom = work.Bottom };
        // The placement's normal position is in workspace coordinates (from the primary work area's corner).
        var origin = PrimaryWorkOrigin();
        int border = S(7); // Windows 10's invisible resize border, corrected afterwards if different
        var next = placement;
        next.flags = 0;
        next.showCmd = 1; // SW_SHOWNORMAL
        next.normal = new Win32.RECT
        {
            Left = _fillFrame.Left - border - origin.X,
            Top = _fillFrame.Top - origin.Y,
            Right = _fillFrame.Right + border - origin.X,
            Bottom = _fillFrame.Bottom + border - origin.Y,
        };
        SetWindowPlacement(Target, ref next);
        _filled = true;
        _filledAt = DateTime.UtcNow;
    }

    /// <summary>Right after filling: one exact correction (borders differ per app). Later, a window that
    /// no longer fills the screen (moved, resized) is simply a normal window again.</summary>
    private bool FixFill(Win32.RECT frame)
    {
        if (frame.SameAs(_fillFrame)) return false;
        if (DateTime.UtcNow - _filledAt > TimeSpan.FromSeconds(2)) { _filled = false; _beforeFill = null; return false; }
        if (!Win32.GetWindowRect(Target, out var window)) return false;
        const uint flags = 0x0004 | 0x0010 | 0x0200 | 0x4000;
        SetWindowPos(Target, IntPtr.Zero,
            window.Left + (_fillFrame.Left - frame.Left), window.Top + (_fillFrame.Top - frame.Top),
            window.Right - window.Left + (_fillFrame.Right - frame.Right) - (_fillFrame.Left - frame.Left),
            window.Bottom - window.Top + (_fillFrame.Bottom - frame.Bottom) - (_fillFrame.Top - frame.Top), flags);
        _filledAt = DateTime.UtcNow.AddSeconds(-1.5); // one more look, then settle
        return true;
    }

    /// <summary>Zoom: back to where the window was, or fill the screen below the title bar.</summary>
    private void ZoomStrip()
    {
        if (!MacWindowButtons.TryGetWork(Target, out var work) || !MacWindowButtons.TryGetFrame(Target, out var frame)) return;
        var fill = new Win32.RECT { Left = work.Left, Top = work.Top + S(StripHeight), Right = work.Right, Bottom = work.Bottom };
        int slack = S(24);
        bool Near(int a, int b) => Math.Abs(a - b) <= slack;
        bool atFill = Near(frame.Left, fill.Left) && Near(frame.Top, fill.Top) && Near(frame.Right, fill.Right) && Near(frame.Bottom, fill.Bottom);
        if (!atFill) { Fill(work); return; }

        _filled = false;
        var before = _beforeFill;
        _beforeFill = null;
        bool beforeIsSmaller = before is { } b &&
            (b.normal.Right - b.normal.Left < fill.Right - fill.Left - S(60) || b.normal.Bottom - b.normal.Top < fill.Bottom - fill.Top - S(60));
        if (before is { } old && beforeIsSmaller)
        {
            old.showCmd = 1; // SW_SHOWNORMAL, at its old normal position
            old.flags = 0;
            SetWindowPlacement(Target, ref old);
            return;
        }
        // Nowhere smaller to go back to: a centered window, 3/4 of the screen (like a fresh window on a Mac).
        if (!Win32.GetWindowRect(Target, out var window)) return;
        int w = (fill.Right - fill.Left) * 3 / 4, h = (fill.Bottom - fill.Top) * 3 / 4;
        int x = fill.Left + (fill.Right - fill.Left - w) / 2, y = fill.Top + (fill.Bottom - fill.Top - h) / 2;
        int l = frame.Left - window.Left, t = frame.Top - window.Top, r = window.Right - frame.Right, bt = window.Bottom - frame.Bottom;
        const uint flags = 0x0004 | 0x0010 | 0x0200 | 0x4000;
        SetWindowPos(Target, IntPtr.Zero, x - l, y - t, w + l + r, h + t + bt, flags);
    }

    /// <summary>When the bar goes away, a filled window is maximized again (with its old normal position).</summary>
    private void GiveRoomBack()
    {
        if (!_filled || _beforeFill is not { } before || !Win32.IsWindow(Target)) return;
        before.showCmd = 3; // SW_SHOWMAXIMIZED
        before.flags = 0;
        SetWindowPlacement(Target, ref before);
        _filled = false;
    }

    private static POINT PrimaryWorkOrigin()
    {
        IntPtr primary = MonitorFromPoint(new POINT(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (primary == IntPtr.Zero || !Win32.GetMonitorInfo(primary, ref info)) return new POINT();
        return new POINT { X = info.rcWork.Left - info.rcMonitor.Left, Y = info.rcWork.Top - info.rcMonitor.Top };
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    private void RenderPatch(bool force)
    {
        if (_patch == IntPtr.Zero || _pw <= 0 || _ph <= 0) return;
        if (!force && _patchColor == _fill) return;
        _patchColor = _fill;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(_fill), null, new Rect(0, 0, _pw, _ph));
        var bitmap = new RenderTargetBitmap(_pw, _ph, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        _patchSurface.Present(_patch, bitmap, _px, _py);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    private const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3, GW_OWNER = 4, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;

    /// <summary>
    /// Puts the bar right above its window in the Z order. (Showing an owned window would otherwise put it
    /// on top of every window, also over the ones in front of its own.)
    /// </summary>
    private void PlaceAboveOwner(IntPtr bar, bool show)
    {
        IntPtr prev = GetWindow(Target, GW_HWNDPREV);
        while (prev != IntPtr.Zero && (prev == _hwnd || prev == _patch)) prev = GetWindow(prev, GW_HWNDPREV);
        long targetEx = Win32.GetWindowLongPtr(Target, Win32.GWL_EXSTYLE).ToInt64();
        bool prevTopmost = prev != IntPtr.Zero && (Win32.GetWindowLongPtr(prev, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOPMOST) != 0;
        bool targetTopmost = (targetEx & Win32.WS_EX_TOPMOST) != 0;
        IntPtr after = prev == IntPtr.Zero || (prevTopmost && !targetTopmost) ? IntPtr.Zero /* HWND_TOP */ : prev;
        uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | (show ? SWP_SHOWWINDOW : 0);
        SetWindowPos(bar, after, 0, 0, 0, 0, flags);
    }

    /// <summary>Only the window itself (and its own dialogs) may be right under the bar; anything else
    /// means the bar ended up in front of other windows: put it back.</summary>
    private void KeepAboveOwnerOnly(IntPtr bar)
    {
        IntPtr next = GetWindow(bar, GW_HWNDNEXT);
        int guard = 0;
        while (next != IntPtr.Zero && next != Target && guard++ < 64 &&
               (!Win32.IsWindowVisible(next) || GetWindow(next, GW_OWNER) == Target))
            next = GetWindow(next, GW_HWNDNEXT);
        if (next != Target) PlaceAboveOwner(bar, show: false);
    }

    private int S(double v) => (int)Math.Round(v * _scale);

    [StructLayout(LayoutKind.Sequential)]
    private struct TITLEBARINFO { public int cbSize; public Win32.RECT rcTitleBar; public int s0, s1, s2, s3, s4, s5; }
    [DllImport("user32.dll")] private static extern bool GetTitleBarInfo(IntPtr hwnd, ref TITLEBARINFO info);

    /// <summary>Bottom of the window's caption (above its menu bar, if it has one), screen pixels.</summary>
    private int CaptionBottom()
    {
        var info = new TITLEBARINFO { cbSize = Marshal.SizeOf<TITLEBARINFO>() };
        if (GetTitleBarInfo(Target, ref info) && info.rcTitleBar.Bottom > info.rcTitleBar.Top) return info.rcTitleBar.Bottom;
        var origin = new POINT();
        ClientToScreen(Target, ref origin);
        return origin.Y;
    }

    /// <summary>
    /// Does Windows draw this window's title bar (then it gets a full macOS title bar and Windows'
    /// buttons are switched off), or does the app draw its own (then only the lights go over its buttons)?
    /// </summary>
    private bool Classify(Win32.RECT frame, long style)
    {
        int caption = CaptionBottom() - frame.Top;
        // Windows draws the caption only when the window's content starts below it. Apps that draw
        // their own title bar (Chrome, Electron) keep the caption style but start their content at the top.
        var origin = new POINT();
        ClientToScreen(Target, ref origin);
        bool contentBelow = origin.Y - frame.Top >= caption - S(2);
        bool standardCaption = contentBelow && (style & MacWindowButtons.WS_CAPTION) == MacWindowButtons.WS_CAPTION && caption >= S(18) && caption <= S(64);
        if (standardCaption)
        {
            int ht = HitTest((frame.Left + frame.Right) / 2, frame.Top + caption / 2);
            if (ht < 0)
            {
                // Busy app: ask again on the next update (a few tries), then settle for the lights.
                if (++_classifyTries < 5) return false;
            }
            else if (ht == HTCAPTION)
            {
                _kind = Kind.Full;
                CaptionStyle.Strip(Target);
                return true;
            }
        }
        _kind = Kind.Strip;
        return true;
    }

    private int HitTest(int x, int y)
    {
        IntPtr lp = new((y << 16) | (x & 0xFFFF));
        if (Win32.SendMessageTimeout(Target, WM_NCHITTEST, IntPtr.Zero, lp, Win32.SMTO_ABORTIFHUNG, 40, out var result) == IntPtr.Zero) return -1;
        return (int)result.ToInt64();
    }

    private string CacheKey(bool zoomed, uint dpi)
    {
        var cls = new System.Text.StringBuilder(128);
        Win32.GetClassName(Target, cls, cls.Capacity);
        if (_exeName == null)
        {
            Win32.GetWindowThreadProcessId(Target, out uint pid);
            try { _exeName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { _exeName = pid.ToString(); }
        }
        return $"{_exeName}|{cls}|{zoomed}|{dpi}";
    }

    private void Probe(Win32.RECT frame, bool zoomed, uint dpi)
    {
        _probedZoomed = zoomed;
        _probedDpi = dpi;
        string key = CacheKey(zoomed, dpi);
        if (!AreaCache.TryGetValue(key, out var area))
        {
            area = Measure(frame, zoomed, out bool complete);
            if (complete) AreaCache[key] = area;
            else _probedZoomed = null; // the app was busy: measure again on the next update
        }
        (_fromRight, _fromTop, _areaWidth, _areaHeight) = area;
    }

    /// <summary>
    /// Finds the app's own minimize / maximize / close buttons by asking the window what is under a few
    /// points (a walk from the right edge, then halving the gap). Apps that draw their buttons in web
    /// content don't report them: then Windows' button area, or the standard size, is used. Time-boxed.
    /// </summary>
    private (int, int, int, int) Measure(Win32.RECT frame, bool zoomed, out bool complete)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool failed = false;
        int Hit(int x, int y)
        {
            if (failed || clock.ElapsedMilliseconds > 90) { failed = true; return -1; }
            int ht = HitTest(x, y);
            if (ht < 0) failed = true;
            return ht;
        }
        static bool IsButton(int ht) => ht is HTMINBUTTON or HTMAXBUTTON or HTCLOSE or HTHELP;

        int cx = frame.Right - S(12), row = int.MinValue;
        foreach (int dy in new[] { S(zoomed ? 10 : 12), S(6), S(18) })
        {
            if (IsButton(Hit(cx, frame.Top + dy))) { row = frame.Top + dy; break; }
            if (failed) break;
        }
        int? left = null, top = null, bottom = null;
        if (row != int.MinValue)
        {
            int inside = cx, outside = int.MinValue;
            for (int x = inside - S(8); x > frame.Right - S(300) && !failed; x -= S(8))
            {
                if (IsButton(Hit(x, row))) inside = x;
                else { outside = x; break; }
            }
            if (outside != int.MinValue)
                while (inside - outside > 1 && !failed)
                {
                    int mid = (inside + outside) / 2;
                    if (IsButton(Hit(mid, row))) inside = mid; else outside = mid;
                }
            int yOut = frame.Top - 1, yIn = row;
            while (yIn - yOut > 1 && !failed)
            {
                int mid = (yIn + yOut) / 2;
                if (IsButton(Hit(cx, mid))) yIn = mid; else yOut = mid;
            }
            int bIn = row, bOut = frame.Top + S(80);
            while (bOut - bIn > 1 && !failed)
            {
                int mid = (bIn + bOut) / 2;
                if (IsButton(Hit(cx, mid))) bIn = mid; else bOut = mid;
            }
            if (!failed && bIn - yIn >= S(14) && frame.Right - inside >= S(60))
            {
                left = inside - S(2);
                top = yIn;
                bottom = bIn + 1;
            }
        }
        complete = !failed;

        // Windows' own idea of the button area, when it has one (standard caption buttons can be drawn
        // wider than their hit areas): the bar covers both.
        if (Win32.GetWindowRect(Target, out var window) &&
            Win32.DwmGetWindowAttribute(Target, MacWindowButtons.DWMWA_CAPTION_BUTTON_BOUNDS, out Win32.RECT bounds, Marshal.SizeOf<Win32.RECT>()) == 0 &&
            bounds.Right - bounds.Left is > 60 and < 400 && bounds.Bottom - bounds.Top is > 14 and < 90)
        {
            int bl = window.Left + bounds.Left, bt = Math.Max(frame.Top, window.Top + bounds.Top), bb = window.Top + bounds.Bottom;
            // Only when it agrees with the window's right edge (some apps report stale bounds).
            if (Math.Abs(window.Left + bounds.Right - frame.Right) <= S(12))
            {
                left = Math.Min(left ?? int.MaxValue, bl);
                top = Math.Min(top ?? int.MaxValue, bt);
                bottom = Math.Max(bottom ?? int.MinValue, bb);
            }
        }

        if (left is int l && top is int t && bottom is int b && b - t >= S(14) && frame.Right - l is var fromRight && fromRight <= S(300))
            return (fromRight, Math.Max(0, t - frame.Top), Math.Max(S(80), fromRight), b - Math.Max(frame.Top, t));
        // Standard Windows 10 caption buttons: 3 x 46 px, 30 px high (plus the top border).
        return (S(139), 0, S(139), S(zoomed ? 30 : 31));
    }

    // ---------------- Drawing ----------------

    private static readonly FontFamily TitleFont = PickFont();

    private static FontFamily PickFont()
    {
        return MacFonts.Text;
    }

    private static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>A rectangle with its two top corners rounded (square when the radius is 0).</summary>
    private static Geometry TopRounded(Rect r, double radius)
    {
        if (radius <= 0) return new RectangleGeometry(r);
        var figure = new PathFigure { StartPoint = new Point(r.Left, r.Bottom), IsClosed = true };
        var size = new Size(radius, radius);
        figure.Segments.Add(new LineSegment(new Point(r.Left, r.Top + radius), false));
        figure.Segments.Add(new ArcSegment(new Point(r.Left + radius, r.Top), size, 0, false, SweepDirection.Clockwise, false));
        figure.Segments.Add(new LineSegment(new Point(r.Right - radius, r.Top), false));
        figure.Segments.Add(new ArcSegment(new Point(r.Right, r.Top + radius), size, 0, false, SweepDirection.Clockwise, false));
        figure.Segments.Add(new LineSegment(new Point(r.Right, r.Bottom), false));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    private void Render()
    {
        if (_w <= 0 || _h <= 0) return;
        bool dark = Luma(_fill) < 0.5;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Apps with their own title bar get a bar of their own on top: rounded top corners like a
            // macOS window. Over Windows' caption (Full) it stays square, or that caption would show
            // through the corners.
            double radius = _kind == Kind.Strip ? Math.Round(10 * _scale) : 0;
            if (_edge is { } edge && edge != _fill)
            {
                dc.DrawGeometry(new SolidColorBrush(edge), null, TopRounded(new Rect(0, 0, _w, _h), radius));
                dc.DrawGeometry(new SolidColorBrush(_fill), null, TopRounded(new Rect(1, 1, _w - 2, _h - 1), Math.Max(0, radius - 1)));
            }
            else dc.DrawGeometry(new SolidColorBrush(_fill), null, TopRounded(new Rect(0, 0, _w, _h), radius));
            double d = 12 * _scale, gap = 8 * _scale;
            double total = d * 3 + gap * 2;
            double cy = _h / 2.0;
            double startX = Math.Round(13 * _scale); // top-left, like macOS

            _lights = new Rect[3];
            for (int i = 0; i < 3; i++)
                _lights[i] = new Rect(startX + i * (d + gap), cy - d / 2, d, d);
            DrawLights(dc, d, dark);

            {
                // Hairline between the title bar and the content, like macOS (apps with their own title
                // bar: none, the bar flows into the app's own tab row).
                if (_kind == Kind.Full) dc.DrawRectangle(dark ? Brush(0x80, 0, 0, 0) : Brush(0x1A, 0, 0, 0), null, new Rect(0, _h - Math.Max(1, Math.Round(_scale * 0.8)), _w, Math.Max(1, Math.Round(_scale * 0.8))));
                double side = startX + total + 16 * _scale;
                double room = _w - side * 2;
                if (room > 30 * _scale && _title.Length > 0)
                {
                    var color = dark ? Brush((byte)(_active ? 0xE6 : 0x80), 0xFF, 0xFF, 0xFF) : Brush((byte)(_active ? 0xD9 : 0x70), 0, 0, 0);
                    var text = new FormattedText(_title, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(TitleFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 13 * _scale, color, 1.0)
                    {
                        MaxTextWidth = room,
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = TextAlignment.Center,
                    };
                    dc.DrawText(text, new Point(side, Math.Round(cy - text.Height / 2)));
                }
            }
        }
        var bitmap = new RenderTargetBitmap(_w, _h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        _surface.Present(_hwnd, bitmap, _x, _y);
    }

    private void DrawLights(DrawingContext dc, double d, bool dark)
    {
        bool lit = _active || _hover;
        (Color fill, Color rim)[] colors = lit
            ? new[] { (Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xE2, 0x46, 0x3F)), (Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0xE1, 0xA1, 0x16)), (Color.FromRgb(0x28, 0xC8, 0x40), Color.FromRgb(0x1A, 0xAB, 0x29)) }
            : Enumerable.Repeat(dark ? (Color.FromRgb(0x4D, 0x4D, 0x50), Color.FromRgb(0x45, 0x45, 0x48)) : (Color.FromRgb(0xD5, 0xD5, 0xD8), Color.FromRgb(0xC4, 0xC4, 0xC7)), 3).ToArray();
        var glyphPen = new Pen(Brush(0x9A, 0x40, 0x00, 0x00), Math.Max(1, 1.1 * _scale)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (int i = 0; i < 3; i++)
        {
            var r = _lights[i];
            var fill = colors[i].fill;
            if (_pressed == i) fill = Color.FromRgb((byte)(fill.R * 0.8), (byte)(fill.G * 0.8), (byte)(fill.B * 0.8));
            var center = new Point(r.X + d / 2, r.Y + d / 2);
            dc.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(colors[i].rim), Math.Max(0.6, 0.5 * _scale)), center, d / 2 - 0.25 * _scale, d / 2 - 0.25 * _scale);
            if (!_hover) continue;
            double g = d * 0.22; // glyph half size
            if (i == 0)
            {
                dc.DrawLine(glyphPen, new Point(center.X - g, center.Y - g), new Point(center.X + g, center.Y + g));
                dc.DrawLine(glyphPen, new Point(center.X + g, center.Y - g), new Point(center.X - g, center.Y + g));
            }
            else if (i == 1)
            {
                dc.DrawLine(glyphPen, new Point(center.X - g * 1.2, center.Y), new Point(center.X + g * 1.2, center.Y));
            }
            else
            {
                var glyph = Brush(0x9A, 0x00, 0x40, 0x00);
                double a = g * 1.15;
                var up = new StreamGeometry();
                using (var ctx = up.Open())
                {
                    ctx.BeginFigure(new Point(center.X - a, center.Y - a), true, true);
                    ctx.LineTo(new Point(center.X + a * 0.45, center.Y - a), true, false);
                    ctx.LineTo(new Point(center.X - a, center.Y + a * 0.45), true, false);
                    ctx.BeginFigure(new Point(center.X + a, center.Y + a), true, true);
                    ctx.LineTo(new Point(center.X - a * 0.45, center.Y + a), true, false);
                    ctx.LineTo(new Point(center.X + a, center.Y - a * 0.45), true, false);
                }
                up.Freeze();
                dc.DrawGeometry(glyph, null, up);
            }
        }
    }

    /// <summary>A layered window's pixels: a 32-bit picture kept between updates, put on screen in one
    /// call (position, size and pixels together).</summary>
    private sealed class Surface
    {
        private IntPtr _memDc, _dib, _bits;
        private int _w, _h;

        public void Present(IntPtr hwnd, BitmapSource bitmap, int x, int y)
        {
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                if (_memDc == IntPtr.Zero || _w != w || _h != h)
                {
                    Free();
                    _memDc = CreateCompatibleDC(screen);
                    var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                    _dib = CreateDIBSection(_memDc, ref info, 0, out _bits, IntPtr.Zero, 0);
                    if (_dib == IntPtr.Zero) { Free(); return; }
                    SelectObject(_memDc, _dib);
                    _w = w;
                    _h = h;
                }
                bitmap.CopyPixels(Int32Rect.Empty, _bits, w * h * 4, w * 4);
                var pos = new POINT { X = x, Y = y };
                var size = new SIZE { W = w, H = h };
                var src = new POINT();
                var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(hwnd, screen, ref pos, ref size, _memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public void Free()
        {
            if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            _memDc = _dib = _bits = IntPtr.Zero;
            _w = _h = 0;
        }
    }

    // ---------------- Color ----------------

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

    // Light / dark mode or the accent changed: apps repaint in the new colors, so every bar looks again
    // (right away and once more after slow apps have caught up).
    static TitleBar() => Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => ResampleAll();

    private static void ResampleAll()
    {
        foreach (var bar in Instances.Values.Distinct().ToList())
        {
            bar._ui.BeginInvoke(() =>
            {
                bar.SampleColorSoon();
                var again = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1200) };
                again.Tick += (_, _) => { again.Stop(); bar.SampleColorSoon(); };
                again.Start();
            });
        }
    }

    public void SampleColorSoon()
    {
        _sampleTimer.Stop();
        _sampleTimer.Start();
    }

    /// <summary>
    /// The bar takes the color of what it sits on: for a full title bar, the top row of the window's
    /// content (macOS title bars blend into the window); for the lights, the app's title bar just left of them.
    /// </summary>
    /// <summary>
    /// The 1 px border Windows 10 draws down the window's sides (gray, or the accent color). The bar
    /// carries it on, so window and bar read as one piece. Null when the window has none.
    /// </summary>
    private Color? SampleEdge()
    {
        int y = _y + _h + S(40);
        var colors = new List<Color>();
        foreach (int x in new[] { _x, _x + _w - 1 })
        {
            var p = new POINT { X = x, Y = y };
            var at = WindowFromPoint(p);
            if (at == IntPtr.Zero || Win32.GetAncestor(at, MacWindowButtons.GA_ROOT) != Target) continue;
            var inside = new POINT { X = x == _x ? x + S(6) : x - S(6), Y = y };
            if (ReadPixel(p) is { } border && ReadPixel(inside) is { } content && border != content) colors.Add(border);
        }
        return colors.Count > 0 ? colors[0] : null;
    }

    private static Color? ReadPixel(POINT p)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, 1, 1);
        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            if (!BitBlt(dc, 0, 0, 1, 1, screen, p.X, p.Y, 0x00CC0020 /* SRCCOPY */)) return null;
            uint c = GetPixel(dc, 0, 0);
            return c == 0xFFFFFFFF ? null : Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void SampleColor()
    {
        if (!_shown || _w <= 0) return;
        var points = new List<POINT>();
        if (_kind == Kind.Full)
        {
            int y = _y + _h + S(2);
            foreach (double f in new[] { 0.15, 0.35, 0.5, 0.65, 0.85 }) points.Add(new POINT { X = _x + (int)(_w * f), Y = y });
        }
        else
        {
            // Just left of the app's own buttons: its title bar, away from tabs and menus.
            int x = _px - S(3);
            foreach (int dy in new[] { 3, _ph / 2, _ph - 4 }) points.Add(new POINT { X = x, Y = _py + dy });
        }
        var visible = points.Where(p =>
        {
            var at = WindowFromPoint(p);
            return at != IntPtr.Zero && Win32.GetAncestor(at, MacWindowButtons.GA_ROOT) == Target; // not covered
        }).ToList();
        if (visible.Count == 0) return;

        var samples = new List<Color>();
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, 1, 1);
        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            foreach (var p in visible)
            {
                if (!BitBlt(dc, 0, 0, 1, 1, screen, p.X, p.Y, 0x00CC0020 /* SRCCOPY */)) continue;
                uint c = GetPixel(dc, 0, 0);
                if (c != 0xFFFFFFFF) samples.Add(Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF)));
            }
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
        if (samples.Count == 0) return;
        var color = samples.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
        var edge = SampleEdge();
        if (_hasFill && color == _fill && edge == _edge) return;
        _hasFill = true;
        _fill = color;
        _edge = edge;
        Render();
        RenderPatch(force: false);
    }

    // ---------------- Mouse ----------------

    private static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out var bar))
        {
            try
            {
                if (bar.OnMessage(hwnd, msg, lParam, out var result)) return result;
            }
            catch (Exception ex)
            {
                CrashLog.Write("TitleBar.OnMessage", ex);
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private int ButtonAt(POINT p)
    {
        for (int i = 0; i < _lights.Length; i++)
        {
            var r = _lights[i];
            r.Inflate(4 * _scale * 0.5, 6 * _scale);
            if (r.Contains(p.X, p.Y)) return i;
        }
        return -1;
    }

    private bool InLights(POINT p)
    {
        if (_lights.Length < 3) return false;
        var group = Rect.Union(_lights[0], _lights[2]);
        group.Inflate(6 * _scale, 8 * _scale);
        return group.Contains(p.X, p.Y);
    }

    private static POINT ClientPoint(IntPtr lParam)
    {
        long v = lParam.ToInt64();
        return new POINT { X = (short)(v & 0xFFFF), Y = (short)((v >> 16) & 0xFFFF) };
    }

    /// <summary>Mouse on the lights window (<see cref="_hwnd"/>) or on the cover over the app's own
    /// buttons (<see cref="_patch"/>: title bar only, no buttons).</summary>
    private bool OnMessage(IntPtr hwnd, uint msg, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        bool lights = hwnd == _hwnd;
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                result = new IntPtr(3); // MA_NOACTIVATE: the app keeps the focus
                return true;
            case WM_SETCURSOR:
                SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512)));
                result = new IntPtr(1);
                return true;
            case WM_DPICHANGED:
                return true; // the bar sizes itself in pixels
            case WM_MOUSEMOVE:
            {
                if (lights && !_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwnd = _hwnd };
                    _tracking = TrackMouseEvent(ref tme);
                }
                bool hover = lights && InLights(ClientPoint(lParam));
                if (hover != _hover) { _hover = hover; Render(); }
                if (_dragArmed || _dragging) DragStep();
                return true;
            }
            case WM_MOUSELEAVE:
                _tracking = false;
                if (_hover || _pressed >= 0) { _hover = false; _pressed = -1; Render(); }
                return true;
            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
            {
                int button = lights ? ButtonAt(ClientPoint(lParam)) : -1;
                if (button >= 0)
                {
                    _pressed = button;
                    SetCapture(_hwnd);
                    Render();
                    return true;
                }
                Win32.SetForegroundWindow(Target);
                if (msg == WM_LBUTTONDBLCLK)
                {
                    _dragArmed = false;
                    Zoom(); // double-click on the title bar: zoom, like macOS
                    return true;
                }
                Win32.GetCursorPos(out var at);
                _downScreen = new POINT { X = at.X, Y = at.Y };
                _dragArmed = true;
                SetCapture(hwnd);
                _dragPoll.Start(); // the drag also runs without mouse messages (the app may take the capture)
                return true;
            }
            case WM_LBUTTONUP:
            {
                if (_dragging) { EndDrag(); return true; }
                _dragArmed = false;
                int pressed = _pressed;
                _pressed = -1;
                ReleaseCapture();
                if (pressed >= 0)
                {
                    Render();
                    if (lights && ButtonAt(ClientPoint(lParam)) == pressed) Click(pressed);
                }
                return true;
            }
            case WM_CAPTURECHANGED:
                // Not a cancel: the bar shares mouse input with its app window, and the app can take the
                // capture between press and release. The release position decides the click.
                return false;
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    // ---------------- Dragging the window by the bar ----------------
    //
    // MacDock moves the window itself (asynchronously, so a busy app never holds the drag up) and moves
    // its bar in the same step, so bar and window travel together with no lag. The drag runs on mouse
    // messages and on a fast timer, so it keeps going even when the app takes the mouse capture.

    private bool _dragging;
    private POINT _dragFrom;
    private Win32.RECT _dragWindow;
    private int _dragBarX, _dragBarY, _dragPatchX, _dragPatchY;
    private uint _dragDpi;
    private DispatcherTimer? _dragTimer;
    private DispatcherTimer _dragPoll => _dragTimer ??= CreateDragTimer();

    private DispatcherTimer CreateDragTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) => Safe(DragStep);
        return timer;
    }

    private static bool LeftButtonDown()
    {
        int button = Win32.GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01;
        return (GetAsyncKeyState(button) & 0x8000) != 0;
    }

    /// <summary>One step: start once the mouse moved far enough, follow it, end when the button is up.</summary>
    private void DragStep()
    {
        if (!_dragArmed && !_dragging) { _dragPoll.Stop(); return; }
        Win32.GetCursorPos(out var cursor);
        var now = new POINT { X = cursor.X, Y = cursor.Y };
        if (!LeftButtonDown())
        {
            if (_dragging) EndDrag();
            else { _dragArmed = false; _dragPoll.Stop(); ReleaseCapture(); }
            return;
        }
        if (!_dragging)
        {
            uint dpi = (uint)Math.Round(96 * _scale);
            if (Math.Abs(now.X - _downScreen.X) <= GetSystemMetricsForDpi(SM_CXDRAG, dpi) &&
                Math.Abs(now.Y - _downScreen.Y) <= GetSystemMetricsForDpi(SM_CYDRAG, dpi)) return;
            StartDrag();
        }
        DragTo(now);
        uint windowDpi = MacWindowButtons.GetDpiForWindow(Target);
        if (windowDpi != 0 && windowDpi != _dragDpi) Rebase(now, windowDpi);
    }

    /// <summary>
    /// The window crossed onto a monitor with other scaling and Windows resized it: the bar is redrawn at
    /// the new size right away and the window is put back under the cursor at the same spot of its bar,
    /// so bar and window stay one piece across monitors instead of the bar hanging off until the drop.
    /// </summary>
    private void Rebase(POINT now, uint dpi)
    {
        if (!Win32.GetWindowRect(Target, out var window) || !MacWindowButtons.TryGetFrame(Target, out var frame)) return;
        double ratio = dpi / (double)_dragDpi;
        double along = _w > 0 ? Math.Clamp((now.X - _x) / (double)_w, 0, 1) : 0.5;
        int grabY = (int)Math.Round((now.Y - _y) * ratio);
        _dragDpi = dpi;
        _scale = dpi / 96.0;
        int fw = frame.Right - frame.Left;
        int barHeight = _kind == Kind.Strip ? S(StripHeight) : Math.Max(S(16), CaptionBottom() - frame.Top);
        int barX = now.X - (int)Math.Round(along * fw);
        int barY = now.Y - grabY;
        int frameTop = _kind == Kind.Strip ? barY + barHeight - StripOverlap : barY;
        int winX = barX - (frame.Left - window.Left), winY = frameTop - (frame.Top - window.Top);
        const uint async = 0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x4000;
        SetWindowPos(Target, IntPtr.Zero, winX, winY, 0, 0, async);
        // New starting point for the rest of the drag.
        _dragWindow = new Win32.RECT { Left = winX, Top = winY, Right = winX + (window.Right - window.Left), Bottom = winY + (window.Bottom - window.Top) };
        _dragFrom = now;
        _x = barX; _y = barY; _w = fw; _h = barHeight;
        _dragBarX = _x; _dragBarY = _y;
        Render();
        SetWindowPos(_hwnd, IntPtr.Zero, _x, _y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        if (_patch != IntPtr.Zero && _kind == Kind.Strip)
        {
            var moved = new Win32.RECT { Left = barX, Top = frameTop, Right = barX + fw, Bottom = frameTop + (frame.Bottom - frame.Top) };
            Probe(moved, false, dpi);
            _px = moved.Right - _fromRight; _py = moved.Top + _fromTop; _pw = _areaWidth; _ph = _areaHeight;
            _dragPatchX = _px; _dragPatchY = _py;
            RenderPatch(force: true);
            SetWindowPos(_patch, IntPtr.Zero, _px, _py, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
    }

    private void StartDrag()
    {
        _dragArmed = false;
        _dragging = true;
        _dragPoll.Start();
        if (Win32.IsZoomed(Target)) RestoreUnderCursor();
        Win32.GetWindowRect(Target, out _dragWindow);
        _dragDpi = MacWindowButtons.GetDpiForWindow(Target);
        _dragFrom = _downScreen;
        _dragBarX = _x; _dragBarY = _y;
        _dragPatchX = _px; _dragPatchY = _py;
        if (_filled) { _filled = false; _beforeFill = null; } // dragged away: a normal window again, like on a Mac
    }

    /// <summary>A maximized window being dragged comes back to its normal size under the mouse, like macOS.</summary>
    private void RestoreUnderCursor()
    {
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(Target, ref placement) || !Win32.GetWindowRect(Target, out var window)) return;
        int w = placement.normal.Right - placement.normal.Left, h = placement.normal.Bottom - placement.normal.Top;
        double along = (_downScreen.X - window.Left) / (double)Math.Max(1, window.Right - window.Left);
        var origin = PrimaryWorkOrigin();
        int left = _downScreen.X - (int)(w * along), top = _downScreen.Y - S(StripHeight) / 2;
        placement.flags = 0;
        placement.showCmd = 1; // SW_SHOWNORMAL
        placement.normal = new Win32.RECT { Left = left - origin.X, Top = top - origin.Y, Right = left + w - origin.X, Bottom = top + h - origin.Y };
        SetWindowPlacement(Target, ref placement);
        // The bar follows from its new place.
        if (MacWindowButtons.TryGetFrame(Target, out var frame))
        {
            _x = frame.Left;
            _y = _kind == Kind.Strip ? frame.Top - S(StripHeight) + StripOverlap : frame.Top;
        }
    }

    private void DragTo(POINT now)
    {
        int dx = now.X - _dragFrom.X, dy = now.Y - _dragFrom.Y;
        // The title bar never goes under the top bar of the screen (like macOS).
        if (MonitorWork(now) is { } work && _dragBarY + dy < work.Top) dy = work.Top - _dragBarY;
        const uint async = 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */ | 0x0200 /* NOOWNERZORDER */ | 0x4000 /* ASYNCWINDOWPOS */;
        SetWindowPos(Target, IntPtr.Zero, _dragWindow.Left + dx, _dragWindow.Top + dy, 0, 0, async);
        const uint move = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        _x = _dragBarX + dx; _y = _dragBarY + dy;
        SetWindowPos(_hwnd, IntPtr.Zero, _x, _y, 0, 0, move);
        if (_patch != IntPtr.Zero && _kind == Kind.Strip)
        {
            _px = _dragPatchX + dx; _py = _dragPatchY + dy;
            SetWindowPos(_patch, IntPtr.Zero, _px, _py, 0, 0, move);
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragArmed = false;
        _dragPoll.Stop();
        ReleaseCapture();
        Win32.GetCursorPos(out var cursor);
        var at = new POINT { X = cursor.X, Y = cursor.Y };
        Snap(at);
        // Settle on the window's real position once the app has moved.
        var settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        settle.Tick += (_, _) => { settle.Stop(); Safe(Update); };
        settle.Start();
    }

    /// <summary>Released at the top edge of the screen: fill it. At the far left or right edge: half of it
    /// (like window tiling on macOS).</summary>
    private void Snap(POINT at)
    {
        IntPtr monitor = MonitorFromPoint(at, 2 /* NEAREST */);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !Win32.GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;
        int left = Win32.GetSystemMetrics(76 /* SM_XVIRTUALSCREEN */), right = left + Win32.GetSystemMetrics(78 /* SM_CXVIRTUALSCREEN */);
        if (at.Y <= info.rcMonitor.Top + 1)
        {
            if (_kind == Kind.Strip) Fill(work);
            else Win32.ShowWindowAsync(Target, 3 /* SW_MAXIMIZE */);
            return;
        }
        bool toLeft = at.X <= left + 1, toRight = at.X >= right - 2;
        if (!toLeft && !toRight) return;
        int top = work.Top + (_kind == Kind.Strip ? S(StripHeight) : 0);
        int half = (work.Right - work.Left) / 2;
        var target = toLeft
            ? new Win32.RECT { Left = work.Left, Top = top, Right = work.Left + half, Bottom = work.Bottom }
            : new Win32.RECT { Left = work.Right - half, Top = top, Right = work.Right, Bottom = work.Bottom };
        if (!MacWindowButtons.TryGetFrame(Target, out var frame) || !Win32.GetWindowRect(Target, out var window)) return;
        int l = frame.Left - window.Left, t = frame.Top - window.Top, r = window.Right - frame.Right, b = window.Bottom - frame.Bottom;
        const uint flags = 0x0004 | 0x0010 | 0x0200 | 0x4000;
        SetWindowPos(Target, IntPtr.Zero, target.Left - l, target.Top - t, target.Right - target.Left + l + r, target.Bottom - target.Top + t + b, flags);
    }

    private static Win32.RECT? MonitorWork(POINT at)
    {
        IntPtr monitor = MonitorFromPoint(at, 2 /* NEAREST */);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        return monitor != IntPtr.Zero && Win32.GetMonitorInfo(monitor, ref info) ? info.rcWork : null;
    }

    private void Click(int button)
    {
        switch (button)
        {
            case 0: Win32.PostMessage(Target, Win32.WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero); break;
            case 1: _hover = false; _minimize(Target); break;
            case 2: Zoom(); break;
        }
    }

    private void Zoom()
    {
        if (_kind == Kind.Strip) { ZoomStrip(); return; }
        Win32.ShowWindowAsync(Target, Win32.IsZoomed(Target) ? 9 /* SW_RESTORE */ : 3 /* SW_MAXIMIZE */);
    }
}
