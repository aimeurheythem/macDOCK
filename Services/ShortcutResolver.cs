using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MacDock.Services;

/// <summary>Reads the target path of a .lnk shortcut through the Shell's IShellLink.</summary>
internal static class ShortcutResolver
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // Only the first vtable slot is needed.
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }

    private const uint SLGP_RAWPATH = 0x4;
    private const int STGM_READ = 0;

    public static bool IsShortcut(string path) =>
        string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the expanded target path, or null (e.g. MSI "advertised" shortcuts have none).</summary>
    public static string? GetTarget(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnkPath, STGM_READ);

            var sb = new StringBuilder(1024);
            if (((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH) != 0)
                return null;

            var target = Environment.ExpandEnvironmentVariables(sb.ToString());
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }
}
