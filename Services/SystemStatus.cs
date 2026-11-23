using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using MacDock.Native;

namespace MacDock.Services;

internal enum NetworkKind { None, Wifi, Ethernet }

/// <summary>Current network connection, as shown in the menu bar.</summary>
internal sealed record NetworkState(NetworkKind Kind, string? Name, int SignalPercent, bool HasWifiAdapter)
{
    public static readonly NetworkState Offline = new(NetworkKind.None, null, 0, false);
}

/// <summary>A Wi-Fi network in range.</summary>
internal sealed record WifiNetwork(Guid Interface, string Ssid, int Signal, bool Secured, bool Connected, string? Profile);

/// <summary>Battery state; <see cref="Present"/> is false on desktops.</summary>
internal sealed record BatteryState(bool Present, int Percent, bool Charging);

/// <summary>An installed keyboard layout / input language.</summary>
internal sealed record KeyboardLayout(IntPtr Handle, string Code, string Name);

/// <summary>Reads Wi-Fi, battery and keyboard-language state. All methods are thread-safe.</summary>
internal static class SystemStatus
{
    // ---------------- Network ----------------

    /// <summary>Wi-Fi first (with name and signal), else a wired connection, else offline. Takes a few ms: call off the UI thread.</summary>
    public static NetworkState GetNetwork()
    {
        var wifi = Wlan.Query();
        if (wifi is { Connected: true }) return new NetworkState(NetworkKind.Wifi, wifi.Ssid, wifi.Signal, true);

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx)) continue;
                var name = nic.Description;
                if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)) continue;
                if (!nic.GetIPProperties().GatewayAddresses.Any()) continue; // not really connected
                return new NetworkState(NetworkKind.Ethernet, "Ethernet", 100, wifi != null);
            }
        }
        catch
        {
            // Treat as offline.
        }
        return NetworkState.Offline with { HasWifiAdapter = wifi != null };
    }

    /// <summary>Wi-Fi networks in range, strongest first (one entry per name). Empty without Wi-Fi.</summary>
    public static List<WifiNetwork> GetWifiNetworks() => Wlan.Available();

    /// <summary>Joins a network the PC already knows (saved profile). False = needs Windows' dialog (password).</summary>
    public static bool ConnectWifi(WifiNetwork network) => Wlan.Connect(network);

    /// <summary>IPv4 address of the active wired or wireless adapter, or null.</summary>
    public static string? GetIpAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any()) continue;
                var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ip != null) return ip.Address.ToString();
            }
        }
        catch
        {
            // No address to show.
        }
        return null;
    }

    /// <summary>Native Wi-Fi API (wlanapi.dll): the connected network's name and signal quality.</summary>
    private static class Wlan
    {
        [DllImport("wlanapi.dll")]
        private static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, int flags, IntPtr reserved, out IntPtr list);

        [DllImport("wlanapi.dll")]
        private static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern int WlanConnect(IntPtr handle, ref Guid iface, ref ConnectionParameters parameters, IntPtr reserved);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ConnectionParameters
        {
            public int Mode;           // 0 = wlan_connection_mode_profile
            public string Profile;
            public IntPtr Ssid;
            public IntPtr DesiredBssids;
            public int BssType;        // 1 = infrastructure
            public int Flags;
        }

        private const int AvailableNetworkSize = 628; // WLAN_AVAILABLE_NETWORK
        private const int FlagConnected = 1, FlagHasProfile = 2;

        public static List<WifiNetwork> Available()
        {
            var result = new Dictionary<string, WifiNetwork>();
            IntPtr handle = IntPtr.Zero, interfaces = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return new();
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out interfaces) != 0) return new();
                int count = Marshal.ReadInt32(interfaces);
                for (int i = 0; i < count; i++)
                {
                    var guid = Marshal.PtrToStructure<Guid>(interfaces + 8 + i * InterfaceInfoSize);
                    WlanScan(handle, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // refresh for next time
                    if (WlanGetAvailableNetworkList(handle, ref guid, 0, IntPtr.Zero, out var list) != 0) continue;
                    try
                    {
                        int n = Marshal.ReadInt32(list);
                        for (int k = 0; k < n; k++)
                        {
                            IntPtr item = list + 8 + k * AvailableNetworkSize;
                            int ssidLength = Math.Clamp(Marshal.ReadInt32(item + 512), 0, 32);
                            if (ssidLength == 0) continue; // hidden network
                            var bytes = new byte[ssidLength];
                            Marshal.Copy(item + 516, bytes, 0, ssidLength);
                            string ssid = Encoding.UTF8.GetString(bytes);
                            int signal = Math.Clamp(Marshal.ReadInt32(item + 604), 0, 100);
                            bool secured = Marshal.ReadInt32(item + 608) != 0;
                            int flags = Marshal.ReadInt32(item + 620);
                            string? profile = (flags & FlagHasProfile) != 0 ? Marshal.PtrToStringUni(item) : null;
                            var network = new WifiNetwork(guid, ssid, signal, secured, (flags & FlagConnected) != 0, profile);
                            // Same name listed twice (with and without profile): keep the most useful one.
                            if (!result.TryGetValue(ssid, out var existing) || network.Connected ||
                                (!existing.Connected && (network.Profile != null && existing.Profile == null || network.Signal > existing.Signal)))
                                result[ssid] = network;
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(list);
                    }
                }
            }
            catch
            {
                return new();
            }
            finally
            {
                if (interfaces != IntPtr.Zero) WlanFreeMemory(interfaces);
                if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
            }
            return result.Values.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal).ToList();
        }

        public static bool Connect(WifiNetwork network)
        {
            if (string.IsNullOrEmpty(network.Profile)) return false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return false;
                var guid = network.Interface;
                var parameters = new ConnectionParameters { Mode = 0, Profile = network.Profile, BssType = 1 };
                return WlanConnect(handle, ref guid, ref parameters, IntPtr.Zero) == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
            }
        }

        public sealed record Result(bool Connected, string? Ssid, int Signal);

        [DllImport("wlanapi.dll")]
        private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

        [DllImport("wlanapi.dll")]
        private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

        [DllImport("wlanapi.dll")]
        private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved,
            out int dataSize, out IntPtr data, IntPtr opcodeValueType);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        private const int CurrentConnection = 7;   // wlan_intf_opcode_current_connection
        private const int StateConnected = 1;      // wlan_interface_state_connected
        private const int InterfaceInfoSize = 532; // GUID (16) + WCHAR[256] (512) + state (4)

        /// <summary>Null when there is no Wi-Fi adapter (or the WLAN service is off).</summary>
        public static Result? Query()
        {
            IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return null;
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return null;

                int count = Marshal.ReadInt32(list);
                if (count == 0) return null;
                for (int i = 0; i < count; i++)
                {
                    IntPtr info = list + 8 + i * InterfaceInfoSize; // dwNumberOfItems, dwIndex, then items
                    int state = Marshal.ReadInt32(info + 16 + 512);
                    if (state != StateConnected) continue;

                    var guid = Marshal.PtrToStructure<Guid>(info);
                    if (WlanQueryInterface(handle, ref guid, CurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) continue;
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state(4) mode(4) profile WCHAR[256] (512), then
                        // WLAN_ASSOCIATION_ATTRIBUTES at 520: SSID length(4) + bytes[32], BSS type(4),
                        // BSSID[6] + pad(2), PHY type(4), PHY index(4), signal quality(4) at 576.
                        int ssidLength = Math.Clamp(Marshal.ReadInt32(data + 520), 0, 32);
                        var ssidBytes = new byte[ssidLength];
                        Marshal.Copy(data + 524, ssidBytes, 0, ssidLength);
                        int signal = Math.Clamp(Marshal.ReadInt32(data + 576), 0, 100);
                        return new Result(true, Encoding.UTF8.GetString(ssidBytes), signal);
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }
                return new Result(false, null, 0);
            }
            catch
            {
                return null; // wlanapi.dll missing (Server editions without the WLAN feature)
            }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
    }

    // ---------------- Battery ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static BatteryState GetBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return new BatteryState(false, 0, false);
        bool present = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100;
        return new BatteryState(present, s.BatteryLifePercent, s.ACLineStatus == 1);
    }

    // ---------------- Keyboard ----------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? list);

    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    /// <summary>Input language of the window in front ("EN", "FR", "AR"...).</summary>
    public static KeyboardLayout GetCurrentLayout()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        uint thread = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, out _);
        return Describe(GetKeyboardLayout(thread));
    }

    public static List<KeyboardLayout> GetInstalledLayouts()
    {
        int count = GetKeyboardLayoutList(0, null);
        var handles = new IntPtr[Math.Max(count, 0)];
        if (count > 0) GetKeyboardLayoutList(count, handles);
        return handles.Select(Describe).ToList();
    }

    /// <summary>Switches the input language of <paramref name="window"/> (the app in front).</summary>
    public static void SwitchLayout(IntPtr window, KeyboardLayout layout)
    {
        if (window != IntPtr.Zero)
            Win32.PostMessage(window, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout.Handle);
    }

    private static KeyboardLayout Describe(IntPtr hkl)
    {
        int langId = (int)(hkl.ToInt64() & 0xFFFF);
        try
        {
            var culture = CultureInfo.GetCultureInfo(langId);
            var name = culture.DisplayName;
            return new KeyboardLayout(hkl, culture.TwoLetterISOLanguageName.ToUpperInvariant(), name);
        }
        catch
        {
            return new KeyboardLayout(hkl, "??", $"Layout {langId:X4}");
        }
    }
}
