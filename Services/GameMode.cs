using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacDock.Services;

/// <summary>
/// While a fullscreen app (a game, a video) is in front, MacDock steps aside: its background timers
/// skip their work, its process runs at lower priority and in Windows' power-saving mode (EcoQoS),
/// and it gives back memory it isn't using. Everything returns to normal when the app leaves fullscreen.
/// </summary>
internal static class GameMode
{
    private static volatile bool _active;

    /// <summary>True while a fullscreen app is in front. Read from any thread.</summary>
    public static bool Active => _active;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    /// <summary>Raised on the UI thread when a game (or video) goes fullscreen or leaves it.</summary>
    public static event Action<bool>? Changed;

    public static void Set(bool on)
    {
        if (_active == on) return;
        _active = on;
        try { Changed?.Invoke(on); } catch (Exception ex) { CrashLog.Write("GameMode.Changed", ex); }
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = on ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            var state = new PowerThrottlingState { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = on ? 1u : 0u };
            SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottlingState>());
            if (on)
            {
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); // hand unused RAM to the game
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("GameMode.Set", ex);
        }
    }
}
