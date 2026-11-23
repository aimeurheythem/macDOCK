using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using MacDock.Native;
using MacDock.Services;

namespace MacDock.Finder;

/// <summary>
/// Folders open in Finder, not File Explorer: double-clicking a folder on the desktop, Win+E, "Show in
/// folder" in a browser, "Open file location"... anything that opens a File Explorer window.
///
/// How: when File Explorer creates a folder window it is made fully transparent at once (so it never
/// shows), its location and selection are read through the shell's window list, and then it is closed
/// and a Finder window opens there instead. Places Finder can't show (Control Panel, Recycle Bin,
/// network...) get their File Explorer window back, made visible again. A .zip opened this way is
/// extracted next to itself, like Archive Utility on macOS. No registry change: if MacDock isn't
/// running, File Explorer simply works as usual.
/// </summary>
internal sealed class ExplorerRedirect : IDisposable
{
    private const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_SHOW = 0x8002;
    private const int LWA_ALPHA = 0x2;
    private const long WS_EX_LAYERED = 0x00080000;
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    /// <summary>Explorer windows opened on purpose (Finder's "Show in File Explorer") are left alone until then.</summary>
    public static DateTime BypassUntil { get; set; }

    private readonly Action<string, IReadOnlyList<string>> _openFolder;
    private readonly Action<string> _openZip;
    private readonly Action _openDefault;
    private readonly Dispatcher _ui;
    private readonly Thread _thread;
    private Dispatcher? _worker;
    private IntPtr _hook;
    private Win32.WinEventProc? _proc;
    private readonly HashSet<IntPtr> _handled = new();

    /// <param name="openFolder">Opens Finder at a folder, selecting the given items (UI thread).</param>
    /// <param name="openZip">Extracts a .zip next to itself (UI thread).</param>
    /// <param name="openDefault">A new Finder window at its usual place (This PC, Quick Access, Home).</param>
    public ExplorerRedirect(Dispatcher ui, Action<string, IReadOnlyList<string>> openFolder, Action<string> openZip, Action openDefault)
    {
        _ui = ui;
        _openFolder = openFolder;
        _openZip = openZip;
        _openDefault = openDefault;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _worker = Dispatcher.CurrentDispatcher;
            try
            {
                _proc = OnEvent;
                _hook = Win32.SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0,
                    Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            }
            catch (Exception ex)
            {
                CrashLog.Write("ExplorerRedirect", ex);
            }
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "MacDock Explorer redirect" };
        _thread.SetApartmentState(ApartmentState.STA); // the shell window list is a COM object
        _thread.Start();
        ready.Wait(3000);
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != Win32.OBJID_WINDOW || idChild != 0) return;
        if (DateTime.UtcNow < BypassUntil) return;
        if (_handled.Contains(hwnd)) return;
        if (ClassOf(hwnd) != "CabinetWClass") return;
        if (Win32.GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;
        _handled.Add(hwnd);
        if (_handled.Count > 500) _handled.Clear();

        // Invisible right away: it must never flash on screen.
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        bool wasLayered = (ex & WS_EX_LAYERED) != 0;
        if (!wasLayered) Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        Resolve(hwnd, wasLayered, DateTime.UtcNow);
    }

    /// <summary>Waits (without blocking the thread) until File Explorer knows where the window is, then decides.</summary>
    private void Resolve(IntPtr hwnd, bool wasLayered, DateTime started)
    {
        try
        {
            if (!Win32.IsWindow(hwnd)) return;
            var (path, selection) = Locate(hwnd);
            if (path == null)
            {
                if (DateTime.UtcNow - started < TimeSpan.FromSeconds(2.5))
                {
                    Retry(() => Resolve(hwnd, wasLayered, started), 40);
                    return;
                }
                Reveal(hwnd, wasLayered); // never found out: give it back
                return;
            }

            if (Directory.Exists(path) || IsDefaultPlace(path))
            {
                // "Show in folder" selects the file a moment after the folder opens: give it that moment.
                if (selection.Count == 0 && Directory.Exists(path) && DateTime.UtcNow - started < TimeSpan.FromMilliseconds(700))
                {
                    Retry(() => Finish(hwnd, path), 160);
                    return;
                }
                Finish(hwnd, path, selection);
                return;
            }
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Win32.PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _ui.BeginInvoke(() => _openZip(path));
                return;
            }
            Reveal(hwnd, wasLayered); // Control Panel, Recycle Bin, network, phones...
        }
        catch (Exception ex)
        {
            CrashLog.Write("ExplorerRedirect.Resolve", ex);
            Reveal(hwnd, wasLayered);
        }
    }

    private void Finish(IntPtr hwnd, string path, IReadOnlyList<string>? selection = null)
    {
        if (!Win32.IsWindow(hwnd)) return;
        selection ??= Locate(hwnd).Selection;
        Win32.PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (IsDefaultPlace(path)) _ui.BeginInvoke(_openDefault);
        else _ui.BeginInvoke(() => _openFolder(path, selection));
    }

    private void Retry(Action action, int ms)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _worker!) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private static void Reveal(IntPtr hwnd, bool wasLayered)
    {
        if (!Win32.IsWindow(hwnd)) return;
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        if (!wasLayered)
        {
            long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_LAYERED));
        }
    }

    /// <summary>This PC, Quick Access and Home: Finder opens its usual window.</summary>
    private static bool IsDefaultPlace(string path) =>
        path.StartsWith("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase) || // This PC
        path.StartsWith("::{679f85cb-0220-4080-b29b-5540cc05aab6}", StringComparison.OrdinalIgnoreCase) || // Quick Access
        path.StartsWith("::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", StringComparison.OrdinalIgnoreCase);   // Home (Windows 11)

    private static readonly Type? ShellType = Type.GetTypeFromProgID("Shell.Application");

    /// <summary>The folder a File Explorer window shows, and its selected items (empty while it's still opening).</summary>
    private static (string? Path, IReadOnlyList<string> Selection) Locate(IntPtr hwnd)
    {
        if (ShellType == null) return (null, Array.Empty<string>());
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(ShellType);
            dynamic windows = ((dynamic)shell!).Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? window = null;
                try
                {
                    window = windows.Item(i);
                    if (window == null || new IntPtr((long)window.HWND) != hwnd) continue;
                    dynamic document = window.Document;
                    if (document == null) return (null, Array.Empty<string>());
                    string? path = document.Folder?.Self?.Path as string;
                    if (string.IsNullOrEmpty(path)) return (null, Array.Empty<string>());
                    var selection = new List<string>();
                    try
                    {
                        dynamic items = document.SelectedItems();
                        int n = items.Count;
                        for (int k = 0; k < n && k < 200; k++)
                            if (items.Item(k)?.Path is string p && p.Length > 0) selection.Add(p);
                    }
                    catch
                    {
                        // No selection available.
                    }
                    return (path, selection);
                }
                catch
                {
                    // That window went away or is still loading.
                }
                finally
                {
                    if (window != null) try { Marshal.FinalReleaseComObject(window); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("ExplorerRedirect.Locate", ex);
        }
        finally
        {
            if (shell != null) try { Marshal.FinalReleaseComObject(shell); } catch { }
        }
        return (null, Array.Empty<string>());
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        var worker = _worker;
        if (worker == null) return;
        worker.BeginInvoke(() =>
        {
            if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            worker.InvokeShutdown();
        });
    }
}
