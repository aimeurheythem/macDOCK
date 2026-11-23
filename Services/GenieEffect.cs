using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacDock.Controls;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// macOS "Genie" minimize / restore, for minimizes from the dock AND from a window's own minimize
/// button (title bar) or keyboard.
///
/// A minimized window can't be captured any more, so snapshots are taken ahead of time: when a
/// window comes to the front, when the mouse nears its title bar, and when the mouse reaches the dock.
/// Snapshots copy what is already on screen (BitBlt of the desktop) - invisible to the app. PrintWindow
/// is only a fallback, because it makes Chromium/Electron windows flicker while they repaint for it.
///
/// While MacDock runs, Windows' own minimize/restore animation is turned off (and the change is
/// broadcast so the window manager really stops animating), so only the Genie ever plays.
/// </summary>
internal sealed class GenieEffect : IDisposable
{
    private const double MinimizeMs = 450, RestoreMs = 450; // reference default
    private const int MaxCached = 6;
    private const int HTMINBUTTON = 8;
    private const uint WM_NCHITTEST = 0x0084;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const uint SPIF_SENDCHANGE = 0x2;
    private const int SRCCOPY = 0x00CC0020;
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan HoverRecapture = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(800);

    private sealed record Snapshot(BitmapSource Image, Rect Bounds, DateTime TakenAt);

    private readonly Func<double> _dpiScale;
    private readonly Func<IntPtr, Rect?> _iconFor;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<IntPtr, Snapshot> _cache = new();
    private readonly LinkedList<IntPtr> _order = new();
    private readonly HashSet<IntPtr> _busy = new();
    private readonly HashSet<IntPtr> _capturing = new();
    private readonly Dictionary<IntPtr, DateTime> _lastPlayed = new();
    private readonly Win32.WinEventProc _hookProc; // keep alive while hooked
    private readonly List<IntPtr> _hooks = new();
    private readonly DispatcherTimer _hoverTimer;
    // The overlay covers every screen and holds GPU surfaces of several hundred MB while it exists, so
    // it is created for an animation and closed a few seconds after the last one.
    private GenieOverlay? _overlayWindow;
    private readonly DispatcherTimer _overlayIdle = new() { Interval = TimeSpan.FromSeconds(2) };

    private GenieOverlay _overlay
    {
        get
        {
            _overlayIdle.Stop();
            if (_overlayWindow == null)
            {
                _overlayWindow = new GenieOverlay();
                _overlayWindow.Park();
            }
            return _overlayWindow;
        }
    }

    private void ReleaseOverlaySoon()
    {
        if (_busy.Count > 0) return;
        _overlayIdle.Stop();
        _overlayIdle.Start();
    }

    private void ReleaseOverlay()
    {
        _overlayIdle.Stop();
        if (_busy.Count > 0 || _overlayWindow == null) return;
        // WPF keeps a window's large drawing surfaces for as long as the window exists (shrinking it
        // doesn't give them back), so the used window is closed and a fresh, tiny one is made ready
        // right away: the memory goes back to Windows and the next Genie still starts instantly.
        _overlayWindow.Close();
        _overlayWindow = null;
        MemoryTrim.Soon(1);
        _dispatcher.BeginInvoke(() =>
        {
            if (_overlayWindow != null) return;
            _ = _overlay;
            _overlayIdle.Stop();
        }, DispatcherPriority.ApplicationIdle);
        return;
        // Its GPU surfaces are only freed by finalizers, and MacDock allocates so little managed memory
        // that a collection might not come for hours: ask for one once the window is gone.
        var collect = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        collect.Tick += (_, _) =>
        {
            collect.Stop();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        };
        collect.Start();
    }
    private readonly MinimizeButtonInterceptor _minimizeButton = new();

    private static int? _originalMinAnimate;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Win32.POINT point);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <param name="dpiScale">Device pixels per DIP (UI thread only).</param>
    /// <param name="iconFor">Screen rect (DIPs) of the dock icon for a window, or null if unknown.</param>
    public GenieEffect(Func<double> dpiScale, Func<IntPtr, Rect?> iconFor)
    {
        _dpiScale = dpiScale;
        _iconFor = iconFor;
        _overlayIdle.Tick += (_, _) => ReleaseOverlay();
        GameMode.Changed += on => _minimizeButton.SetPaused(on); // no mouse hook at all while a game is in front
        _ = _overlay; // created now, parked and tiny: the first Genie starts without a delay
        _overlayIdle.Stop();
        Task.Run(DisableSystemMinimizeAnimation); // broadcasts a setting change: keep it off the UI thread

        _hookProc = OnWinEvent;
        const uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
        AddHook(Win32.EVENT_SYSTEM_MINIMIZESTART, flags);
        AddHook(Win32.EVENT_SYSTEM_FOREGROUND, flags);

        // Title-bar minimize button: handled by us, so the copy covers the window before it goes.
        _minimizeButton.Clicked += hwnd => _dispatcher.BeginInvoke(() => MinimizeFromTitleBar(hwnd));

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hoverTimer.Tick += (_, _) => { if (!GameMode.Active) WatchMinimizeButtonHover(); };
        _hoverTimer.Start();

        // The window in front when MacDock starts gets a snapshot right away (no foreground change yet).
        _dispatcher.BeginInvoke(() => CaptureInBackground(WindowActivator.GetForegroundRoot()), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Snapshots a window right now (called when the mouse reaches the dock, before any icon has
    /// magnified, so the dock can't end up inside the picture).
    /// </summary>
    public void SnapshotNow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _busy.Contains(hwnd) || !WindowEnumerator.IsTaskbarWindow(hwnd)) return;
        if (Capture(hwnd, _dpiScale()) is { } snapshot) Remember(hwnd, snapshot);
    }

    // ---------------- Minimize / restore from the dock ----------------

    /// <summary>Minimizes with the Genie effect into <paramref name="icon"/> (screen DIPs). False = not possible.</summary>
    public async Task<bool> MinimizeAsync(IntPtr hwnd, Rect icon)
    {
        if (_busy.Contains(hwnd)) return true;
        _busy.Add(hwnd);
        try
        {
            // Freshest possible picture: capture right now unless the dock's magnified icons overlap
            // the window (they would end up in the picture) - then use the one taken when the mouse
            // reached the dock.
            double scale = _dpiScale();
            bool clearOfDock = VisibleBottom(hwnd, scale) is double bottom && bottom < icon.Top - icon.Height * 0.75;
            bool haveRecent = _cache.TryGetValue(hwnd, out var s) && DateTime.UtcNow - s.TakenAt < FreshFor;
            var snapshot = clearOfDock || !haveRecent ? Capture(hwnd, scale) ?? s : s;
            if (snapshot == null) return false;
            Remember(hwnd, snapshot);
            _lastPlayed[hwnd] = DateTime.UtcNow;
            await _overlay.PrepareAsync(snapshot.Image, snapshot.Bounds, icon, 0);
            await _overlay.WaitUntilOnScreenAsync(); // the exact copy now covers the real window
            DisableTransitions(hwnd);
            // Async: a synchronous ShowWindow waits for the app to process it (~70 ms of frozen
            // copy). The warp holds still until the window is really minimized, so it never peeks out.
            Win32.ShowWindowAsync(hwnd, Win32.SW_MINIMIZE);
            await _overlay.AnimateAsync(0, 1, MinimizeMs, startWhen: () => Win32.IsIconic(hwnd));
            await _overlay.ClearAsync();
            return true;
        }
        finally
        {
            _busy.Remove(hwnd);
            ReleaseOverlaySoon();
        }
    }

    /// <summary>Restores a minimized window with the reverse Genie effect. False = no snapshot.</summary>
    public async Task<bool> RestoreAsync(IntPtr hwnd, Rect icon)
    {
        if (_busy.Contains(hwnd)) return true;
        if (!_cache.TryGetValue(hwnd, out var snapshot)) return false;

        _busy.Add(hwnd);
        try
        {
            await _overlay.PrepareAsync(snapshot.Image, snapshot.Bounds, icon, 1);
            await _overlay.AnimateAsync(1, 0, RestoreMs);
            DisableTransitions(hwnd);
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            WindowActivator.Activate(hwnd);
            // The last frame is the snapshot exactly over the window: keep it until the real window
            // has painted underneath (GPU apps can take a few frames after a restore), so the handoff is invisible.
            await _overlay.WaitFramesAsync(12);
            await _overlay.ClearAsync();
            return true;
        }
        finally
        {
            _busy.Remove(hwnd);
            ReleaseOverlaySoon();
        }
    }

    // ---------------- Minimize from the title bar / keyboard ----------------

    /// <summary>A click on a window's minimize button (swallowed by the hook): same gapless path as the dock.</summary>
    private async void MinimizeFromTitleBar(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd)) return;
        if (_iconFor(hwnd) is { } icon && await MinimizeAsync(hwnd, icon)) return;
        // No dock icon or no picture: let the app minimize itself, as if we had never caught the click.
        Win32.PostMessage(hwnd, Win32.WM_SYSCOMMAND, new IntPtr(Win32.SC_MINIMIZE), IntPtr.Zero);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        if (eventType == Win32.EVENT_SYSTEM_FOREGROUND)
        {
            // Give the window a moment to paint, then keep a snapshot ready.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) => { timer.Stop(); if (!GameMode.Active) CaptureInBackground(hwnd); }; // never grab frames from a game
            timer.Start();
        }
        else if (eventType == Win32.EVENT_SYSTEM_MINIMIZESTART)
        {
            PlayForSystemMinimize(hwnd);
        }
    }

    /// <summary>The window is already gone (Windows' animation is off): play the Genie from its last snapshot.</summary>
    private async void PlayForSystemMinimize(IntPtr hwnd)
    {
        if (_busy.Contains(hwnd)) return;
        // Some apps raise the minimize event more than once: play only once.
        if (_lastPlayed.TryGetValue(hwnd, out var last) && DateTime.UtcNow - last < DuplicateWindow) return;
        if (!_cache.TryGetValue(hwnd, out var snapshot) || _iconFor(hwnd) is not { } icon) return;

        _busy.Add(hwnd);
        _lastPlayed[hwnd] = DateTime.UtcNow;
        try
        {
            await _overlay.PrepareAsync(snapshot.Image, snapshot.Bounds, icon, 0);
            await _overlay.AnimateAsync(0, 1, MinimizeMs);
            await _overlay.ClearAsync();
        }
        finally
        {
            _busy.Remove(hwnd);
            ReleaseOverlaySoon();
        }
    }

    /// <summary>
    /// Refreshes the snapshot as soon as the mouse enters the band of a window's caption buttons
    /// (top-right corner), before the click. Pure geometry: no message is sent to the app.
    /// </summary>
    private Win32.POINT _lastHover;

    private void WatchMinimizeButtonHover()
    {
        if (!Win32.GetCursorPos(out var pt)) return;
        if (pt.X == _lastHover.X && pt.Y == _lastHover.Y) return; // the mouse hasn't moved: nothing to do
        _lastHover = pt;
        IntPtr under = WindowFromPoint(pt);
        if (under == IntPtr.Zero) return;
        IntPtr root = Win32.GetAncestor(under, Win32.GA_ROOT);
        if (root == IntPtr.Zero || !Win32.GetWindowRect(root, out var rect)) return;
        // Near the window's buttons: macOS lights on the left, or Windows' own on the right.
        if (pt.Y > rect.Top + 64 || (pt.X > rect.Left + 160 && pt.X < rect.Right - 300)) return;
        if (_cache.TryGetValue(root, out var s) && DateTime.UtcNow - s.TakenAt < HoverRecapture) return;
        if (!WindowEnumerator.IsTaskbarWindow(root)) return;
        CaptureInBackground(root);
    }

    // ---------------- Snapshots ----------------

    private void CaptureInBackground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _busy.Contains(hwnd) || _capturing.Contains(hwnd)) return;
        if (!WindowEnumerator.IsTaskbarWindow(hwnd) || Win32.IsIconic(hwnd)) return;
        // Only the window in front: it is copied from the screen, which the app never notices. Asking a
        // window behind to draw itself (PrintWindow) made browsers and editors redraw, costing CPU.
        if (WindowActivator.GetForegroundRoot() != hwnd) return;
        // A recent copy is good enough; switching back and forth between windows doesn't copy each time.
        if (_cache.TryGetValue(hwnd, out var recent) && DateTime.UtcNow - recent.TakenAt < TimeSpan.FromSeconds(8)) return;
        _capturing.Add(hwnd);
        double scale = _dpiScale();
        Task.Run(() => Capture(hwnd, scale)).ContinueWith(task =>
            _dispatcher.BeginInvoke(() =>
            {
                _capturing.Remove(hwnd);
                if (task.Result is { } snapshot && !_busy.Contains(hwnd)) Remember(hwnd, snapshot);
            }));
    }

    private void Remember(IntPtr hwnd, Snapshot snapshot)
    {
        if (_cache.Remove(hwnd)) _order.Remove(hwnd);
        _cache[hwnd] = snapshot;
        _order.AddLast(hwnd);
        bool dropped = false;
        while (_order.Count > MaxCached)
        {
            _cache.Remove(_order.First!.Value);
            _order.RemoveFirst();
            dropped = true;
        }
        // Copies of windows that are still open (not minimized) and old are dropped: they are taken again
        // when needed. Minimized windows keep theirs (they are needed to restore with the Genie).
        foreach (var old in _cache.Where(p => !Win32.IsWindow(p.Key) || (!Win32.IsIconic(p.Key) && DateTime.UtcNow - p.Value.TakenAt > TimeSpan.FromSeconds(90))).Select(p => p.Key).ToList())
        {
            _cache.Remove(old);
            _order.Remove(old);
            dropped = true;
        }
        // Their pixels live outside the managed heap: free them soon instead of whenever a collection happens.
        if (dropped) MemoryTrim.Soon(10);
    }

    /// <summary>
    /// Snapshot cropped to the window's visible frame. The window in front is copied straight from
    /// the screen (invisible to it); others fall back to PrintWindow. Thread-safe.
    /// </summary>
    private static Snapshot? Capture(IntPtr hwnd, double scale)
    {
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd) || !Win32.GetWindowRect(hwnd, out var full)) return null;
        var visible = full;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT frame, Marshal.SizeOf<Win32.RECT>()) == 0)
            visible = frame;

        int vw = visible.Right - visible.Left, vh = visible.Bottom - visible.Top;
        if (vw <= 0 || vh <= 0 || vw > 10000 || vh > 10000) return null;

        bool inFront = WindowActivator.GetForegroundRoot() == hwnd;
        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        IntPtr dc = Win32.CreateCompatibleDC(screen);
        try
        {
            if (inFront)
            {
                IntPtr bitmap = Win32.CreateCompatibleBitmap(screen, vw, vh);
                IntPtr old = Win32.SelectObject(dc, bitmap);
                try
                {
                    if (!BitBlt(dc, 0, 0, vw, vh, screen, visible.Left, visible.Top, SRCCOPY)) return null;
                    Win32.SelectObject(dc, old);
                    return ToSnapshot(bitmap, new Int32Rect(0, 0, vw, vh), visible, scale);
                }
                finally
                {
                    Win32.DeleteObject(bitmap);
                }
            }
            else
            {
                int w = full.Right - full.Left, h = full.Bottom - full.Top;
                if (w <= 0 || h <= 0 || w > 10000 || h > 10000) return null;
                IntPtr bitmap = Win32.CreateCompatibleBitmap(screen, w, h);
                IntPtr old = Win32.SelectObject(dc, bitmap);
                try
                {
                    if (!Win32.PrintWindow(hwnd, dc, Win32.PW_RENDERFULLCONTENT)) return null;
                    Win32.SelectObject(dc, old);
                    int x = Math.Clamp(visible.Left - full.Left, 0, w - 1);
                    int y = Math.Clamp(visible.Top - full.Top, 0, h - 1);
                    return ToSnapshot(bitmap, new Int32Rect(x, y, Math.Min(vw, w - x), Math.Min(vh, h - y)), visible, scale);
                }
                finally
                {
                    Win32.DeleteObject(bitmap);
                }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32.DeleteDC(dc);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Copies the pixels into a managed bitmap. Interop bitmaps live in unmanaged memory the garbage
    /// collector can't see, so snapshots piled up (hundreds of MB) before being freed.
    /// </summary>
    private static double? VisibleBottom(IntPtr hwnd, double scale) =>
        Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT r, Marshal.SizeOf<Win32.RECT>()) == 0
            ? r.Bottom / scale
            : null;

    private static Snapshot? ToSnapshot(IntPtr bitmap, Int32Rect crop, Win32.RECT visible, double scale)
    {
        if (Win32.GetObject(bitmap, Marshal.SizeOf<Win32.BITMAP>(), out var info) == 0) return null;
        int fullWidth = info.bmWidth, fullStride = fullWidth * 4;
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = fullWidth,
            biHeight = -(crop.Y + crop.Height), // top-down; rows above the crop are read too, then skipped
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };
        var full = new byte[fullStride * (crop.Y + crop.Height)];
        IntPtr dc = Win32.CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (Win32.GetDIBits(dc, bitmap, 0, (uint)(crop.Y + crop.Height), full, ref header, Win32.DIB_RGB_COLORS) == 0) return null;
        }
        finally
        {
            Win32.DeleteDC(dc);
        }

        int stride = crop.Width * 4;
        byte[] pixels;
        if (crop.X == 0 && crop.Y == 0 && crop.Width == fullWidth)
        {
            pixels = full; // the usual case (screen copy): no second copy
        }
        else
        {
            pixels = new byte[stride * crop.Height];
            for (int row = 0; row < crop.Height; row++)
                Buffer.BlockCopy(full, (crop.Y + row) * fullStride + crop.X * 4, pixels, row * stride, stride);
        }
        var image = BitmapSource.Create(crop.Width, crop.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
        image.Freeze();
        var bounds = new Rect(visible.Left / scale, visible.Top / scale, crop.Width / scale, crop.Height / scale);
        return new Snapshot(image, bounds, DateTime.UtcNow);
    }

    private void AddHook(uint eventId, uint flags)
    {
        var hook = Win32.SetWinEventHook(eventId, eventId, IntPtr.Zero, _hookProc, 0, 0, flags);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    // ---------------- Windows' own minimize animation ----------------

    /// <summary>Per-window "no DWM animation" (belt and braces; Windows may refuse it for other apps' windows).</summary>
    private static void DisableTransitions(IntPtr hwnd)
    {
        int disabled = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
    }

    private static void DisableSystemMinimizeAnimation()
    {
        var info = new Win32.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<Win32.ANIMATIONINFO>() };
        if (!Win32.SystemParametersInfo(Win32.SPI_GETANIMATION, info.cbSize, ref info, 0)) return;
        _originalMinAnimate ??= info.iMinAnimate;
        if (info.iMinAnimate == 0) return;
        info.iMinAnimate = 0;
        // SENDCHANGE broadcasts it, so the window manager (DWM) really stops animating. Not saved to disk.
        Win32.SystemParametersInfo(Win32.SPI_SETANIMATION, info.cbSize, ref info, SPIF_SENDCHANGE);
    }

    /// <summary>Puts Windows' minimize animation back as it was. Safe to call many times (exit, crash handlers).</summary>
    public static void RestoreSystemMinimizeAnimation()
    {
        if (_originalMinAnimate is not int original) return;
        _originalMinAnimate = null;
        var info = new Win32.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<Win32.ANIMATIONINFO>(), iMinAnimate = original };
        Win32.SystemParametersInfo(Win32.SPI_SETANIMATION, info.cbSize, ref info, SPIF_SENDCHANGE);
    }

    /// <summary>
    /// True when the Windhawk mod "MacOS Minimize Animation" is enabled: it injects into every app
    /// (MacDock included) and plays its own genie, so every minimize/restore would animate twice.
    /// </summary>
    public static bool IsWindhawkGenieActive()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Windhawk\Engine\Mods\macos-minimize-animation");
            if (key == null) return false;
            bool enabled = key.GetValue("Disabled") is not int disabled || disabled == 0;
            return enabled && System.Diagnostics.Process.GetProcessesByName("windhawk").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _hoverTimer.Stop();
        _minimizeButton.Dispose();
        foreach (var hook in _hooks) Win32.UnhookWinEvent(hook);
        _hooks.Clear();
        RestoreSystemMinimizeAnimation();
        _overlayIdle.Stop();
        _overlayWindow?.Close();
        _cache.Clear();
        _order.Clear();
    }
}
