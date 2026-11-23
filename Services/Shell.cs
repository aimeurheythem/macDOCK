using System.Diagnostics;

namespace MacDock.Services;

/// <summary>Opens files, folders, programs and URIs (ms-settings:, https:...) like a double-click would.</summary>
internal static class Shell
{
    public static void Open(string target, string? arguments = null)
    {
        if (SystemSettingsWindow.IsSettingsExe(target)) { SystemSettingsWindow.OpenFromShortcut(); return; } // see OpenFromShortcut
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "" });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open {target} failed: {ex.Message}");
        }
    }
}
