using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>One notification-area ("tray") icon of a background app.</summary>
internal sealed record TrayIcon(IntPtr Toolbar, int Index, Win32.RECT ButtonRect, string Tip, BitmapSource? Image, uint ProcessId, string? ExePath = null);

/// <summary>
/// Reads the notification-area icons (the ones beside the clock and behind the taskbar's "^" chevron)
/// so the menu bar can show them, and forwards clicks to them.
///
/// Windows 10 keeps these icons in two toolbar controls inside Explorer (the visible tray and the
/// overflow panel). Their data lives in Explorer's memory, so it is read with a small buffer allocated
/// in Explorer. Clicks are posted to the toolbar button itself: Explorer then notifies the app exactly
/// as if the icon had been clicked on the taskbar (correct message format for every app).
/// Windows 11 draws its tray differently; there <see cref="IsSupported"/> is false.
/// </summary>
internal static partial class TrayIcons
{
    private const uint TB_GETBUTTON = 0x0417, TB_BUTTONCOUNT = 0x0418, TB_GETITEMRECT = 0x041D, TB_GETBUTTONTEXTW = 0x044B;
    private const byte TBSTATE_HIDDEN = 0x08;
    private const uint PROCESS_VM = 0x0008 | 0x0010 | 0x0020 | 0x0400; // operation, read, write, query
    private const uint MEM_COMMIT = 0x1000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;
    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                       WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int TbButtonSize = 32; // TBBUTTON on 64-bit

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);

    private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Windows 10: Explorer's tray toolbars. Windows 11: its icon list in the registry.</summary>
    public static bool IsSupported => IsWindows11 ? Windows11Tray.IsAvailable : FindToolbars().Count > 0;

    /// <summary>True when clicks go to the real tray icon (Windows 10); false when MacDock handles them (Windows 11).</summary>
    public static bool ForwardsClicks(TrayIcon icon) => icon.Toolbar != IntPtr.Zero;

    /// <summary>All visible tray icons (visible area first, then the overflow), skipping hidden placeholders.</summary>
    public static List<TrayIcon> GetIcons()
    {
        if (IsWindows11) return Windows11Tray.GetIcons();
        var result = new List<TrayIcon>();
        foreach (var toolbar in FindToolbars())
        {
            try { ReadToolbar(toolbar, result); }
            catch { /* Explorer busy or restarting: show what we have */ }
        }
        return result;
    }

    /// <summary>Clicks the icon (left or right button), as if on the taskbar.</summary>
    public static void Click(TrayIcon icon, bool right)
    {
        AllowSetForegroundWindow(icon.ProcessId); // its menu / window may come to the front
        var r = icon.ButtonRect;
        int x = (r.Left + r.Right) / 2, y = (r.Top + r.Bottom) / 2;
        IntPtr point = new((y << 16) | (x & 0xFFFF));
        Win32.PostMessage(icon.Toolbar, WM_MOUSEMOVE, IntPtr.Zero, point);
        Win32.PostMessage(icon.Toolbar, right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN, new IntPtr(right ? 2 : 1), point);
        Win32.PostMessage(icon.Toolbar, right ? WM_RBUTTONUP : WM_LBUTTONUP, IntPtr.Zero, point);
    }

    private static List<IntPtr> FindToolbars()
    {
        var list = new List<IntPtr>();
        IntPtr tray = Win32.FindWindow("Shell_TrayWnd", null);
        IntPtr notify = Win32.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr pager = Win32.FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
        IntPtr visible = Win32.FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
        if (visible != IntPtr.Zero) list.Add(visible);
        IntPtr overflow = Win32.FindWindow("NotifyIconOverflowWindow", null);
        IntPtr overflowBar = Win32.FindWindowEx(overflow, IntPtr.Zero, "ToolbarWindow32", null);
        if (overflowBar != IntPtr.Zero) list.Add(overflowBar);
        return list;
    }

    private static void ReadToolbar(IntPtr toolbar, List<TrayIcon> result)
    {
        int count = (int)SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return;

        Win32.GetWindowThreadProcessId(toolbar, out uint explorerPid);
        IntPtr process = Win32.OpenProcess(PROCESS_VM, false, explorerPid);
        if (process == IntPtr.Zero) return;
        const int bufferSize = 1024;
        IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)bufferSize, MEM_COMMIT, PAGE_READWRITE);
        try
        {
            if (remote == IntPtr.Zero) return;
            var local = new byte[bufferSize];
            for (int i = 0; i < count; i++)
            {
                // TBBUTTON: iBitmap(4) idCommand(4) fsState(1) fsStyle(1) reserved(6) dwData(8) iString(8)
                if (SendMessage(toolbar, TB_GETBUTTON, new IntPtr(i), remote) == IntPtr.Zero) continue;
                if (!Read(process, remote, local, TbButtonSize)) continue;
                if ((local[8] & TBSTATE_HIDDEN) != 0) continue;
                int command = BitConverter.ToInt32(local, 4);
                IntPtr data = new(BitConverter.ToInt64(local, 16));
                if (data == IntPtr.Zero) continue;

                // TRAYDATA: hwnd(8) uID(4) uCallbackMessage(4) reserved(8) hIcon(8)
                if (!Read(process, data, local, 32)) continue;
                IntPtr owner = new(BitConverter.ToInt64(local, 0));
                IntPtr hIcon = new(BitConverter.ToInt64(local, 24));
                if (owner == IntPtr.Zero || !Win32.IsWindow(owner)) continue; // icon of an app that is gone
                Win32.GetWindowThreadProcessId(owner, out uint appPid);
                if (appPid == explorerPid) continue; // Windows' own network/sound/battery: the menu bar shows those

                // Tooltip text (the app name, often with status).
                string tip = "";
                int length = (int)SendMessage(toolbar, TB_GETBUTTONTEXTW, new IntPtr(command), remote);
                if (length > 0 && length < bufferSize / 2 - 1 && Read(process, remote, local, (length + 1) * 2))
                    tip = Encoding.Unicode.GetString(local, 0, length * 2).Trim();

                // Button rectangle (toolbar client coordinates), used to forward clicks.
                Win32.RECT rect = default;
                if (SendMessage(toolbar, TB_GETITEMRECT, new IntPtr(i), remote) != IntPtr.Zero && Read(process, remote, local, 16))
                    rect = new Win32.RECT(BitConverter.ToInt32(local, 0), BitConverter.ToInt32(local, 4),
                                          BitConverter.ToInt32(local, 8), BitConverter.ToInt32(local, 12));

                result.Add(new TrayIcon(toolbar, i, rect, tip, ToImage(hIcon) ?? AppIcon(appPid), appPid));
            }
        }
        finally
        {
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
            Win32.CloseHandle(process);
        }
    }

    /// <summary>
    /// Many tray icons are white glyphs made for the dark taskbar; on the light menu bar panel they would
    /// be invisible. Mostly-white icons are recolored to the bar's text color (alpha kept), like macOS.
    /// </summary>
    private static BitmapSource DarkenIfWhite(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);

        int opaque = 0, light = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] < 64) continue;
            opaque++;
            if (Math.Min(px[i], Math.Min(px[i + 1], px[i + 2])) > 190) light++;
        }
        BitmapSource result = bgra;
        if (opaque > 0 && light >= opaque * 0.7)
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = 0x1F; px[i + 1] = 0x1D; px[i + 2] = 0x1D; // BGR of #1D1D1F
            }
            result = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, stride);
        }
        result.Freeze();
        return result;
    }

    /// <summary>Last resort when the tray icon can't be read: the app's own program icon.</summary>
    private static BitmapSource? AppIcon(uint processId)
    {
        IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!Win32.QueryFullProcessImageName(process, 0, sb, ref size)) return null;
            return IconService.GetIconForParsingName(sb.ToString(), 32) as BitmapSource;
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    private static bool Read(IntPtr process, IntPtr address, byte[] buffer, int size) =>
        ReadProcessMemory(process, address, buffer, (UIntPtr)size, out var read) && (int)read == size;

    /// <summary>Icons are shared between processes; a private copy is converted so Explorer may free its own.</summary>
    private static BitmapSource? ToImage(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        IntPtr copy = CopyIcon(hIcon);
        if (copy == IntPtr.Zero) return null;
        try
        {
            BitmapSource image;
            try
            {
                image = Imaging.CreateBitmapSourceFromHIcon(copy, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch
            {
                // Some apps (Electron: Claude, VS Code...) make icons WPF can't read directly; GDI+ can.
                using var icon = System.Drawing.Icon.FromHandle(copy);
                using var bitmap = icon.ToBitmap();
                var hBitmap = bitmap.GetHbitmap(System.Drawing.Color.FromArgb(0));
                try { image = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
                finally { Win32.DeleteObject(hBitmap); }
            }
            return DarkenIfWhite(image);
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(copy);
        }
    }
}
