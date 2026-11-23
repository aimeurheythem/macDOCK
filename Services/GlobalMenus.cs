using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// The macOS menu bar: an app's menus (File, Edit, View...) live in the bar at the top of the screen, not
/// in its window. For apps with classic Windows menus (Notepad and most traditional programs) the menu is
/// taken off the window (it keeps working: MacDock shows the same items in the top bar and sends the
/// app the very same commands) and given back when MacDock stops. Kept on disk so a crash never leaves
/// a window without its menu.
/// </summary>
internal static class GlobalMenus
{
    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetMenu(IntPtr hwnd, IntPtr menu);
    [DllImport("user32.dll")] private static extern bool IsMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int pos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    private const uint MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_STRING = 0x40, MIIM_FTYPE = 0x100;
    private const uint MFT_SEPARATOR = 0x800, MFT_OWNERDRAW = 0x100, MFS_DISABLED = 0x3, MFS_CHECKED = 0x8;
    private const uint WM_INITMENUPOPUP = 0x0117, WM_COMMAND = 0x0111;

    public sealed record Entry(string Text, string? Shortcut, uint Id, IntPtr SubMenu, bool Enabled, bool Checked, bool IsSeparator);

    private static readonly ConcurrentDictionary<IntPtr, IntPtr> Detached = new();
    private static readonly object FileGate = new();
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "menus-in-top-bar.txt");

    /// <summary>The window's menu, whether it is still on the window or already in the top bar.</summary>
    public static bool TryGet(IntPtr hwnd, out IntPtr menu)
    {
        if (Detached.TryGetValue(hwnd, out menu) && IsMenu(menu)) return true;
        menu = GetMenu(hwnd);
        return menu != IntPtr.Zero && IsMenu(menu) && IsReadable(menu);
    }

    /// <summary>Takes the menu off the window (it moves to the top bar). False when there is none or it
    /// can't be shown elsewhere (drawn by the app itself).</summary>
    public static bool Detach(IntPtr hwnd)
    {
        IntPtr menu = GetMenu(hwnd);
        if (menu == IntPtr.Zero || !IsMenu(menu) || !IsReadable(menu)) return false;
        if (!SetMenu(hwnd, IntPtr.Zero)) return false;
        Detached[hwnd] = menu;
        Save();
        return true;
    }

    public static void Restore(IntPtr hwnd)
    {
        if (!Detached.TryRemove(hwnd, out var menu)) return;
        Give(hwnd, menu);
        Save();
    }

    public static void RestoreAll()
    {
        foreach (var hwnd in Detached.Keys.ToList())
            if (Detached.TryRemove(hwnd, out var menu)) Give(hwnd, menu);
        Save();
    }

    public static void RestoreLeftovers()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            foreach (var line in File.ReadAllLines(StateFile))
            {
                var parts = line.Split(' ');
                if (parts.Length == 2 && long.TryParse(parts[0], out long hwnd) && long.TryParse(parts[1], out long menu))
                    Give(new IntPtr(hwnd), new IntPtr(menu));
            }
            File.Delete(StateFile);
        }
        catch (Exception ex)
        {
            CrashLog.Write("GlobalMenus.RestoreLeftovers", ex);
        }
    }

    private static void Give(IntPtr hwnd, IntPtr menu)
    {
        if (Win32.IsWindow(hwnd) && IsMenu(menu) && GetMenu(hwnd) == IntPtr.Zero) SetMenu(hwnd, menu);
    }

    private static void Save()
    {
        lock (FileGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                if (Detached.IsEmpty) { if (File.Exists(StateFile)) File.Delete(StateFile); return; }
                File.WriteAllLines(StateFile, Detached.Select(p => $"{p.Key.ToInt64()} {p.Value.ToInt64()}"));
            }
            catch { /* best effort */ }
        }
    }

    // ---------------- Reading ----------------

    /// <summary>Top-level menus with text (no owner-drawn ones, which only the app itself can show).</summary>
    private static bool IsReadable(IntPtr menu)
    {
        var top = Read(menu);
        return top.Count > 0 && top.All(e => e.IsSeparator || e.Text.Length > 0);
    }

    /// <summary>Lets the app update its items (enabled, checked) the way it does before showing a menu.</summary>
    public static void Prepare(IntPtr hwnd, IntPtr subMenu, int index) =>
        Win32.SendMessageTimeout(hwnd, WM_INITMENUPOPUP, subMenu, new IntPtr(index), Win32.SMTO_ABORTIFHUNG, 150, out _);

    public static List<Entry> Read(IntPtr menu)
    {
        var list = new List<Entry>();
        int count = GetMenuItemCount(menu);
        for (int i = 0; i < count && i < 200; i++)
        {
            var info = new MENUITEMINFO { cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_STRING };
            if (!GetMenuItemInfo(menu, (uint)i, true, ref info)) continue;
            if ((info.fType & MFT_SEPARATOR) != 0) { list.Add(new Entry("", null, 0, IntPtr.Zero, false, false, true)); continue; }
            if ((info.fType & MFT_OWNERDRAW) != 0) { list.Add(new Entry("", null, info.wID, info.hSubMenu, false, false, false)); continue; }
            string raw = "";
            if (info.cch > 0)
            {
                var buffer = Marshal.AllocHGlobal(((int)info.cch + 1) * 2);
                try
                {
                    info.dwTypeData = buffer;
                    info.cch++;
                    info.fMask = MIIM_STRING;
                    if (GetMenuItemInfo(menu, (uint)i, true, ref info)) raw = Marshal.PtrToStringUni(buffer) ?? "";
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                // Read state again (the second call only asked for the text).
                var state = new MENUITEMINFO { cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_STATE | MIIM_ID | MIIM_SUBMENU };
                GetMenuItemInfo(menu, (uint)i, true, ref state);
                info.fState = state.fState;
                info.wID = state.wID;
                info.hSubMenu = state.hSubMenu;
            }
            int tab = raw.IndexOf('\t');
            string text = Clean(tab >= 0 ? raw[..tab] : raw);
            string? shortcut = tab >= 0 ? raw[(tab + 1)..].Trim() : null;
            list.Add(new Entry(text, string.IsNullOrEmpty(shortcut) ? null : shortcut, info.wID, info.hSubMenu,
                (info.fState & MFS_DISABLED) == 0, (info.fState & MFS_CHECKED) != 0, false));
        }
        return list;
    }

    /// <summary>"&amp;File" becomes "File" ("&amp;&amp;" stays a single "&amp;").</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '&')
            {
                if (i + 1 < text.Length && text[i + 1] == '&') { sb.Append('&'); i++; }
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString().Trim();
    }

    public static IntPtr SubMenuAt(IntPtr menu, int index) => GetSubMenu(menu, index);

    /// <summary>Runs a menu command in the app, exactly as if picked from its own menu.</summary>
    public static void Invoke(IntPtr hwnd, uint id) => Win32.PostMessage(hwnd, WM_COMMAND, new IntPtr(id & 0xFFFF), IntPtr.Zero);
}
