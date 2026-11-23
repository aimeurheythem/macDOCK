using System.Runtime.InteropServices;

namespace MacDock.Native;

/// <summary>
/// Higher-level helpers on top of <see cref="Win32"/> for window styling.
/// </summary>
internal static class WindowEffects
{
    /// <summary>Adds extended window styles (tool window, no-activate, click-through...).</summary>
    public static void AddExStyle(IntPtr hwnd, long flags)
    {
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(style | flags));
    }

    /// <summary>Enables the Windows 10 DWM blur behind the window via SetWindowCompositionAttribute.</summary>
    /// <param name="acrylic">
    /// Acrylic = stronger, smoother blur (Windows 10 1803+). Only for windows that don't move every
    /// frame: Windows 10 lags when an acrylic window is moved/resized continuously.
    /// </param>
    public static bool EnableBlur(IntPtr hwnd, bool acrylic = false)
    {
        var accent = acrylic
            ? new Win32.AccentPolicy
            {
                AccentState = Win32.AccentState.EnableAcrylicBlurBehind,
                GradientColor = 0x10F2F2F2, // ABGR: barely tinted; the real tint is drawn by WPF on top
            }
            : new Win32.AccentPolicy { AccentState = Win32.AccentState.EnableBlurBehind };
        int size = Marshal.SizeOf<Win32.AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Win32.WindowCompositionAttributeData
            {
                Attribute = Win32.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            return Win32.SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Clips the window (and therefore the blur) to a rounded rectangle, in device pixels.</summary>
    public static void SetRoundedRegion(IntPtr hwnd, int width, int height, int radius)
    {
        // CreateRoundRectRgn excludes the right/bottom edge, hence +1. Ellipse size = diameter.
        IntPtr rgn = Win32.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        // The system owns the region after a successful SetWindowRgn; only free it on failure.
        if (Win32.SetWindowRgn(hwnd, rgn, true) == 0)
            Win32.DeleteObject(rgn);
    }
}
