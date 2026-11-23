using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Per-user install (no admin rights): the exe is copied to %LOCALAPPDATA%\Programs\MacDock, with a
/// Start menu shortcut, an optional desktop shortcut and an entry in "Installed apps" for uninstalling.
///
/// The same exe is the installer: named "MacDock Setup.exe" (or started with --install) it shows the
/// setup window instead of the dock. Uninstall runs from a temporary copy, so the installed exe can be deleted.
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MacDock";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MacDock");

    public static string InstalledExe => Path.Combine(InstallDir, "MacDock.exe");

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MacDock.lnk");

    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MacDock.lnk");

    public static bool IsSetupMode(string[] args) =>
        args.Contains("--install") ||
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static string Version =>
        typeof(Installer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    // ---------------- Install ----------------

    /// <summary>Installs (or updates) MacDock. Runs on a background thread; reports each step.</summary>
    public static void Install(bool startWithWindows, bool desktopShortcut, Action<string, double> progress)
    {
        progress("Closing MacDock if it's running…", 0.1);
        CloseRunningDock();

        progress("Copying MacDock…", 0.35);
        Directory.CreateDirectory(InstallDir);
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown program path.");
        CopyWithRetry(source, InstalledExe);

        progress("Creating shortcuts…", 0.7);
        CreateShortcut(StartMenuShortcut, InstalledExe);
        if (desktopShortcut) CreateShortcut(DesktopShortcut, InstalledExe);
        else TryDelete(DesktopShortcut);

        progress("Registering MacDock…", 0.85);
        RegisterUninstall();
        StartupService.SetEnabled(startWithWindows, InstalledExe);

        progress("Done", 1);
    }

    public static void LaunchInstalled()
    {
        try { Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir }); }
        catch (Exception ex) { Debug.WriteLine($"Launch failed: {ex.Message}"); }
    }

    /// <summary>Asks a running dock to quit (it restores the taskbar itself), then makes sure it's gone.</summary>
    private static void CloseRunningDock()
    {
        int self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("MacDock"))
        {
            using (process)
            {
                if (process.Id == self) continue;
                try
                {
                    // The dock's main window is titled "MacDock": closing it is a clean exit.
                    Win32.EnumWindows((hwnd, _) =>
                    {
                        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid == process.Id && WindowTitle(hwnd) == "MacDock")
                            Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        return true;
                    }, IntPtr.Zero);
                    if (!process.WaitForExit(6000)) process.Kill(); // its watchdog restores the taskbar
                }
                catch
                {
                    // Already gone.
                }
            }
        }
        Thread.Sleep(300); // let the watchdog copy exit too, so the file can be replaced
    }

    private static void CopyWithRetry(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250); // the old exe may still be closing
            }
        }
    }

    private static void CreateShortcut(string path, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "MacDock - a macOS-style dock and menu bar for Windows";
            link.Save();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Shortcut failed: {ex.Message}");
        }
    }

    private static void RegisterUninstall()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "MacDock");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "MacDock");
        key.SetValue("DisplayIcon", InstalledExe + ",0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    // ---------------- Uninstall ----------------

    /// <summary>Step 1 (from the installed exe): confirm, then continue from a temp copy so this exe can be deleted.</summary>
    public static void BeginUninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Remove MacDock from this PC?\n\nThe Windows taskbar comes back. Your MacDock settings are kept.",
                "Uninstall MacDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var temp = Path.Combine(Path.GetTempPath(), $"MacDock-uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(Environment.ProcessPath!, temp, overwrite: true);
        Process.Start(new ProcessStartInfo(temp, $"--uninstall-run {(quiet ? "--quiet" : "")}") { UseShellExecute = false });
    }

    /// <summary>Step 2 (from the temp copy): close the dock, restore the taskbar, remove everything.</summary>
    public static void RunUninstall(bool quiet)
    {
        CloseRunningDock();
        TaskbarManager.RecoverFromStateFile(force: true);
        TopAppBar.RecoverFromStateFile();
        GenieEffect.RestoreSystemMinimizeAnimation();

        StartupService.SetEnabled(false);
        try { Finder.DesktopFolderIcons.RestoreNow(); } catch { /* folders keep a harmless desktop.ini */ }
        EditorTitleBars.Restore();
        try { MacCursors.Restore(); } catch { /* Windows keeps working with MacDock's pointers until changed */ }
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
        for (int attempt = 0; attempt < 20 && Directory.Exists(InstallDir); attempt++)
        {
            try { Directory.Delete(InstallDir, recursive: true); }
            catch { Thread.Sleep(250); }
        }

        if (!quiet)
            MessageBox.Show("MacDock was removed.", "Uninstall MacDock", MessageBoxButton.OK, MessageBoxImage.Information);

        // Delete this temporary copy once it has exited.
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & del \"{Environment.ProcessPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // A leftover file in %TEMP% is harmless.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        int length = Win32.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var sb = new System.Text.StringBuilder(length + 1);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
