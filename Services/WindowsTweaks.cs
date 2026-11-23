using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MacDock.Services;

/// <summary>
/// Optional tweaks that make Windows lighter, smoother and better for games, shown in System Settings
/// &gt; Gaming &amp; Performance. Each one is a switch that reads the real current state and can be turned
/// back (to Windows' default, or to what the PC had before). Per-user tweaks apply instantly; machine-wide
/// ones need administrator permission, and when several are applied together Windows asks only once.
/// </summary>
internal static class WindowsTweaks
{
    /// <summary>
    /// A tweak. <see cref="Set"/> applies a per-user tweak; administrator tweaks give a PowerShell
    /// <see cref="Script"/> instead (run elevated, alone or batched with others).
    /// </summary>
    public sealed record Tweak(string Id, string Title, string Description, Func<bool> IsOn,
        Action<bool>? Set = null, Func<bool, string>? Script = null, bool Recommended = true)
    {
        public bool NeedsAdmin => Script != null;

        /// <summary>Applies the tweak (asks for administrator permission when it needs it).</summary>
        public void Apply(bool on)
        {
            if (Script != null) RunElevated(Script(on));
            else Set?.Invoke(on);
        }
    }

    // ---------------- Helpers ----------------

    private static int Dword(RegistryKey hive, string key, string name, int fallback)
    {
        try
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    private static string? Text(RegistryKey hive, string key, string name)
    {
        try
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    private static void SetUser(string key, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue(name, value, kind);
        }
        catch (Exception ex) { CrashLog.Write("WindowsTweaks " + name, ex); }
    }

    private static void DeleteUser(string key, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch { }
    }

    /// <summary>PowerShell that sets (or, with a null value, removes) a machine-wide registry value.</summary>
    private static string Hklm(string key, string name, object? value, string type = "DWord")
    {
        string path = "HKLM:\\" + key;
        return value == null
            ? $"Remove-ItemProperty -Path '{path}' -Name '{name}' -ErrorAction SilentlyContinue;"
            : $"New-Item -Path '{path}' -Force -ErrorAction SilentlyContinue | Out-Null; Set-ItemProperty -Path '{path}' -Name '{name}' -Type {type} -Value {(value is string s ? "'" + s + "'" : value)};";
    }

    /// <summary>Runs a PowerShell script as administrator (Windows asks the user once). Waits for it.</summary>
    public static bool RunElevated(string script)
    {
        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='SilentlyContinue';" + script));
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(120000);
            return p?.ExitCode == 0;
        }
        catch { return false; } // the user said no
    }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint uParam, int[] pvParam, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    private static void Broadcast(string area) => Task.Run(() =>
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, area, 0x0002, 1000, out _));

    // ---------------- Gaming ----------------

    private const string Multimedia = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GamesTask = Multimedia + @"\Tasks\Games";

    public static readonly Tweak[] Gaming =
    {
        new("gamemode", "Game Mode", "Windows gives the game you are playing priority over background work.",
            () => Dword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1) != 0,
            on => { SetUser(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", on ? 1 : 0); SetUser(@"Software\Microsoft\GameBar", "AllowAutoGameMode", on ? 1 : 0); }),
        new("gamedvr", "Turn off background game recording", "Stops Xbox Game Bar from recording the last minutes of every game (costs frames).",
            () => Dword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 1) == 0,
            on =>
            {
                SetUser(@"System\GameConfigStore", "GameDVR_Enabled", on ? 0 : 1);
                SetUser(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", on ? 0 : 1);
            }),
        new("mouseaccel", "Raw mouse movement", "Turns off \"Enhance pointer precision\" so aiming feels the same at every speed.",
            () => { var m = new int[3]; SystemParametersInfo(0x0003, 0, m, 0); return m[2] == 0; },
            on => SystemParametersInfo(0x0004, 0, on ? new[] { 0, 0, 0 } : new[] { 6, 10, 1 }, 0x1 | 0x2)),
        new("gamepriority", "Games get the processor and graphics first", "Raises games' CPU, GPU and disk priority and stops Windows from slowing the network while media plays.",
            () => Text(Registry.LocalMachine, GamesTask, "Scheduling Category") == "High" && Dword(Registry.LocalMachine, Multimedia, "SystemResponsiveness", 20) <= 10,
            Script: on => on
                ? Hklm(Multimedia, "SystemResponsiveness", 10) + Hklm(Multimedia, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF)) +
                  Hklm(GamesTask, "GPU Priority", 8) + Hklm(GamesTask, "Priority", 6) + Hklm(GamesTask, "Scheduling Category", "High", "String") + Hklm(GamesTask, "SFIO Priority", "High", "String")
                : Hklm(Multimedia, "SystemResponsiveness", 20) + Hklm(Multimedia, "NetworkThrottlingIndex", 10) +
                  Hklm(GamesTask, "Priority", 2) + Hklm(GamesTask, "Scheduling Category", "Medium", "String") + Hklm(GamesTask, "SFIO Priority", "Normal", "String")),
        new("foreground", "The app in front comes first", "Windows gives the window you are using (your game) longer, more frequent turns on the processor.",
            () => Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 2) == 0x26,
            Script: on => Hklm(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", on ? 0x26 : 2)),
        new("gpusched", "Hardware-accelerated GPU scheduling", "The graphics card manages its own memory: lower latency in games. Takes effect after a restart.",
            () => Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1) == 2,
            Script: on => Hklm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", on ? 2 : 1), Recommended: false),
        new("powerthrottle", "No power throttling", "Windows stops slowing down background and minimized programs to save power (laptops: uses more battery).",
            () => Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 0) == 1,
            Script: on => Hklm(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", on ? 1 : null), Recommended: false),
    };

    // ---------------- Smoother, lighter Windows ----------------

    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private static readonly string[] AdValues =
    {
        "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled", "SubscribedContent-353694Enabled",
        "SubscribedContent-353696Enabled", "SubscribedContent-310093Enabled", "SystemPaneSuggestionsEnabled",
        "SilentInstalledAppsEnabled", "SoftLandingEnabled", "RotatingLockScreenOverlayEnabled", "PreInstalledAppsEnabled",
        "OemPreInstalledAppsEnabled",
    };

    public static readonly Tweak[] Smoothness =
    {
        new("menus", "Instant menus", "Menus open with no delay (Windows waits 0.4 s by default).",
            () => Text(Registry.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay") == "0",
            on => { SetUser(@"Control Panel\Desktop", "MenuShowDelay", on ? "0" : "400", RegistryValueKind.String); Broadcast("WindowMetrics"); }),
        new("hover", "Quicker hover", "Tooltips and hover effects react in 0.1 s instead of 0.4 s.",
            () => Text(Registry.CurrentUser, @"Control Panel\Mouse", "MouseHoverTime") == "100",
            on => { SetUser(@"Control Panel\Mouse", "MouseHoverTime", on ? "100" : "400", RegistryValueKind.String); Broadcast("Mouse"); }),
        new("startupdelay", "Start login apps immediately", "Removes the delay Windows adds before starting apps after you log in.",
            () => Dword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", -1) == 0,
            on =>
            {
                if (on) SetUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0);
                else DeleteUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec");
            }),
        new("backgroundapps", "Stop background apps", "Store apps can't run in the background (Mail, Weather, News...). Saves memory and battery.",
            () => Dword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0) == 1,
            on =>
            {
                SetUser(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", on ? 1 : 0);
                SetUser(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", on ? 0 : 1);
            }),
        new("ads", "No tips, ads or suggested apps", "No suggestions in Start and Settings, no apps installed silently, no lock screen tips.",
            () => Dword(Registry.CurrentUser, ContentDelivery, "SystemPaneSuggestionsEnabled", 1) == 0 && Dword(Registry.CurrentUser, ContentDelivery, "SilentInstalledAppsEnabled", 1) == 0,
            on => { foreach (var v in AdValues) SetUser(ContentDelivery, v, on ? 0 : 1); }),
        new("websearch", "No web results in search", "Searching from Start only shows what is on this PC (no Bing).",
            () => Dword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1) == 0,
            on =>
            {
                SetUser(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", on ? 0 : 1);
                SetUser(@"Software\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", on ? 0 : 1);
                if (on) SetUser(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1);
                else DeleteUser(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions");
            }),
        new("widgets", "No News, Widgets or weather on the taskbar", "Turns off News and Interests (Windows 10) and Widgets (Windows 11), which keep loading web content.",
            () => Dword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Feeds", "ShellFeedsTaskbarViewMode", 0) == 2 ||
                  (Environment.OSVersion.Version.Build >= 22000 && Dword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 1) == 0),
            on =>
            {
                SetUser(@"Software\Microsoft\Windows\CurrentVersion\Feeds", "ShellFeedsTaskbarViewMode", on ? 2 : 0);
                if (Environment.OSVersion.Version.Build >= 22000) SetUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", on ? 0 : 1);
                Broadcast("TraySettings");
            }),
        new("ai", "No Copilot or Recall", "Turns off Windows Copilot and Recall's screen snapshots (Windows 11).",
            () => Dword(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0) == 1,
            on =>
            {
                if (on)
                {
                    SetUser(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                    SetUser(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
                    SetUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0);
                }
                else
                {
                    DeleteUser(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot");
                    DeleteUser(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis");
                    SetUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 1);
                }
            }),
        new("onedrive", "OneDrive doesn't start with Windows", "OneDrive stops syncing in the background until you open it yourself.",
            () => OneDriveLoginItem() is { Enabled: false },
            on => { if (OneDriveLoginItem() is { } item) WindowsSettings.SetLoginItem(item, !on); }, Recommended: false),
        new("edge", "Edge stays closed in the background", "Microsoft Edge no longer pre-loads at login or keeps running after you close it.",
            () => Dword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 1) == 0,
            Script: on => Hklm(@"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", on ? 0 : null) + Hklm(@"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", on ? 0 : null)),
        new("delivery", "No sharing updates with other PCs", "Windows stops uploading updates to other computers over the internet (saves bandwidth).",
            () => Dword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1) == 0,
            Script: on => Hklm(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", on ? 0 : null)),
        new("telemetry", "Don't send diagnostic data", "Turns off Windows telemetry and its background services.",
            () => Dword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3) == 0,
            Script: on => on
                ? Hklm(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0) + ServiceScript(new[] { "DiagTrack", "dmwappushservice" }, disable: true)
                : Hklm(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", null) + ServiceScript(new[] { "DiagTrack", "dmwappushservice" }, disable: false)),
        new("services", "Turn off unneeded background services", "Superfetch, error reporting, maps, retail demo, fax, link tracking, Insider and parental-controls services.",
            () => ServiceStart("SysMain") == 4 && ServiceStart("WerSvc") == 4,
            Script: on => ServiceScript(UnneededServices, disable: on)),
        new("spooler", "Turn off printing", "Stops the Print Spooler service. Only if you never print or scan.",
            () => ServiceStart("Spooler") == 4,
            Script: on => ServiceScript(new[] { "Spooler" }, disable: on), Recommended: false),
        new("hibernate", "Turn off hibernation", "Frees disk space as large as your memory (also turns off Fast Startup).",
            () => Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 1) == 0,
            Script: on => on ? "powercfg -h off;" : "powercfg -h on;", Recommended: false),
    };

    // ---------------- Services ----------------

    private static readonly string[] UnneededServices =
        { "SysMain", "WerSvc", "MapsBroker", "RetailDemo", "Fax", "TrkWks", "wisvc", "WpcMonSvc", "PcaSvc" };

    /// <summary>Windows' own start type for each service, used to put them back.</summary>
    private static readonly Dictionary<string, int> DefaultStart = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SysMain"] = 2, ["WerSvc"] = 3, ["MapsBroker"] = 2, ["RetailDemo"] = 3, ["Fax"] = 3, ["TrkWks"] = 2,
        ["wisvc"] = 3, ["WpcMonSvc"] = 3, ["PcaSvc"] = 2, ["DiagTrack"] = 2, ["dmwappushservice"] = 3, ["Spooler"] = 2,
    };

    private static readonly string BackupFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "services-backup.json");

    /// <summary>A service's start type (2 automatic, 3 manual, 4 disabled), or -1 when it doesn't exist.</summary>
    private static int ServiceStart(string name) => Dword(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{name}", "Start", -1);

    /// <summary>
    /// Disables services (remembering how each was set first), or puts them back as they were (or as
    /// Windows ships them).
    /// </summary>
    private static string ServiceScript(IEnumerable<string> names, bool disable)
    {
        Dictionary<string, int> backup;
        try { backup = File.Exists(BackupFile) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(BackupFile)) ?? new() : new(); }
        catch { backup = new(); }
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            int current = ServiceStart(name);
            if (current < 0) continue; // not on this PC
            string key = $"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\{name}";
            if (disable)
            {
                if (current != 4 && !backup.ContainsKey(name)) backup[name] = current;
                sb.Append($"Stop-Service -Name '{name}' -Force; Set-ItemProperty -Path '{key}' -Name Start -Value 4;");
            }
            else
            {
                int start = backup.TryGetValue(name, out int saved) ? saved : DefaultStart.GetValueOrDefault(name, 3);
                backup.Remove(name);
                sb.Append($"Set-ItemProperty -Path '{key}' -Name Start -Value {start};");
                if (start == 2) sb.Append($"Start-Service -Name '{name}';");
            }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup));
        }
        catch { }
        return sb.ToString();
    }

    private static WindowsSettings.LoginItem? OneDriveLoginItem() =>
        WindowsSettings.LoginItems().FirstOrDefault(i => !i.FromFolder && i.Name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Turns on every recommended tweak that is off. The administrator ones go in one script, so Windows
    /// asks for permission only once. Returns how many were changed.
    /// </summary>
    public static int ApplyRecommended()
    {
        int changed = 0;
        var script = new StringBuilder();
        foreach (var tweak in Gaming.Concat(Smoothness).Where(t => t.Recommended))
        {
            if (tweak.IsOn()) continue;
            changed++;
            if (tweak.Script != null) script.Append(tweak.Script(true));
            else tweak.Set?.Invoke(true);
        }
        if (script.Length > 0) RunElevated(script.ToString());
        return changed;
    }

    // ---------------- Power ----------------

    private const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    /// <summary>Adds the "Ultimate Performance" power plan (hidden on most PCs) and makes it active.</summary>
    public static void AddUltimatePlan()
    {
        if (WindowsSettings.PowerPlans().FirstOrDefault(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            WindowsSettings.SetPowerPlan(existing.Guid);
            return;
        }
        var output = WindowsSettings.Run("powercfg", "-duplicatescheme " + UltimateTemplate);
        var m = System.Text.RegularExpressions.Regex.Match(output, "[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}");
        if (m.Success) WindowsSettings.SetPowerPlan(m.Value);
    }

    // ---------------- Clean-up ----------------

    /// <summary>Deletes temporary files more than a day old (yours and Windows'). Returns the bytes freed.</summary>
    public static long CleanTemporaryFiles()
    {
        long freed = 0;
        var cutoff = DateTime.Now.AddDays(-1);
        foreach (var dir in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") })
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTime > cutoff) continue;
                        long size = info.Length;
                        info.Delete();
                        freed += size;
                    }
                    catch { /* in use */ }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
            }
            catch { /* no access */ }
        }
        return freed;
    }

    // ---------------- Bloatware ----------------

    private const string OneDrivePackage = "OneDrive (app)";

    /// <summary>Preinstalled apps most people never use (package name, friendly name). Xbox apps and sign-in parts are never listed: games need them.</summary>
    public static readonly (string Package, string Name)[] Bloat =
    {
        ("Microsoft.BingNews", "Microsoft News"), ("Microsoft.News", "News"), ("Microsoft.BingWeather", "Weather"), ("Microsoft.BingFinance", "Money"),
        ("Microsoft.BingSports", "Sports"), ("Microsoft.BingTravel", "Travel"), ("Microsoft.BingFoodAndDrink", "Food & Drink"),
        ("Microsoft.BingHealthAndFitness", "Health & Fitness"), ("Microsoft.BingSearch", "Bing Search"), ("Microsoft.Copilot", "Copilot"),
        ("Microsoft.Windows.Ai.Copilot.Provider", "Copilot (system part)"), ("Microsoft.GetHelp", "Get Help"), ("Microsoft.Getstarted", "Tips"),
        ("Microsoft.Microsoft3DViewer", "3D Viewer"), ("Microsoft.MicrosoftOfficeHub", "Office (promo app)"), ("Microsoft.Office.Sway", "Sway"),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection"), ("Microsoft.MixedReality.Portal", "Mixed Reality Portal"),
        ("Microsoft.People", "People"), ("Microsoft.SkypeApp", "Skype"), ("Microsoft.WindowsFeedbackHub", "Feedback Hub"),
        ("Microsoft.WindowsMaps", "Maps"), ("Microsoft.ZuneMusic", "Groove Music / Media Player"), ("Microsoft.ZuneVideo", "Films & TV"),
        ("Microsoft.YourPhone", "Phone Link"), ("Microsoft.Wallet", "Wallet"), ("Microsoft.Messaging", "Messaging"),
        ("Microsoft.OneConnect", "Mobile Plans"), ("Microsoft.Print3D", "Print 3D"), ("Microsoft.MSPaint", "Paint 3D"),
        ("Microsoft.Office.OneNote", "OneNote for Windows 10"), ("Microsoft.549981C3F5F10", "Cortana"),
        ("Microsoft.WindowsCommunicationsApps", "Mail and Calendar"), ("Microsoft.OutlookForWindows", "New Outlook"),
        ("MicrosoftTeams", "Teams (personal)"), ("MSTeams", "Teams"), ("Microsoft.Windows.DevHome", "Dev Home"),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist"), ("Microsoft.MicrosoftJournal", "Journal"),
        ("MicrosoftCorporationII.MicrosoftFamily", "Family Safety"), ("Clipchamp.Clipchamp", "Clipchamp"),
        ("Microsoft.Todos", "Microsoft To Do"), ("Microsoft.PowerAutomateDesktop", "Power Automate"),
        ("king.com.CandyCrushSaga", "Candy Crush Saga"), ("king.com.CandyCrushSodaSaga", "Candy Crush Soda Saga"),
        ("king.com.CandyCrushFriends", "Candy Crush Friends"), ("king.com.FarmHeroesSaga", "Farm Heroes Saga"),
        ("SpotifyAB.SpotifyMusic", "Spotify (preinstalled)"), ("Facebook.Facebook", "Facebook"), ("Disney.37853FC22B2CE", "Disney+"),
        ("BytedancePte.Ltd.TikTok", "TikTok"), ("AmazonVideo.PrimeVideo", "Prime Video"),
        (OneDrivePackage, "OneDrive"),
    };

    private static string? OneDriveSetup()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var path in new[] { Path.Combine(windows, "System32", "OneDriveSetup.exe"), Path.Combine(windows, "SysWOW64", "OneDriveSetup.exe") })
            if (File.Exists(path)) return path;
        return null;
    }

    private static bool OneDriveInstalled() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe"));

    /// <summary>Which of <see cref="Bloat"/> are installed for this user. Slow (PowerShell): off the UI thread.</summary>
    public static List<(string Package, string Name)> InstalledBloat()
    {
        var installed = WindowsSettings.Run("powershell.exe", "-NoProfile -NonInteractive -Command \"Get-AppxPackage | ForEach-Object { $_.Name }\"", 30000)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (OneDriveInstalled() && OneDriveSetup() != null) installed.Add(OneDrivePackage);
        return Bloat.Where(b => installed.Contains(b.Package)).ToList();
    }

    /// <summary>Uninstalls the given apps for this user (Store apps can be installed again from the Microsoft Store).</summary>
    public static void Remove(IEnumerable<string> packages)
    {
        var list = packages.ToList();
        if (list.Remove(OneDrivePackage) && OneDriveSetup() is { } setup)
        {
            foreach (var p in Process.GetProcessesByName("OneDrive")) { try { p.Kill(); } catch { } p.Dispose(); }
            WindowsSettings.Run(setup, "/uninstall", 120000); // the files in the OneDrive folder stay on the PC
        }
        var names = string.Join(",", list.Select(p => "'" + p.Replace("'", "") + "'"));
        if (names.Length == 0) return;
        WindowsSettings.Run("powershell.exe", "-NoProfile -NonInteractive -Command \"" +
            $"foreach ($n in @({names})) {{ Get-AppxPackage -Name $n | Remove-AppxPackage -ErrorAction SilentlyContinue }}\"", 180000);
    }
}
