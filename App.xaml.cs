using System.Diagnostics;
using System.Threading;
using System.Windows;
using MacDock.Services;

namespace MacDock;

/// <summary>
/// Startup modes:
///   (none)              normal dock (single instance)
///   --restore           show the taskbar and restore work areas, then exit (manual recovery)
///   --watchdog &lt;pid&gt;  internal: waits for the dock process to die and restores the taskbar if it crashed
/// </summary>
public partial class App : Application
{
    /// <summary>Quits with MacDock's windows fading away (dock, menu bar, open windows) instead of vanishing at once.</summary>
    public static void QuitSmoothly()
    {
        var app = Current;
        if (app == null) return;
        var windows = app.Windows.OfType<Window>().Where(w => w.IsVisible && w.AllowsTransparency).ToList();
        if (windows.Count == 0) { app.Shutdown(); return; }
        int left = windows.Count;
        foreach (var w in windows)
            MacMotion.FadeOut(w, 220, () => { if (--left == 0) app.Shutdown(); });
        // Safety net: never hang on the way out.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) => { timer.Stop(); app.Shutdown(); };
        timer.Start();
    }
    static App() => MacFonts.ApplyAppWide(); // before any window or text exists

    private const string MutexName = @"Local\MacDock.SingleInstance";
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (MacDock.Native.Glass.IsWindows11) // the system can only round glass by 8 px there
        {
            Resources["MenuRadius"] = new CornerRadius(8);
            Resources["MenuItemRadius"] = new CornerRadius(4);
        }

        // Installer modes: the same exe installs ("MacDock Setup.exe" / --install) and uninstalls.
        if (e.Args.Contains("--uninstall-run"))
        {
            Installer.RunUninstall(e.Args.Contains("--quiet"));
            Shutdown();
            return;
        }
        if (e.Args.Contains("--uninstall"))
        {
            Installer.BeginUninstall(e.Args.Contains("--quiet"));
            Shutdown();
            return;
        }
        if (Installer.IsSetupMode(e.Args))
        {
            var setup = new SetupWindow();
            MainWindow = setup;
            setup.Show();
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0] == "--restore")
        {
            TaskbarManager.RecoverFromStateFile(force: true);
            TopAppBar.RecoverFromStateFile();
            Shutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--watchdog" && int.TryParse(e.Args[1], out int pid))
        {
            RunWatchdog(pid);
            return;
        }

        _instanceMutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        RegisterSafetyHandlers();

        // A previous run that was killed may have left the taskbar hidden: put it back first.
        TaskbarManager.RecoverFromStateFile(force: false);
        TopAppBar.RecoverFromStateFile();
        StartWatchdog();

        CustomIcons.EnsureFolder();
        var dock = new MainWindow();
        MainWindow = dock;
        dock.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex != null) // only the dock itself changed the desktop (not setup / uninstall / restore)
        {
            TopAppBar.Remove();
            TaskbarManager.Restore();
            GenieEffect.RestoreSystemMinimizeAnimation();
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>Restore the taskbar on every way out we can catch.</summary>
    private void RegisterSafetyHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("DispatcherUnhandledException", args.Exception);
            TopAppBar.Remove();
            TaskbarManager.Restore();
            GenieEffect.RestoreSystemMinimizeAnimation();
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashLog.Write("UnhandledException", args.ExceptionObject);
            TopAppBar.Remove();
            TaskbarManager.Restore();
            GenieEffect.RestoreSystemMinimizeAnimation();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            TopAppBar.Remove();
            TaskbarManager.Restore();
            GenieEffect.RestoreSystemMinimizeAnimation();
        };
        SessionEnding += (_, _) =>
        {
            TopAppBar.Remove();
            TaskbarManager.Restore();
        };
    }

    /// <summary>
    /// Starts a tiny copy of this exe that restores the taskbar if the dock is killed hard
    /// (Task Manager, Ctrl+C in "dotnet run", crash) where no handler can run.
    /// </summary>
    private static void StartWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            var watchdog = Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            // It only waits; never let it compete with the dock for CPU.
            try { if (watchdog != null) watchdog.PriorityClass = ProcessPriorityClass.Idle; } catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Watchdog not started: {ex.Message}");
        }
    }

    private void RunWatchdog(int pid)
    {
        var thread = new Thread(() =>
        {
            try { Process.GetProcessById(pid).WaitForExit(); }
            catch { /* already gone */ }

            // Clean exits delete the state file, so this only acts after a crash/kill.
            TaskbarManager.RecoverFromStateFile(force: false);
            TopAppBar.RecoverFromStateFile();
            Dispatcher.BeginInvoke(() => Shutdown());
        }) { IsBackground = true, Name = "MacDock watchdog" };
        thread.Start();
    }
}
