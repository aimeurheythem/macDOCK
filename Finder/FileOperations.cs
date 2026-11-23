using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using MacDock.Services;

namespace MacDock.Finder;

/// <summary>
/// File actions behind the Finder window. Copy, move and delete go through the Windows shell, so they
/// get its progress dialog, conflict prompts and Undo, exactly like File Explorer.
/// </summary>
internal static class FileOperations
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_MOVE = 1, FO_COPY = 2;
    private const ushort FOF_RENAMEONCOLLISION = 0x0008, FOF_ALLOWUNDO = 0x0040;

    /// <summary>Copies (or moves) items into a folder. Copies into their own folder get a "- Copy" name.</summary>
    public static void CopyOrMove(IntPtr owner, IReadOnlyCollection<string> paths, string destination, bool move)
    {
        var items = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (items.Count == 0 || !Directory.Exists(destination)) return;
        // Moving something into the folder it is already in does nothing.
        if (move && items.All(p => FinderAssets.Same(Path.GetDirectoryName(p), destination))) return;
        // A folder can't go inside itself.
        if (items.Any(p => Directory.Exists(p) && (destination + "\\").StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            return;

        bool sameFolder = items.Any(p => FinderAssets.Same(Path.GetDirectoryName(p), destination));
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = move ? FO_MOVE : FO_COPY,
            pFrom = string.Join('\0', items) + "\0\0",
            pTo = destination + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | (sameFolder && !move ? FOF_RENAMEONCOLLISION : 0)),
        };
        try { SHFileOperation(ref op); }
        catch (Exception ex) { Debug.WriteLine($"File operation failed: {ex.Message}"); }
    }

    public static void MoveToTrash(IntPtr owner, IReadOnlyCollection<string> paths) => RecycleBin.MoveToBin(owner, paths);

    /// <summary>Renames in place. Returns an error message, or null on success.</summary>
    public static string? Rename(string path, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return "The name can't be empty.";
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name can't contain any of these characters: \\ / : * ? \" < > |";
        var dir = Path.GetDirectoryName(path);
        if (dir == null) return "This item can't be renamed.";
        var target = Path.Combine(dir, newName);
        if (string.Equals(target, path, StringComparison.Ordinal)) return null;
        bool caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            return $"The name “{newName}” is already taken. Please choose a different name.";
        try
        {
            if (Directory.Exists(path))
            {
                if (caseOnly)
                {
                    var temp = path + ".~rename";
                    Directory.Move(path, temp);
                    Directory.Move(temp, target);
                }
                else Directory.Move(path, target);
            }
            else File.Move(path, target);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Creates "untitled folder" (or "untitled folder 2", ...) and returns its path.</summary>
    public static string? NewFolder(string parent)
    {
        try
        {
            string path = Path.Combine(parent, "untitled folder");
            for (int i = 2; Directory.Exists(path) || File.Exists(path); i++)
                path = Path.Combine(parent, $"untitled folder {i}");
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"New folder failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opens a file with its default app, or runs a program.</summary>
    public static void Open(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = dir ?? "" });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open {path} failed: {ex.Message}");
        }
    }

    /// <summary>Windows' "Open with" chooser.</summary>
    public static void OpenWith(string path)
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false }); }
        catch (Exception ex) { Debug.WriteLine($"Open with failed: {ex.Message}"); }
    }

    public static void ShowInExplorer(string path)
    {
        ExplorerRedirect.BypassUntil = DateTime.UtcNow.AddSeconds(4); // asked for File Explorer on purpose
        try
        {
            if (Directory.Exists(path) && !File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Debug.WriteLine($"Explorer failed: {ex.Message}"); }
    }

    /// <summary>Windows Terminal in the folder, or PowerShell when Terminal isn't installed.</summary>
    public static void OpenTerminal(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder }); }
            catch (Exception ex) { Debug.WriteLine($"Terminal failed: {ex.Message}"); }
        }
    }

    // ---------------- Get Info (Windows properties sheet) ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    private const uint SEE_MASK_INVOKEIDLIST = 0x0C;

    public static void ShowProperties(IntPtr owner, string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
        };
        try { ShellExecuteEx(ref info); }
        catch (Exception ex) { Debug.WriteLine($"Properties failed: {ex.Message}"); }
    }

    // ---------------- Clipboard ----------------

    private const string DropEffectFormat = "Preferred DropEffect";

    /// <summary>Puts files on the clipboard the way File Explorer does (so Explorer can paste them too).</summary>
    public static void SetClipboard(IReadOnlyCollection<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        var effect = new MemoryStream(BitConverter.GetBytes((int)(cut ? DragDropEffects.Move : DragDropEffects.Copy | DragDropEffects.Link)));
        data.SetData(DropEffectFormat, effect);
        try { Clipboard.SetDataObject(data, true); }
        catch (Exception ex) { Debug.WriteLine($"Clipboard failed: {ex.Message}"); }
    }

    /// <summary>Files on the clipboard and whether they were cut (move) rather than copied.</summary>
    public static (List<string> Paths, bool Cut) GetClipboard()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return (new List<string>(), false);
            var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
            bool cut = false;
            if (Clipboard.GetData(DropEffectFormat) is MemoryStream stream && stream.Length >= 4)
            {
                var bytes = new byte[4];
                stream.Position = 0;
                _ = stream.Read(bytes, 0, 4);
                cut = (BitConverter.ToInt32(bytes, 0) & (int)DragDropEffects.Move) != 0;
            }
            return (paths, cut);
        }
        catch
        {
            return (new List<string>(), false);
        }
    }

    public static bool ClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch { return false; }
    }
}
