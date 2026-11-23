using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacDock.Services;

/// <summary>Lock, sleep, sign out, restart, shut down.</summary>
internal static class PowerActions
{
    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public static void Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void SignOut() => RunShutdown("/l");

    public static void Restart() => RunShutdown("/r /t 0");

    public static void ShutDown() => RunShutdown("/s /t 0");

    private static void RunShutdown(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"shutdown {args} failed: {ex.Message}");
        }
    }
}
