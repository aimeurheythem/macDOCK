using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using MacDock.Native;
using Microsoft.Win32;

namespace MacDock.Services;

internal static partial class TrayIcons
{
    /// <summary>
    /// Windows 11 no longer uses toolbar controls for the tray, but it records every notification icon in
    /// HKCU\Control Panel\NotifyIconSettings (program path, tooltip and a snapshot of the icon image).
    /// Icons of programs that are running right now are the ones the taskbar would show.
    /// </summary>
    private static class Windows11Tray
    {
        private const string KeyPath = @"Control Panel\NotifyIconSettings";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        public static bool IsAvailable
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key != null;
            }
        }

        public static List<TrayIcon> GetIcons()
        {
            var running = RunningPrograms();
            var result = new List<TrayIcon>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
                if (root == null) return result;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name);
                    if (entry?.GetValue("ExecutablePath") is not string rawPath) continue;
                    var path = ResolveKnownFolder(rawPath);
                    if (path == null || !running.TryGetValue(path, out uint pid)) continue;
                    if (Path.GetFileName(path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(path)) continue; // one icon per program

                    var tip = entry.GetValue("InitialTooltip") as string;
                    if (string.IsNullOrWhiteSpace(tip)) tip = ProgramName(path);
                    var image = Decode(entry.GetValue("IconSnapshot") as byte[]) ?? AppIcon(pid);
                    result.Add(new TrayIcon(IntPtr.Zero, 0, default, tip, image, pid, path));
                }
            }
            catch
            {
                // Show what was read.
            }
            return result;
        }

        /// <summary>"{GUID}\Folder\app.exe" (a known folder such as Program Files) to a normal path.</summary>
        private static string? ResolveKnownFolder(string path)
        {
            if (!path.StartsWith('{')) return path;
            int end = path.IndexOf('}');
            if (end < 0 || !Guid.TryParse(path[..(end + 1)], out var id)) return null;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var folder) != 0) return null;
            try
            {
                return Marshal.PtrToStringUni(folder) + path[(end + 1)..];
            }
            finally
            {
                Marshal.FreeCoTaskMem(folder);
            }
        }

        /// <summary>Full path of every running program (one process id each).</summary>
        private static Dictionary<string, uint> RunningPrograms()
        {
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    IntPtr handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)process.Id);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        var sb = new StringBuilder(1024);
                        uint size = (uint)sb.Capacity;
                        if (Win32.QueryFullProcessImageName(handle, 0, sb, ref size)) map.TryAdd(sb.ToString(), (uint)process.Id);
                    }
                    finally
                    {
                        Win32.CloseHandle(handle);
                    }
                }
            }
            return map;
        }

        private static BitmapSource? Decode(byte[]? data)
        {
            if (data == null || data.Length < 8) return null;
            try
            {
                using var stream = new MemoryStream(data);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                return DarkenIfWhite(frame);
            }
            catch
            {
                return null;
            }
        }

        private static string ProgramName(string path)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch
            {
                // Fall back to the file name.
            }
            return Path.GetFileNameWithoutExtension(path);
        }
    }

    // ---------------- Actions MacDock performs itself (Windows 11) ----------------

    /// <summary>Left click: bring the app's window forward, or start it (tray apps then show their window).</summary>
    public static void Open(TrayIcon icon)
    {
        var window = WindowEnumerator.GetTaskbarWindows()
            .FirstOrDefault(w => icon.ExePath != null && PathHelper.Comparer.Equals(w.ExePath, PathHelper.Normalize(icon.ExePath)));
        if (window.Hwnd != IntPtr.Zero) WindowActivator.Activate(window.Hwnd);
        else if (icon.ExePath != null) Shell.Open(icon.ExePath);
    }

    /// <summary>Asks the app to close (every window of it, including hidden tray windows), like its own Exit.</summary>
    public static void Quit(TrayIcon icon)
    {
        uint pid = icon.ProcessId;
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid) Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Ends the app at once (for apps that only hide when asked to close).</summary>
    public static void ForceQuit(TrayIcon icon)
    {
        try
        {
            using var process = Process.GetProcessById((int)icon.ProcessId);
            process.Kill();
        }
        catch
        {
            // Already gone or not allowed.
        }
    }
}
