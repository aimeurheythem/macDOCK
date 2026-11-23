using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MacDock.Services;

/// <summary>One app that is (or recently was) playing sound, with its own volume.</summary>
internal sealed class AppAudio
{
    private readonly ISimpleAudioVolume _volume;

    public uint ProcessId { get; }
    public string Name { get; }
    public string? ExePath { get; }

    internal AppAudio(uint pid, string name, string? exePath, ISimpleAudioVolume volume)
    {
        ProcessId = pid;
        Name = name;
        ExePath = exePath;
        _volume = volume;
    }

    public float Level
    {
        get { try { _volume.GetMasterVolume(out float v); return v; } catch { return 0; } }
        set { try { _volume.SetMasterVolume(Math.Clamp(value, 0f, 1f), IntPtr.Zero); } catch { } }
    }

    public bool Muted
    {
        get { try { _volume.GetMute(out bool m); return m; } catch { return false; } }
        set { try { _volume.SetMute(value, IntPtr.Zero); } catch { } }
    }
}

/// <summary>
/// Per-app volume (like Windows' Volume Mixer): the audio sessions of the default playback device.
/// Browsers (Chrome, Brave, Edge) play all their tabs through one session, so they appear as one app:
/// Windows offers no way to set the volume of a single tab from outside the browser.
/// UI thread only.
/// </summary>
internal static class AudioMixer
{
    public static List<AppAudio> GetApps()
    {
        var result = new List<AppAudio>();
        var byProcess = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var enumerator = (IMMDeviceEnumerator)AudioVolume.CreateDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) return result;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out var managerObj) != 0) return result;
            var manager = (IAudioSessionManager2)managerObj;
            if (manager.GetSessionEnumerator(out var sessions) != 0) return result;
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0) continue;
                var control2 = (IAudioSessionControl2)control;
                if (control2.IsSystemSoundsSession() == 0) continue; // S_OK = system sounds: not an app
                control2.GetState(out int state);
                if (state == 2) continue; // AudioSessionStateExpired
                control2.GetProcessId(out uint pid);
                if (pid == 0) continue;

                string? exe = ExePath(pid);
                string name = DisplayName(control2, exe, pid);
                var app = new AppAudio(pid, name, exe, (ISimpleAudioVolume)control);
                // One row per program (browsers and others can have several sessions).
                if (exe != null && byProcess.ContainsKey(exe)) continue;
                if (exe != null) byProcess[exe] = app;
                result.Add(app);
            }
        }
        catch
        {
            // No audio device / service.
        }
        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string DisplayName(IAudioSessionControl2 control, string? exe, uint pid)
    {
        try
        {
            control.GetDisplayName(out var ptr);
            var name = Marshal.PtrToStringUni(ptr);
            Marshal.FreeCoTaskMem(ptr);
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@')) return name;
        }
        catch
        {
            // Fall back to the program's description.
        }
        if (exe != null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch
            {
                // Fall back to the file name.
            }
            return Path.GetFileNameWithoutExtension(exe);
        }
        return $"App {pid}";
    }

    private static string? ExePath(uint pid)
    {
        IntPtr handle = Native.Win32.OpenProcess(Native.Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Native.Win32.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Native.Win32.CloseHandle(handle);
        }
    }

    // ---------------- COM declarations ----------------


    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, IntPtr context);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
