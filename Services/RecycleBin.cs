using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacDock.Services;

/// <summary>Recycle Bin: state, open, empty (with the Windows confirmation), and moving files into it.</summary>
internal static class RecycleBin
{
    // SHQUERYRBINFO is byte-packed on 32-bit and naturally aligned on 64-bit.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct QueryInfo32 { public int cbSize; public long i64Size; public long i64NumItems; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo64 { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int Query32(string? rootPath, ref QueryInfo32 info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int Query64(string? rootPath, ref QueryInfo64 info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

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

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Number of items in all recycle bins (0 on error).</summary>
    public static long ItemCount()
    {
        try
        {
            if (IntPtr.Size == 8)
            {
                var info = new QueryInfo64 { cbSize = Marshal.SizeOf<QueryInfo64>() };
                return Query64(null, ref info) == 0 ? info.i64NumItems : 0;
            }
            else
            {
                var info = new QueryInfo32 { cbSize = Marshal.SizeOf<QueryInfo32>() };
                return Query32(null, ref info) == 0 ? info.i64NumItems : 0;
            }
        }
        catch
        {
            return 0;
        }
    }

    public static void Open()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Open Recycle Bin failed: {ex.Message}"); }
    }

    /// <summary>Empties the bin. Windows shows its own "Are you sure?" confirmation.</summary>
    public static void Empty(IntPtr owner)
    {
        try { SHEmptyRecycleBin(owner, null, 0); }
        catch (Exception ex) { Debug.WriteLine($"Empty Recycle Bin failed: {ex.Message}"); }
    }

    /// <summary>Moves files/folders to the Recycle Bin (undoable). Warns if something can't be recycled.</summary>
    public static void MoveToBin(IntPtr owner, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0", // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_WANTNUKEWARNING,
        };
        try { SHFileOperation(ref op); }
        catch (Exception ex) { Debug.WriteLine($"Move to Recycle Bin failed: {ex.Message}"); }
    }
}
