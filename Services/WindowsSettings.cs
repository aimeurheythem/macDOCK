using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MacDock.Services;

/// <summary>
/// Reads and changes Windows settings directly (registry, SystemParametersInfo, Core Audio, powercfg...),
/// so MacDock's System Settings can show them without opening the Windows Settings app. Every call is
/// best effort: a setting that can't be read gives its default, one that can't be written is ignored.
/// </summary>
internal static class WindowsSettings
{
    // ---------------- Plumbing ----------------

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint uParam, IntPtr pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint uParam, ref int pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint uParam, string pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint uParam, StringBuilder pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint uParam, ref AnimationInfo pvParam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint uParam, ref StickyKeys pvParam, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    [StructLayout(LayoutKind.Sequential)] private struct AnimationInfo { public int cbSize, iMinAnimate; }
    [StructLayout(LayoutKind.Sequential)] private struct StickyKeys { public int cbSize, dwFlags; }

    private const uint SPIF_SAVE = 0x1 | 0x2; // update the user profile and tell every window

    /// <summary>
    /// Tells every window a setting changed. Sent from a background thread: it waits on each window in
    /// turn (up to a second for a busy one), which froze System Settings for seconds when done inline.
    /// </summary>
    private static void Broadcast(string area) => Task.Run(() =>
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, area, 0x0002 /* ABORTIFHUNG */, 1000, out _));

    private static T Get<T>(string key, string name, T fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) is T value ? value : fallback;
        }
        catch { return fallback; }
    }

    private static void Set(string key, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue(name, value, kind);
        }
        catch (Exception ex) { CrashLog.Write("WindowsSettings.Set " + name, ex); }
    }

    /// <summary>Runs a console tool hidden and returns its output ("" on failure).</summary>
    public static string Run(string exe, string args, int timeoutMs = 15000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
            return output.Result;
        }
        catch { return ""; }
    }

    // ---------------- Appearance ----------------

    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark => Get(Personalize, "AppsUseLightTheme", 1) == 0;

    public static void SetDark(bool dark)
    {
        Set(Personalize, "AppsUseLightTheme", dark ? 0 : 1);
        Set(Personalize, "SystemUsesLightTheme", dark ? 0 : 1);
        Broadcast("ImmersiveColorSet");
    }

    public static bool Transparency
    {
        get => Get(Personalize, "EnableTransparency", 1) != 0;
        set { Set(Personalize, "EnableTransparency", value ? 1 : 0); Broadcast("ImmersiveColorSet"); }
    }

    public static bool AccentOnTitleBars
    {
        get => Get(@"Software\Microsoft\Windows\DWM", "ColorPrevalence", 0) != 0;
        set { Set(@"Software\Microsoft\Windows\DWM", "ColorPrevalence", value ? 1 : 0); Broadcast("ImmersiveColorSet"); }
    }

    /// <summary>The Windows accent color (0xRRGGBB).</summary>
    public static int Accent
    {
        get
        {
            int abgr = Get(@"Software\Microsoft\Windows\DWM", "AccentColor", unchecked((int)0xFFD47800));
            return ((abgr & 0xFF) << 16) | (abgr & 0xFF00) | ((abgr >> 16) & 0xFF);
        }
    }

    /// <summary>Makes <paramref name="rgb"/> the Windows accent color (Start, taskbar, title bars, controls).</summary>
    public static void SetAccent(int rgb)
    {
        byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
        (byte, byte, byte) Mix(double t) => t >= 0
            ? ((byte)(r + (255 - r) * t), (byte)(g + (255 - g) * t), (byte)(b + (255 - b) * t))
            : ((byte)(r * (1 + t)), (byte)(g * (1 + t)), (byte)(b * (1 + t)));
        var shades = new[] { Mix(0.45), Mix(0.3), Mix(0.15), Mix(0), Mix(-0.25), Mix(-0.45), Mix(-0.65) };
        var palette = new List<byte>();
        foreach (var (sr, sg, sb) in shades) palette.AddRange(new byte[] { sr, sg, sb, 0 });
        palette.AddRange(new byte[] { 0xF7, 0x63, 0x0C, 0 });
        static int Abgr((byte R, byte G, byte B) c) => unchecked((int)(0xFF000000u | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R));
        const string accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
        Set(accent, "AccentPalette", palette.ToArray(), RegistryValueKind.Binary);
        Set(accent, "AccentColorMenu", Abgr(shades[4]));
        Set(accent, "StartColorMenu", Abgr(shades[5]));
        Set(@"Software\Microsoft\Windows\DWM", "AccentColor", Abgr(shades[3]));
        Set(@"Software\Microsoft\Windows\DWM", "ColorizationColor", unchecked((int)(0xC4000000u | (uint)rgb)));
        Set(@"Software\Microsoft\Windows\DWM", "ColorizationAfterglow", unchecked((int)(0xC4000000u | (uint)rgb)));
        Set(@"Control Panel\Desktop", "AutoColorization", "0", RegistryValueKind.String);
        Broadcast("ImmersiveColorSet");
    }

    // ---------------- Notifications ----------------

    private const string Push = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string NotifySettings = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    public static bool NotificationsOn
    {
        get => Get(Push, "ToastEnabled", 1) != 0;
        set => Set(Push, "ToastEnabled", value ? 1 : 0);
    }

    public static bool NotificationsOnLockScreen
    {
        get => Get(NotifySettings, "NOC_GLOBAL_SETTING_ALLOW_TOASTS_ABOVE_LOCK", 1) != 0;
        set => Set(NotifySettings, "NOC_GLOBAL_SETTING_ALLOW_TOASTS_ABOVE_LOCK", value ? 1 : 0);
    }

    public static bool NotificationSounds
    {
        get => Get(NotifySettings, "NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND", 1) != 0;
        set => Set(NotifySettings, "NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND", value ? 1 : 0);
    }

    public sealed record NotifyingApp(string Id, string Name, bool Enabled);

    /// <summary>Apps that have sent notifications, with their own on/off switch.</summary>
    public static List<NotifyingApp> NotifyingApps()
    {
        var list = new List<NotifyingApp>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(NotifySettings);
            if (root == null) return list;
            foreach (var id in root.GetSubKeyNames())
            {
                if (id.StartsWith("Windows.SystemToast", StringComparison.OrdinalIgnoreCase) || id.Contains("QuietHours")) continue;
                using var k = root.OpenSubKey(id);
                bool enabled = k?.GetValue("Enabled") is not int e || e != 0;
                list.Add(new NotifyingApp(id, FriendlyAppName(id), enabled));
            }
        }
        catch { }
        return list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void SetAppNotifications(string id, bool on) => Set(NotifySettings + "\\" + id, "Enabled", on ? 1 : 0);

    private static string FriendlyAppName(string id)
    {
        string name = id;
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        int under = name.LastIndexOf('_');
        if (under > 0 && bang > 0) name = name[..under]; // package family: drop the publisher id
        name = name.Split('\\').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1) name = parts.Last().Length > 2 ? parts.Last() : string.Join(" ", parts);
        name = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : id;
    }

    // ---------------- Privacy ----------------

    private const string Consent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static bool AppAccess(string capability) => !string.Equals(Get(Consent + capability, "Value", "Allow"), "Deny", StringComparison.OrdinalIgnoreCase);

    public static void SetAppAccess(string capability, bool allow) => Set(Consent + capability, "Value", allow ? "Allow" : "Deny", RegistryValueKind.String);

    public static bool AdvertisingId
    {
        get => Get(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1) != 0;
        set => Set(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", value ? 1 : 0);
    }

    // ---------------- Mouse & keyboard ----------------

    /// <summary>Pointer speed 1..20.</summary>
    public static int MouseSpeed
    {
        get { int v = 10; SystemParametersInfo(0x0070, 0, ref v, 0); return v; }
        set => SystemParametersInfo(0x0071, 0, new IntPtr(Math.Clamp(value, 1, 20)), SPIF_SAVE);
    }

    /// <summary>Lines per wheel notch 1..20.</summary>
    public static int ScrollLines
    {
        get { int v = 3; SystemParametersInfo(0x0068, 0, ref v, 0); return v; }
        set => SystemParametersInfo(0x0069, (uint)Math.Clamp(value, 1, 20), IntPtr.Zero, SPIF_SAVE);
    }

    /// <summary>Double-click speed in ms (200..900).</summary>
    public static int DoubleClickTime
    {
        get => (int)GetDoubleClickTime();
        set => SystemParametersInfo(0x0020, (uint)Math.Clamp(value, 200, 900), IntPtr.Zero, SPIF_SAVE);
    }

    public static bool SwapButtons
    {
        get => GetSystemMetrics(23) != 0;
        set => SystemParametersInfo(0x0021, value ? 1u : 0u, IntPtr.Zero, SPIF_SAVE);
    }

    /// <summary>Key repeat rate 0 (slow) .. 31 (fast).</summary>
    public static int KeyRepeat
    {
        get { int v = 31; SystemParametersInfo(0x000A, 0, ref v, 0); return v; }
        set => SystemParametersInfo(0x000B, (uint)Math.Clamp(value, 0, 31), IntPtr.Zero, SPIF_SAVE);
    }

    /// <summary>Delay until repeat 0 (short) .. 3 (long).</summary>
    public static int KeyDelay
    {
        get { int v = 1; SystemParametersInfo(0x0016, 0, ref v, 0); return v; }
        set => SystemParametersInfo(0x0017, (uint)Math.Clamp(value, 0, 3), IntPtr.Zero, SPIF_SAVE);
    }

    // ---------------- Accessibility ----------------

    public static bool ReduceMotion
    {
        get { int v = 1; SystemParametersInfo(0x1042, 0, ref v, 0); return v == 0; }
        set
        {
            SystemParametersInfo(0x1043, 0, new IntPtr(value ? 0 : 1), SPIF_SAVE);
            var info = new AnimationInfo { cbSize = 8, iMinAnimate = value ? 0 : 1 };
            SystemParametersInfo(0x0049, 8, ref info, SPIF_SAVE);
        }
    }

    public static bool StickyKeysOn
    {
        get { var s = new StickyKeys { cbSize = 8 }; SystemParametersInfo(0x003A, 8, ref s, 0); return (s.dwFlags & 1) != 0; }
        set
        {
            var s = new StickyKeys { cbSize = 8 };
            SystemParametersInfo(0x003A, 8, ref s, 0);
            s.dwFlags = value ? s.dwFlags | 1 : s.dwFlags & ~1;
            SystemParametersInfo(0x003B, 8, ref s, SPIF_SAVE);
        }
    }

    public static bool IsRunning(string process) => Process.GetProcessesByName(process).Length > 0;

    /// <summary>Starts or stops a Windows accessibility tool (Magnify, Narrator).</summary>
    public static void SetRunning(string process, bool on)
    {
        try
        {
            if (on) Process.Start(new ProcessStartInfo(process + ".exe") { UseShellExecute = true });
            else foreach (var p in Process.GetProcessesByName(process)) p.CloseMainWindow();
        }
        catch (Exception ex) { CrashLog.Write("WindowsSettings.SetRunning " + process, ex); }
    }

    // ---------------- Wallpaper ----------------

    public static string? Wallpaper
    {
        get
        {
            var sb = new StringBuilder(520);
            return SystemParametersInfo(0x0073, (uint)sb.Capacity, sb, 0) && sb.Length > 0 ? sb.ToString() : Get<string?>(@"Control Panel\Desktop", "WallPaper", null);
        }
    }

    /// <summary>Fill, Fit, Stretch, Centre, Tile, Span (as macOS names them).</summary>
    public static readonly string[] WallpaperFits = { "Fill Screen", "Fit to Screen", "Stretch to Fill Screen", "Centre", "Tile", "Span Screens" };
    private static readonly (string Style, string Tile)[] FitValues = { ("10", "0"), ("6", "0"), ("2", "0"), ("0", "0"), ("0", "1"), ("22", "0") };

    public static int WallpaperFit
    {
        get
        {
            string style = Get(@"Control Panel\Desktop", "WallpaperStyle", "10"), tile = Get(@"Control Panel\Desktop", "TileWallpaper", "0");
            int i = Array.FindIndex(FitValues, v => v.Style == style && v.Tile == tile);
            return i < 0 ? 0 : i;
        }
    }

    public static void SetWallpaper(string path, int? fit = null)
    {
        if (fit is int f && f >= 0 && f < FitValues.Length)
        {
            Set(@"Control Panel\Desktop", "WallpaperStyle", FitValues[f].Style, RegistryValueKind.String);
            Set(@"Control Panel\Desktop", "TileWallpaper", FitValues[f].Tile, RegistryValueKind.String);
        }
        SystemParametersInfo(0x0014, 0, path, SPIF_SAVE);
    }

    /// <summary>
    /// Sets the wallpaper through the desktop's own interface (IDesktopWallpaper), so it crossfades
    /// to the new picture instead of switching with a jump. Runs on its own thread; falls back to
    /// <see cref="SetWallpaper"/> when the interface isn't available.
    /// </summary>
    public static Task SetWallpaperSmoothAsync(string path, int? fit = null)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                var wallpaper = (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD"))!)!;
                if (fit is int f && f >= 0 && f < FitValues.Length)
                {
                    Set(@"Control Panel\Desktop", "WallpaperStyle", FitValues[f].Style, RegistryValueKind.String);
                    Set(@"Control Panel\Desktop", "TileWallpaper", FitValues[f].Tile, RegistryValueKind.String);
                    wallpaper.SetPosition(f switch { 1 => 3, 2 => 2, 3 => 0, 4 => 1, 5 => 5, _ => 4 }); // fill / fit / stretch / center / tile / span
                }
                wallpaper.SetWallpaper(null, path);
                Marshal.ReleaseComObject(wallpaper);
            }
            catch (Exception ex)
            {
                CrashLog.Write("SetWallpaperSmooth", ex);
                SetWallpaper(path, fit);
            }
            finally { done.TrySetResult(); }
        }) { IsBackground = true, Name = "MacDock wallpaper" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    [StructLayout(LayoutKind.Sequential)] private struct WallpaperRect { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out WallpaperRect rect);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
    }

    /// <summary>Pictures offered as wallpapers: Windows' own and the user's Pictures folder.</summary>
    public static List<string> WallpaperChoices()
    {
        var list = new List<string>();
        void Add(string dir, SearchOption depth)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                list.AddRange(Directory.EnumerateFiles(dir, "*.*", depth)
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                    .Take(40));
            }
            catch { }
        }
        Add(@"C:\Windows\Web\Wallpaper", SearchOption.AllDirectories);
        Add(@"C:\Windows\Web\4K\Wallpaper\Windows", SearchOption.TopDirectoryOnly);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), SearchOption.TopDirectoryOnly);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();
    }

    // ---------------- Power ----------------

    public sealed record PowerPlan(string Guid, string Name, bool Active);

    public static List<PowerPlan> PowerPlans()
    {
        var list = new List<PowerPlan>();
        foreach (Match m in Regex.Matches(Run("powercfg", "/l"), @"([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})\s+\((.+?)\)\s*(\*)?"))
            list.Add(new PowerPlan(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success));
        return list;
    }

    public static void SetPowerPlan(string guid) => Run("powercfg", "/s " + guid);

    /// <summary>Minutes (0 = never) of a power setting on mains power, e.g. ("SUB_VIDEO", "VIDEOIDLE").</summary>
    public static int PowerMinutes(string group, string setting)
    {
        var values = Regex.Matches(Run("powercfg", $"/q SCHEME_CURRENT {group} {setting}"), @":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline);
        // The last two hex values are the AC and DC settings (seconds).
        if (values.Count < 2) return -1;
        return Convert.ToInt32(values[values.Count - 2].Groups[1].Value, 16) / 60;
    }

    /// <summary>"monitor-timeout" or "standby-timeout", for mains and battery.</summary>
    public static void SetPowerMinutes(string what, int minutes)
    {
        Run("powercfg", $"/change {what}-ac {minutes}");
        Run("powercfg", $"/change {what}-dc {minutes}");
    }

    /// <summary>Screen saver delay in minutes, 0 = off.</summary>
    public static int ScreenSaverMinutes
    {
        get
        {
            int on = 0; SystemParametersInfo(0x0010, 0, ref on, 0);
            int seconds = 0; SystemParametersInfo(0x000E, 0, ref seconds, 0);
            return on != 0 ? seconds / 60 : 0;
        }
        set
        {
            if (value > 0) SystemParametersInfo(0x000F, (uint)(value * 60), IntPtr.Zero, SPIF_SAVE);
            SystemParametersInfo(0x0011, value > 0 ? 1u : 0u, IntPtr.Zero, SPIF_SAVE);
            if (value > 0 && string.IsNullOrEmpty(Get(@"Control Panel\Desktop", "SCRNSAVE.EXE", "")))
                Set(@"Control Panel\Desktop", "SCRNSAVE.EXE", @"C:\Windows\system32\Ribbons.scr", RegistryValueKind.String);
        }
    }

    // ---------------- Radios (Wi-Fi, Bluetooth) ----------------

    // Windows.Devices.Radios is a WinRT API; Windows PowerShell can call it without MacDock taking
    // on the whole Windows SDK projection.
    private const string RadioScript =
        "Add-Type -AssemblyName System.Runtime.WindowsRuntime;" +
        "$as=([System.WindowsRuntimeSystemExtensions].GetMethods()|?{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'})[0];" +
        "function Aw($o,$t){$k=$as.MakeGenericMethod($t).Invoke($null,@($o));$k.Wait(-1)|Out-Null;$k.Result};" +
        "[Windows.Devices.Radios.Radio,Windows.System.Devices,ContentType=WindowsRuntime]|Out-Null;" +
        "[Windows.Devices.Radios.RadioAccessStatus,Windows.System.Devices,ContentType=WindowsRuntime]|Out-Null;" +
        "[Windows.Devices.Radios.RadioState,Windows.System.Devices,ContentType=WindowsRuntime]|Out-Null;" +
        "Aw ([Windows.Devices.Radios.Radio]::RequestAccessAsync()) ([Windows.Devices.Radios.RadioAccessStatus])|Out-Null;" +
        "$rs=Aw ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]]);";

    /// <summary>State of each radio, e.g. {"WiFi": true, "Bluetooth": false}. Slow (about a second): call off the UI thread.</summary>
    public static Dictionary<string, bool> Radios()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string output = Run("powershell.exe", "-NoProfile -NonInteractive -Command \"" + RadioScript + "$rs|%{$_.Kind.ToString()+'='+$_.State.ToString()}\"");
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=');
            if (parts.Length == 2) result[parts[0]] = parts[1] == "On";
        }
        return result;
    }

    /// <summary>Turns a radio ("WiFi" or "Bluetooth") on or off. Slow: call off the UI thread.</summary>
    public static void SetRadio(string kind, bool on) =>
        Run("powershell.exe", "-NoProfile -NonInteractive -Command \"" + RadioScript +
            $"$rs|?{{$_.Kind -eq '{kind}'}}|%{{Aw ($_.SetStateAsync('{(on ? "On" : "Off")}')) ([Windows.Devices.Radios.RadioAccessStatus])|Out-Null}}\"");

    // ---------------- Wi-Fi profiles ----------------

    /// <summary>Saved Wi-Fi networks.</summary>
    public static List<string> KnownWifi()
    {
        var list = new List<string>();
        bool listing = false;
        foreach (var raw in Run("netsh", "wlan show profiles").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("-----")) { listing = true; continue; }
            if (!listing) continue;
            int colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon > 0 && line.StartsWith("    ")) list.Add(line[(colon + 3)..].Trim());
        }
        return list.Where(n => n.Length > 0).Distinct().ToList();
    }

    public static void ForgetWifi(string name) => Run("netsh", $"wlan delete profile name=\"{name.Replace("\"", "")}\"");

    /// <summary>Joins a WPA2 network with a password (saves it, like macOS remembers networks).</summary>
    public static bool JoinWifi(string ssid, string password)
    {
        try
        {
            string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
            string esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
            string xml = $"""
                <?xml version="1.0"?>
                <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                  <name>{esc(ssid)}</name>
                  <SSIDConfig><SSID><hex>{hex}</hex><name>{esc(ssid)}</name></SSID></SSIDConfig>
                  <connectionType>ESS</connectionType>
                  <connectionMode>auto</connectionMode>
                  <MSM><security>
                    <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
                    <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{esc(password)}</keyMaterial></sharedKey>
                  </security></MSM>
                </WLANProfile>
                """;
            string file = Path.Combine(Path.GetTempPath(), "macdock-wifi.xml");
            File.WriteAllText(file, xml);
            Run("netsh", $"wlan add profile filename=\"{file}\" user=current");
            File.Delete(file);
            string result = Run("netsh", $"wlan connect name=\"{ssid.Replace("\"", "")}\"");
            return result.Length > 0;
        }
        catch { return false; }
    }

    // ---------------- Login items ----------------

    public sealed record LoginItem(string Name, string Command, bool Enabled, bool FromFolder);

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<LoginItem> LoginItems()
    {
        var list = new List<LoginItem>();
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            using var approved = Registry.CurrentUser.OpenSubKey(Approved + "Run");
            foreach (var name in run?.GetValueNames() ?? Array.Empty<string>())
            {
                if (name.Length == 0) continue;
                bool on = approved?.GetValue(name) is not byte[] b || b.Length == 0 || b[0] % 2 == 0;
                list.Add(new LoginItem(name, run!.GetValue(name)?.ToString() ?? "", on, false));
            }
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            using var approvedFolder = Registry.CurrentUser.OpenSubKey(Approved + "StartupFolder");
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                {
                    string name = Path.GetFileName(file);
                    bool on = approvedFolder?.GetValue(name) is not byte[] b || b.Length == 0 || b[0] % 2 == 0;
                    list.Add(new LoginItem(name, file, on, true));
                }
        }
        catch { }
        return list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Allows or stops an item at login the way Task Manager does (the item itself stays).</summary>
    public static void SetLoginItem(LoginItem item, bool on)
    {
        var data = new byte[12];
        data[0] = (byte)(on ? 2 : 3);
        if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        Set(Approved + (item.FromFolder ? "StartupFolder" : "Run"), item.Name, data, RegistryValueKind.Binary);
    }

    public static void AddLoginItem(string exe) =>
        Set(RunKey, Path.GetFileNameWithoutExtension(exe), "\"" + exe + "\"", RegistryValueKind.String);

    // ---------------- Date & time ----------------

    public static bool Clock24
    {
        get => Get(@"Control Panel\International", "sShortTime", "h:mm tt").Contains('H');
        set
        {
            Set(@"Control Panel\International", "sShortTime", value ? "HH:mm" : "h:mm tt", RegistryValueKind.String);
            Set(@"Control Panel\International", "sTimeFormat", value ? "HH:mm:ss" : "h:mm:ss tt", RegistryValueKind.String);
            System.Globalization.CultureInfo.CurrentCulture.ClearCachedData();
            Broadcast("intl");
        }
    }

    public static void SetTimeZone(string id) => Run("tzutil", $"/s \"{id}\"");

    // ---------------- About this PC ----------------

    public static string WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string name = k?.GetValue("ProductName")?.ToString() ?? "Windows";
            string build = k?.GetValue("CurrentBuild")?.ToString() ?? "";
            if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            string display = k?.GetValue("DisplayVersion")?.ToString() ?? k?.GetValue("ReleaseId")?.ToString() ?? "";
            string ubr = k?.GetValue("UBR")?.ToString() ?? "";
            return $"{name} {display} (build {build}{(ubr.Length > 0 ? "." + ubr : "")})".Replace("  ", " ");
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    public static string Processor()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return Regex.Replace(k?.GetValue("ProcessorNameString")?.ToString() ?? "", @"\s+", " ").Trim() + $" ({Environment.ProcessorCount} threads)";
        }
        catch { return Environment.ProcessorCount + " cores"; }
    }

    public static string Graphics()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            var names = new List<string>();
            foreach (var sub in k?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!int.TryParse(sub, out _)) continue;
                try
                {
                    using var dev = k!.OpenSubKey(sub);
                    if (dev?.GetValue("DriverDesc") is string d && !d.Contains("Basic", StringComparison.OrdinalIgnoreCase) && !names.Contains(d)) names.Add(d);
                }
                catch { /* protected key */ }
            }
            return names.Count > 0 ? string.Join(", ", names) : "Unknown";
        }
        catch { return "Unknown"; }
    }

    public static string Model()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string maker = k?.GetValue("SystemManufacturer")?.ToString() ?? "";
            string model = k?.GetValue("SystemProductName")?.ToString() ?? "";
            string text = (maker + " " + model).Trim();
            return text.Length == 0 || text.Contains("To be filled", StringComparison.OrdinalIgnoreCase) ? "PC" : text;
        }
        catch { return "PC"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    public static string Memory()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m) ? $"{Math.Round(m.TotalPhys / 1073741824.0)} GB" : "Unknown";
    }

    public static bool IsAdmin()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return id.Groups?.Any(g => g.Value == "S-1-5-32-544") == true;
        }
        catch { return false; }
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);

    public static string FullUserName()
    {
        var sb = new StringBuilder(256);
        uint size = (uint)sb.Capacity;
        try { if (GetUserNameEx(3 /* NameDisplay */, sb, ref size) && sb.Length > 0) return sb.ToString(); } catch { }
        return Environment.UserName;
    }

    /// <summary>A Microsoft account signs in with an e-mail address.</summary>
    public static bool IsMicrosoftAccount()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\IdentityCRL\UserExtendedProperties");
            return k?.GetSubKeyNames().Any(n => n.Contains('@')) == true;
        }
        catch { return false; }
    }

    // ---------------- Printers ----------------

    [DllImport("winspool.drv", CharSet = CharSet.Unicode)] private static extern bool SetDefaultPrinter(string name);

    public static List<string> Printers()
    {
        var list = new List<string>();
        try { foreach (string p in System.Drawing.Printing.PrinterSettings.InstalledPrinters) list.Add(p); } catch { }
        return list;
    }

    public static string? DefaultPrinter()
    {
        try { return new System.Drawing.Printing.PrinterSettings().PrinterName; } catch { return null; }
    }

    public static void MakeDefaultPrinter(string name) => SetDefaultPrinter(name);

    // ---------------- Bluetooth devices ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BtSearch { public int Size, Authenticated, Remembered, Unknown, Connected, Inquiry; public byte Timeout; public IntPtr Radio; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BtDevice
    {
        public int Size;
        public ulong Address;
        public uint Class;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public short Year, Month, DayOfWeek, Day, Hour, Minute, Second, Ms; }
    [StructLayout(LayoutKind.Sequential)] private struct BtRadioSearch { public int Size; }

    [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref BtSearch search, ref BtDevice device);
    [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern bool BluetoothFindNextDevice(IntPtr find, ref BtDevice device);
    [DllImport("BluetoothApis.dll")] private static extern bool BluetoothFindDeviceClose(IntPtr find);
    [DllImport("BluetoothApis.dll", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref BtRadioSearch search, out IntPtr radio);
    [DllImport("BluetoothApis.dll")] private static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("BluetoothApis.dll")] private static extern int BluetoothRemoveDevice(ref ulong address);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public sealed record BluetoothDevice(ulong Address, string Name, bool Connected, uint Class);

    public static bool HasBluetooth()
    {
        try
        {
            var search = new BtRadioSearch { Size = 4 };
            IntPtr find = BluetoothFindFirstRadio(ref search, out var radio);
            if (find == IntPtr.Zero) return false;
            CloseHandle(radio);
            BluetoothFindRadioClose(find);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Paired devices ("My Devices" on a Mac).</summary>
    public static List<BluetoothDevice> BluetoothDevices()
    {
        var list = new List<BluetoothDevice>();
        try
        {
            var search = new BtSearch { Size = Marshal.SizeOf<BtSearch>(), Authenticated = 1, Remembered = 1, Connected = 1 };
            var device = new BtDevice { Size = Marshal.SizeOf<BtDevice>() };
            IntPtr find = BluetoothFindFirstDevice(ref search, ref device);
            if (find == IntPtr.Zero) return list;
            do
            {
                if (!list.Any(d => d.Address == device.Address))
                    list.Add(new BluetoothDevice(device.Address, string.IsNullOrWhiteSpace(device.Name) ? "Unknown device" : device.Name, device.Connected != 0, device.Class));
                device = new BtDevice { Size = Marshal.SizeOf<BtDevice>() };
            } while (BluetoothFindNextDevice(find, ref device));
            BluetoothFindDeviceClose(find);
        }
        catch { }
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    public static void ForgetBluetooth(ulong address) { try { BluetoothRemoveDevice(ref address); } catch { } }

    // ---------------- Displays ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string String;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, Width, Height, DisplayFlags, Frequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, int index, ref DisplayDevice info, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref DevMode mode, IntPtr hwnd, int flags, IntPtr param);

    public sealed record Display(string Device, string Name, bool Primary, DevMode Current, List<DevMode> Modes);

    public static List<Display> Displays()
    {
        var list = new List<Display>();
        for (int i = 0; i < 16; i++)
        {
            var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.Flags & 0x1 /* ATTACHED_TO_DESKTOP */) == 0) continue;
            var monitor = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            string name = EnumDisplayDevices(adapter.Name, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.String) ? monitor.String : adapter.String;
            var current = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(adapter.Name, -1 /* ENUM_CURRENT_SETTINGS */, ref current)) continue;
            var modes = new List<DevMode>();
            for (int m = 0; m < 1000; m++)
            {
                var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
                if (!EnumDisplaySettings(adapter.Name, m, ref mode)) break;
                if (mode.BitsPerPel != 32 || mode.Width < 800) continue;
                if (!modes.Any(x => x.Width == mode.Width && x.Height == mode.Height && x.Frequency == mode.Frequency)) modes.Add(mode);
            }
            list.Add(new Display(adapter.Name, name, (adapter.Flags & 0x4 /* PRIMARY */) != 0, current,
                modes.OrderByDescending(x => x.Width * x.Height).ThenByDescending(x => x.Frequency).ToList()));
        }
        return list;
    }

    /// <summary>Switches a screen to another resolution / refresh rate. True when Windows accepted it.</summary>
    public static bool SetDisplayMode(string device, DevMode mode)
    {
        mode.Fields = 0x80000 | 0x100000 | 0x400000; // width, height, frequency
        return ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, 0x1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero) == 0;
    }
}

/// <summary>Playback and recording devices (Core Audio), and the default one of each.</summary>
internal static class AudioDevices
{
    public sealed record Device(string Id, string Name, bool IsDefault);

    public static List<Device> List(bool output)
    {
        var list = new List<Device>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)AudioVolume.CreateDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(output ? 0 : 1, 1, out var def) == 0 && def.GetId(out var id) == 0) defaultId = id;
            if (enumerator.EnumAudioEndpoints(output ? 0 : 1, 1 /* ACTIVE */, out var collection) != 0) return list;
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0 || device.GetId(out var deviceId) != 0) continue;
                list.Add(new Device(deviceId, FriendlyName(device) ?? "Audio device", deviceId == defaultId));
            }
        }
        catch { }
        return list;
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) != 0) return null;
        var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>Makes a device the default for every role (like picking it in macOS's Sound settings).</summary>
    public static void SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))!)!;
            for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
        }
        catch (Exception ex) { CrashLog.Write("AudioDevices.SetDefault", ex); }
    }

    /// <summary>Input level 0..1 of the default microphone, or null without one.</summary>
    public static float? InputLevel
    {
        get
        {
            var e = InputEndpoint();
            return e != null && e.GetMasterVolumeLevelScalar(out float v) == 0 ? v : null;
        }
        set
        {
            if (value is float v) InputEndpoint()?.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), IntPtr.Zero);
        }
    }

    private static IAudioEndpointVolume? InputEndpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)AudioVolume.CreateDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(1 /* eCapture */, 1, out var device) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            return device.Activate(ref iid, 23, IntPtr.Zero, out var obj) == 0 ? (IAudioEndpointVolume)obj : null;
        }
        catch { return null; }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public int Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
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
    }

    // Undocumented but stable since Windows 7: the interface Windows' own Sound settings use.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility();
    }
}
