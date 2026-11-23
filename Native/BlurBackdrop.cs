namespace MacDock.Native;

/// <summary>
/// Lightweight raw Win32 popup that sits behind the dock background and provides the DWM blur,
/// clipped to a rounded rectangle. It paints black (GDI alpha 0), so only the blur shows.
/// Unlike a WPF layered window, moving/resizing it every animation frame is cheap.
/// </summary>
internal sealed class BlurBackdrop : IDisposable
{
    private const string ClassName = "MacDock.BlurBackdrop";
    private static bool _classRegistered;

    private int _regionWidth = -1, _regionHeight = -1, _regionRadius = -1;
    private int _cornerPreference = -1;

    /// <summary>
    /// Windows 11 draws the blur over the whole window rectangle and ignores the rounded region, which
    /// showed as square boxes behind the dock's round corners. There the system's own rounded corners
    /// (8 px) clip the blur instead, and the window is inset just enough to sit inside the larger curve.
    /// </summary>
    private static readonly bool IsWindows11 = Environment.OSVersion.Version.Build >= 22000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public IntPtr Handle { get; private set; }

    public BlurBackdrop(bool acrylic = false, bool topmost = true)
    {
        RegisterClassOnce();
        Handle = Win32.CreateWindowEx(
            (uint)(Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | (topmost ? Win32.WS_EX_TOPMOST : 0)),
            ClassName, "MacDock Backdrop", Win32.WS_POPUP,
            -32000, -32000, 1, 1,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);

        if (Handle == IntPtr.Zero) return;
        if (IsWindows11)
        {
            int none = unchecked((int)0xFFFFFFFE); // Windows 11 draws a 1 px border around rounded windows: not around the glass
            DwmSetWindowAttribute(Handle, 34 /* DWMWA_BORDER_COLOR */, ref none, sizeof(int));
        }
        WindowEffects.EnableBlur(Handle, acrylic);
        Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
    }

    private static void RegisterClassOnce()
    {
        if (_classRegistered) return;
        // Use the native DefWindowProc directly: no managed callback to keep alive.
        IntPtr defProc = Win32.GetProcAddress(Win32.GetModuleHandle("user32.dll"), "DefWindowProcW");
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = defProc,
            hInstance = Win32.GetModuleHandle(null),
            hbrBackground = Win32.GetStockObject(Win32.BLACK_BRUSH),
            lpszClassName = ClassName,
        };
        Win32.RegisterClassEx(ref wc);
        _classRegistered = true;
    }

    /// <summary>Moves/resizes in device pixels and re-clips to a rounded rectangle when the size changes.</summary>
    public void Place(int x, int y, int width, int height, int radius)
    {
        if (Handle == IntPtr.Zero || width <= 0 || height <= 0) return;

        if (IsWindows11)
        {
            SetCornerPreference(radius > 0 ? DWMWCP_ROUND : DWMWCP_DONOTROUND);
            if (radius > 0)
            {
                // Smallest inset that keeps the system's 8 px corner inside our radius:
                // (R - r) * (1 - 1/sqrt 2), plus a pixel for anti-aliasing.
                uint dpi = GetDpiForWindow(Handle);
                double systemRadius = 8.0 * (dpi == 0 ? 96 : dpi) / 96;
                // Same radius as the system's: no inset at all, the blur matches the shape exactly.
                int inset = radius <= systemRadius + 0.5 ? 0 : (int)Math.Ceiling((radius - systemRadius) * 0.2929) + 1;
                if (inset > 0 && width > 4 * inset && height > 4 * inset)
                {
                    x += inset;
                    y += inset;
                    width -= 2 * inset;
                    height -= 2 * inset;
                    radius = Math.Max(0, radius - inset);
                }
            }
        }

        Win32.SetWindowPos(Handle, IntPtr.Zero, x, y, width, height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);

        if (width == _regionWidth && height == _regionHeight && radius == _regionRadius) return;
        _regionWidth = width;
        _regionHeight = height;
        _regionRadius = radius;
        WindowEffects.SetRoundedRegion(Handle, width, height, radius);
    }

    private void SetCornerPreference(int preference)
    {
        if (preference == _cornerPreference) return;
        _cornerPreference = preference;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>When true the blur window always stays hidden (the owner draws its own frosted glass).</summary>
    public bool Suppressed { get; set; }

    public void SetVisible(bool visible)
    {
        if (Handle != IntPtr.Zero)
            Win32.ShowWindow(Handle, visible && !Suppressed ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Win32.DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }
}
