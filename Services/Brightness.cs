using System.Runtime.InteropServices;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Screen brightness for the Control Center "Display" slider.
///  - Laptop screens: Windows' WMI brightness interface.
///  - External monitors: DDC/CI (the monitor's own brightness setting over the video cable), when the
///    monitor supports it.
/// All calls are slow (tens of ms): use from a background thread. Thread-safe.
/// </summary>
internal static class Brightness
{
    private static readonly object Gate = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

    /// <summary>Current brightness 0..1 of the first screen that supports it, or null when none does.</summary>
    public static double? Get()
    {
        lock (Gate)
        {
            if (WmiGet() is double laptop) return laptop;
            double? result = null;
            ForEachMonitor(h =>
            {
                if (result == null && GetMonitorBrightness(h, out uint min, out uint cur, out uint max) && max > min)
                    result = (cur - min) / (double)(max - min);
            });
            return result;
        }
    }

    /// <summary>Sets every screen that supports it to <paramref name="level"/> (0..1).</summary>
    public static void Set(double level)
    {
        level = Math.Clamp(level, 0, 1);
        lock (Gate)
        {
            WmiSet(level);
            ForEachMonitor(h =>
            {
                if (GetMonitorBrightness(h, out uint min, out _, out uint max) && max > min)
                    SetMonitorBrightness(h, (uint)Math.Round(min + level * (max - min)));
            });
        }
    }

    private static void ForEachMonitor(Action<IntPtr> action)
    {
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0) return true;
                var physical = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return true;
                try
                {
                    foreach (var p in physical) action(p.Handle);
                }
                finally
                {
                    DestroyPhysicalMonitors(count, physical);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // dxva2 missing or the driver refused: no external brightness.
        }
    }

    // ---------------- Laptop screen (WMI, through its scripting COM interface) ----------------

    private static dynamic? Wmi()
    {
        var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
        if (type == null) return null;
        dynamic locator = Activator.CreateInstance(type)!;
        return locator.ConnectServer(".", @"root\WMI");
    }

    private static double? WmiGet()
    {
        try
        {
            var service = Wmi();
            if (service == null) return null;
            foreach (dynamic item in service.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                return (byte)item.CurrentBrightness / 100.0;
        }
        catch
        {
            // Not a laptop screen (desktops have no WMI brightness).
        }
        return null;
    }

    private static void WmiSet(double level)
    {
        try
        {
            var service = Wmi();
            if (service == null) return;
            foreach (dynamic item in service.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods"))
            {
                dynamic method = item.Methods_.Item("WmiSetBrightness");
                dynamic input = method.InParameters.SpawnInstance_();
                input.Properties_.Item("Timeout").Value = 1;
                input.Properties_.Item("Brightness").Value = (byte)Math.Round(level * 100);
                item.ExecMethod_("WmiSetBrightness", input);
            }
        }
        catch
        {
            // No laptop screen.
        }
    }
}
