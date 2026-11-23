using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MacDock.Services;

namespace MacDock.Finder;

/// <summary>
/// Blue macOS folders on the desktop.
///
/// Windows 10 draws a folder that has files in it as a yellow folder with its contents peeking out, and
/// that preview ignores the machine-wide folder icon. A folder with its own icon (a desktop.ini with an
/// IconResource line) never gets the preview, so every folder on the desktop gets one pointing at the
/// blue folder. New folders are picked up as they appear. Everything changed is remembered in
/// %APPDATA%\MacDock\desktop-folders.json so turning the option off puts each folder back exactly.
/// Folders that already have their own icon are left alone.
/// </summary>
internal static class DesktopFolderIcons
{
    private const string Marker = "MacFolder.ico";

    private static readonly string IconFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDock", "MacFolder.ico");

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "desktop-folders.json");

    private static FileSystemWatcher? _watcher;
    private static readonly object Gate = new();

    private sealed class Change
    {
        public string Folder { get; set; } = "";
        public bool CreatedIni { get; set; }
        public bool AddedReadOnly { get; set; }
        public bool AddedGitExclude { get; set; }
    }

    /// <summary>Turns the blue desktop folders on (all current folders + new ones) or off (everything restored).</summary>
    public static void Set(bool on)
    {
        if (on)
        {
            try { WindowsFolderIcons.WriteIconTo(IconFile); } // renders with WPF: on this (UI) thread
            catch (Exception ex) { CrashLog.Write("DesktopFolderIcons.Icon", ex); return; }
            Task.Run(() =>
            {
                try
                {
                    foreach (var desktop in Desktops())
                        foreach (var dir in SafeDirectories(desktop)) Apply(dir);
                    Notify(null);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("DesktopFolderIcons.Set", ex);
                }
            });
            StartWatching();
        }
        else
        {
            StopWatching();
            Task.Run(RestoreAll);
        }
    }

    private static IEnumerable<string> Desktops()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (Directory.Exists(user)) yield return user;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    private static void StartWatching()
    {
        if (_watcher != null) return;
        var desktop = Desktops().FirstOrDefault();
        if (desktop == null) return;
        try
        {
            _watcher = new FileSystemWatcher(desktop)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => Later(e.FullPath);
            _watcher.Renamed += (_, e) => Later(e.FullPath);
        }
        catch (Exception ex)
        {
            CrashLog.Write("DesktopFolderIcons.Watch", ex);
        }
    }

    private static void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>A new folder: wait a moment (it may be renamed straight away, "New folder" then a name).</summary>
    private static void Later(string path) => Task.Run(async () =>
    {
        await Task.Delay(1200);
        try
        {
            if (Directory.Exists(path) && Apply(path)) Notify(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("DesktopFolderIcons.New", ex);
        }
    });

    /// <summary>Gives one folder the blue icon. Returns true when something was changed.</summary>
    private static bool Apply(string folder)
    {
        lock (Gate)
        {
            var ini = Path.Combine(folder, "desktop.ini");
            var change = new Change { Folder = folder };
            Encoding encoding = Encoding.Unicode;
            var lines = new List<string>();
            if (File.Exists(ini))
            {
                string text;
                using (var reader = new StreamReader(ini, Encoding.Default, detectEncodingFromByteOrderMarks: true))
                {
                    text = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                }
                if (text.Contains(Marker, StringComparison.OrdinalIgnoreCase)) return false; // already ours
                foreach (var line in text.Split('\n')) lines.Add(line.TrimEnd('\r'));
                bool hasIcon = lines.Any(l =>
                {
                    var t = l.TrimStart();
                    return t.StartsWith("IconResource", StringComparison.OrdinalIgnoreCase) ||
                           t.StartsWith("IconFile", StringComparison.OrdinalIgnoreCase);
                });
                if (hasIcon) return false; // the folder has its own icon: leave it
            }
            else
            {
                change.CreatedIni = true;
            }

            string iconLine = $"IconResource={IconFile},0";
            int section = lines.FindIndex(l => l.Trim().Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase));
            if (section < 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                lines.Add("[.ShellClassInfo]");
                lines.Add(iconLine);
            }
            else
            {
                lines.Insert(section + 1, iconLine);
            }

            try
            {
                if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal);
                File.WriteAllText(ini, string.Join("\r\n", lines).TrimEnd() + "\r\n", encoding);
                File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

                var attributes = File.GetAttributes(folder);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System)) == 0)
                {
                    // Windows only reads desktop.ini in folders marked read-only or system.
                    File.SetAttributes(folder, attributes | FileAttributes.ReadOnly);
                    change.AddedReadOnly = true;
                }
                change.AddedGitExclude = ExcludeFromGit(folder);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"DesktopFolderIcons.Apply {folder}", ex);
                return false;
            }

            var state = Load();
            state.RemoveAll(c => string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase));
            state.Add(change);
            Save(state);
            return true;
        }
    }

    /// <summary>A Git project on the desktop must not see desktop.ini as a new file: it is listed in the
    /// project's local exclude list (never committed).</summary>
    private static bool ExcludeFromGit(string folder)
    {
        var exclude = Path.Combine(folder, ".git", "info", "exclude");
        if (!Directory.Exists(Path.Combine(folder, ".git"))) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            var existing = File.Exists(exclude) ? File.ReadAllText(exclude) : "";
            if (existing.Split('\n').Any(l => l.Trim() == "desktop.ini")) return false;
            File.AppendAllText(exclude, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + "desktop.ini\n");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Uninstall: puts every changed desktop folder back, right now.</summary>
    public static void RestoreNow()
    {
        StopWatching();
        RestoreAll();
    }

    private static void RestoreAll()
    {
        lock (Gate)
        {
            foreach (var change in Load())
            {
                try { Restore(change); }
                catch (Exception ex) { CrashLog.Write($"DesktopFolderIcons.Restore {change.Folder}", ex); }
            }
            Save(new List<Change>());
        }
        Notify(null);
    }

    private static void Restore(Change change)
    {
        var folder = change.Folder;
        if (!Directory.Exists(folder)) return;
        var ini = Path.Combine(folder, "desktop.ini");
        if (File.Exists(ini))
        {
            string text;
            Encoding encoding;
            using (var reader = new StreamReader(ini, Encoding.Default, detectEncodingFromByteOrderMarks: true))
            {
                text = reader.ReadToEnd();
                encoding = reader.CurrentEncoding;
            }
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r'))
                .Where(l => !(l.TrimStart().StartsWith("IconResource", StringComparison.OrdinalIgnoreCase) && l.Contains(Marker, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            bool onlyEmptySection = lines.All(l => l.Trim().Length == 0 || l.Trim().Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase));
            File.SetAttributes(ini, FileAttributes.Normal);
            if (change.CreatedIni && onlyEmptySection) File.Delete(ini);
            else
            {
                File.WriteAllText(ini, string.Join("\r\n", lines).TrimEnd() + "\r\n", encoding);
                File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            }
        }
        if (change.AddedReadOnly)
        {
            var attributes = File.GetAttributes(folder);
            File.SetAttributes(folder, attributes & ~FileAttributes.ReadOnly);
        }
        if (change.AddedGitExclude)
        {
            var exclude = Path.Combine(folder, ".git", "info", "exclude");
            if (File.Exists(exclude))
            {
                var kept = File.ReadAllLines(exclude).Where(l => l.Trim() != "desktop.ini");
                File.WriteAllLines(exclude, kept);
            }
        }
    }

    private static List<Change> Load()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(StateFile)) ?? new List<Change>();
        }
        catch
        {
            // A damaged state file: start over (folders already changed keep working).
        }
        return new List<Change>();
    }

    private static void Save(List<Change> state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            CrashLog.Write("DesktopFolderIcons.Save", ex);
        }
    }

    // ---------------- Explorer refresh ----------------

    private const int SHCNE_UPDATEDIR = 0x00001000, SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNF_PATHW = 0x0005, SHCNF_FLUSH = 0x1000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);

    private static void Notify(string? folder)
    {
        try
        {
            if (folder != null)
            {
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, folder, IntPtr.Zero);
            }
            else
            {
                foreach (var desktop in Desktops())
                {
                    foreach (var dir in SafeDirectories(desktop))
                        SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, dir, IntPtr.Zero);
                    SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, desktop, IntPtr.Zero);
                }
            }
        }
        catch
        {
            // Cosmetic: Explorer catches up on its own.
        }
    }
}
