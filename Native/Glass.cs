using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacDock.Native;

/// <summary>
/// macOS-style "glass" for popups (menus, Control Center): the desktop behind is blurred by the
/// window manager, the content is drawn on top with a light tint and a bright rim.
///
/// The blur is applied to the popup's own window and clipped to the content's rounded shape.
/// Windows 10 honors the rounded clip; Windows 11 ignores it and blurs the full rectangle, so there
/// the system's own rounded corners are used and the content uses the same radius (8 px), so the
/// blur and the drawing always match exactly.
/// </summary>
internal static class Glass
{
    public static readonly bool IsWindows11 = Environment.OSVersion.Version.Build >= 22000;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Corner radius to draw glass surfaces with (Windows 11 can only round by 8 px).</summary>
    public static double Radius(double preferred) => IsWindows11 ? 8 : preferred;

    /// <summary>Blurs behind <paramref name="surface"/> once its popup window exists (call before opening).</summary>
    public static void Attach(Popup popup, FrameworkElement surface, double radius) =>
        popup.Opened += (_, _) => Apply(surface, radius);

    public static void Attach(ContextMenu menu, double radius) =>
        // A menu's window is finished a moment after Opened: apply once it has rendered.
        menu.Opened += (_, _) => menu.Dispatcher.BeginInvoke(() => Apply(menu, radius), System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>Applies blur + rounded clip to the window that hosts <paramref name="surface"/>.</summary>
    public static void Apply(FrameworkElement surface, double radius)
    {
        if (PresentationSource.FromVisual(surface) is not HwndSource source) return;
        IntPtr hwnd = source.Handle;
        WindowEffects.EnableBlur(hwnd, acrylic: false);

        if (IsWindows11)
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            return;
        }

        // Windows 10: clip the window (and so the blur) to the surface's rounded rectangle.
        void Clip()
        {
            if (PresentationSource.FromVisual(surface) == null || surface.ActualWidth <= 0) return;
            var scale = VisualTreeHelper.GetDpi(surface);
            var origin = surface.TransformToAncestor(source.RootVisual).Transform(new Point(0, 0));
            int x = (int)Math.Round(origin.X * scale.DpiScaleX), y = (int)Math.Round(origin.Y * scale.DpiScaleY);
            int w = (int)Math.Round(surface.ActualWidth * scale.DpiScaleX), h = (int)Math.Round(surface.ActualHeight * scale.DpiScaleY);
            int r = (int)Math.Round(radius * scale.DpiScaleX);
            IntPtr region = Win32.CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, r * 2, r * 2);
            if (Win32.SetWindowRgn(hwnd, region, true) == 0) Win32.DeleteObject(region);
        }
        Clip();
        surface.SizeChanged += (_, _) => Clip();
    }
}
