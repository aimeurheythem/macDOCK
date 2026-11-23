using System.Diagnostics;
using Microsoft.Win32;

namespace MacDock.Services;

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MacDock";

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <param name="exePath">Program to start (default: this exe). The installer passes the installed copy.</param>
    public static void SetEnabled(bool enabled, string? exePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            exePath ??= ExePath;
            if (enabled && exePath != null)
                key.SetValue(ValueName, $"\"{exePath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Startup setting failed: {ex.Message}");
        }
    }
}
