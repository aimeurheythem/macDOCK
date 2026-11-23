using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MacDock.Services;

/// <summary>
/// Hands memory MacDock no longer uses back to Windows: after start-up (icons, wallpapers and pictures
/// were loaded) and after a big window (System Settings, Finder) closes. Keeps MacDock light in Task
/// Manager without making anything slower afterwards.
/// </summary>
internal static class MemoryTrim
{
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    private static DispatcherTimer? _soon, _guard;

    /// <summary>
    /// Every minute, when MacDock has nothing else to do: if it holds more than 280 MB, it cleans up.
    /// A safety net, so it can never slowly grow to take memory from games and other apps.
    /// </summary>
    public static void StartGuard()
    {
        if (_guard != null) return;
        _guard = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(60) };
        _guard.Tick += (_, _) =>
        {
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                if (self.PrivateMemorySize64 > 280L * 1024 * 1024) Now();
            }
            catch { }
        };
        _guard.Start();
    }

    public static void Now()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers(); // pictures release their native memory here
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch (Exception ex) { CrashLog.Write("MemoryTrim", ex); }
    }

    /// <summary>Trims a few seconds from now (repeated calls just push it back). UI thread.</summary>
    public static void Soon(double seconds = 3)
    {
        _soon ??= new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        _soon.Stop();
        _soon.Interval = TimeSpan.FromSeconds(seconds);
        _soon.Tick -= OnTick;
        _soon.Tick += OnTick;
        _soon.Start();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        _soon?.Stop();
        Now();
    }
}
