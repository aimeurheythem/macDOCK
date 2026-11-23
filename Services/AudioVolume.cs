using System.Runtime.InteropServices;

namespace MacDock.Services;

/// <summary>
/// Master volume of the default playback device (Windows Core Audio). UI thread only.
/// The device is looked up again when it fails, so plugging in headphones keeps working.
/// </summary>
internal sealed class AudioVolume
{
    private IAudioEndpointVolume? _endpoint;
    private DateTime _endpointAt;

    /// <summary>0..1, or null when there is no playback device.</summary>
    public float? Level
    {
        get => Call(e => { Marshal.ThrowExceptionForHR(e.GetMasterVolumeLevelScalar(out float v)); return (float?)v; });
        set
        {
            if (value is float v)
                Call(e => { e.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), IntPtr.Zero); return (float?)0; });
        }
    }

    public bool Muted
    {
        get => Call(e => { Marshal.ThrowExceptionForHR(e.GetMute(out bool m)); return (float?)(m ? 1 : 0); }) == 1;
        set => Call(e => { e.SetMute(value, IntPtr.Zero); return (float?)0; });
    }

    private float? Call(Func<IAudioEndpointVolume, float?> action)
    {
        // Re-resolve every 15 s: the default device can change (headphones, HDMI...).
        if (_endpoint == null || DateTime.UtcNow - _endpointAt > TimeSpan.FromSeconds(15)) Resolve();
        if (_endpoint == null) return null;
        try
        {
            return action(_endpoint);
        }
        catch
        {
            Resolve();
            try { return _endpoint == null ? null : action(_endpoint); }
            catch { return null; }
        }
    }

    private void Resolve()
    {
        _endpointAt = DateTime.UtcNow;
        _endpoint = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)CreateDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0) return;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) != 0) return;
            _endpoint = (IAudioEndpointVolume)obj;
        }
        catch
        {
            _endpoint = null; // no audio service / device
        }
    }

    // ---------------- COM declarations (only the methods used are typed) ----------------

    /// <summary>
    /// The Core Audio device enumerator. Created from its class id (not a [ComImport] class): two such
    /// classes for the same COM object in one app (this one and the mixer's) made .NET fail to cast
    /// between them, so the volume stopped working once the mixer had been opened.
    /// </summary>
    internal static object CreateDeviceEnumerator() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

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

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, IntPtr context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, IntPtr context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
