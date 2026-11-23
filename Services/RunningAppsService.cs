using System.IO;
using System.Windows.Threading;
using MacDock.Models;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// Tracks running apps (grouped by executable). Refreshes on window show/hide/destroy/cloak
/// events via SetWinEventHook (throttled), plus a slow safety poll.
/// </summary>
internal sealed class RunningAppsService : IDisposable
{
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Win32.WinEventProc _winEventProc; // keep alive: native code holds a pointer to it
    private readonly List<IntPtr> _hooks = new();
    private readonly DispatcherTimer _throttle;
    private readonly DispatcherTimer _poll;
    private HashSet<IntPtr> _knownWindows = new();
    private List<RunningApp> _apps = new();

    /// <summary>Raised on the UI thread when the set of running apps or their windows changes.</summary>
    public event Action? Changed;

    /// <summary>Running apps in order of first appearance.</summary>
    public IReadOnlyList<RunningApp> Apps => _apps;

    public RunningAppsService()
    {
        _throttle = new DispatcherTimer { Interval = ThrottleDelay };
        _throttle.Tick += (_, _) => { _throttle.Stop(); Refresh(); };

        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += (_, _) => { if (!GameMode.Active) Refresh(); };
        _poll.Start();

        _winEventProc = OnWinEvent;
        const uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
        AddHook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_HIDE, flags); // destroy, show, hide
        AddHook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE, flags);
        AddHook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED, flags);

        Refresh();
    }

    /// <summary>Windows of the app whose executable is <paramref name="exePath"/> (empty if not running).</summary>
    public IReadOnlyList<IntPtr> GetWindows(string? exePath)
    {
        if (exePath == null) return Array.Empty<IntPtr>();
        var normalized = PathHelper.Normalize(exePath);
        if (_apps.FirstOrDefault(a => PathHelper.Comparer.Equals(a.ExePath, normalized)) is { } exact) return exact.Windows;
        // Apps whose windows belong to a helper exe inside their own folder: Steam's window is
        // steamwebhelper.exe (Steam\bin\cef\...), not steam.exe. Clicking Steam in the dock shows that window.
        if (Path.GetDirectoryName(normalized) is { } folder && IsAppFolder(folder))
        {
            string prefix = folder.TrimEnd('\\') + "\\";
            var helper = _apps.Where(a => a.ExePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).SelectMany(a => a.Windows).ToList();
            if (helper.Count > 0) return helper;
        }
        return Array.Empty<IntPtr>();
    }

    /// <summary>A folder that belongs to one app (not Windows' own folders or a shared root like Program Files).</summary>
    private static bool IsAppFolder(string folder)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return false;
        string[] shared =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        string trimmed = folder.TrimEnd('\\');
        if (shared.Any(s => string.Equals(trimmed, s.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return false;
        return Path.GetPathRoot(trimmed)?.TrimEnd('\\') != trimmed; // not a drive root
    }

    public void Refresh()
    {
        var found = WindowEnumerator.GetTaskbarWindows();
        var byExe = new Dictionary<string, List<IntPtr>>(PathHelper.Comparer);
        var exeOrder = new List<string>();
        foreach (var (hwnd, exe) in found)
        {
            if (!byExe.TryGetValue(exe, out var list))
            {
                byExe[exe] = list = new List<IntPtr>();
                exeOrder.Add(exe);
            }
            list.Add(hwnd);
        }

        // Keep the previous order of apps and windows stable; append newcomers at the end.
        var next = new List<RunningApp>();
        foreach (var old in _apps)
        {
            if (!byExe.Remove(old.ExePath, out var current)) continue;
            var windows = old.Windows.Where(current.Contains).ToList();
            windows.AddRange(current.Where(w => !windows.Contains(w)));
            next.Add(new RunningApp { ExePath = old.ExePath, Windows = windows });
        }
        foreach (var exe in exeOrder)
        {
            if (byExe.TryGetValue(exe, out var windows))
                next.Add(new RunningApp { ExePath = exe, Windows = windows });
        }

        _knownWindows = found.Select(f => f.Hwnd).ToHashSet();
        if (IsSame(_apps, next)) return;
        _apps = next;
        Changed?.Invoke();
    }

    private static bool IsSame(List<RunningApp> a, List<RunningApp> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            PathHelper.Comparer.Equals(p.First.ExePath, p.Second.ExePath) &&
            p.First.Windows.SequenceEqual(p.Second.Windows));

    private void AddHook(uint min, uint max, uint flags)
    {
        var hook = Win32.SetWinEventHook(min, max, IntPtr.Zero, _winEventProc, 0, 0, flags);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        bool known = _knownWindows.Contains(hwnd);
        switch (eventType)
        {
            case Win32.EVENT_OBJECT_DESTROY:
                if (!known) return; // destroyed windows can't be inspected; only care about ours
                break;
            case Win32.EVENT_OBJECT_NAMECHANGE:
                if (known) return;  // titles aren't shown; only matters if an untitled window gets a title
                if (Win32.GetAncestor(hwnd, Win32.GA_ROOT) != hwnd) return;
                break;
            default:
                if (!known && Win32.GetAncestor(hwnd, Win32.GA_ROOT) != hwnd) return;
                break;
        }

        if (!_throttle.IsEnabled) _throttle.Start();
    }

    public void Dispose()
    {
        foreach (var hook in _hooks) Win32.UnhookWinEvent(hook);
        _hooks.Clear();
        _throttle.Stop();
        _poll.Stop();
    }
}
