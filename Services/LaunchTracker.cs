using System.Windows.Threading;
using MacDock.Controls;

namespace MacDock.Services;

/// <summary>
/// Keeps a dock icon bouncing after a launch until the app shows a window, or a timeout passes.
/// </summary>
internal sealed class LaunchTracker : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Shortcuts whose target is unknown can't be matched to a window: just bounce briefly.</summary>
    private static readonly TimeSpan UnknownTargetDuration = TimeSpan.FromSeconds(1.5);

    private readonly Func<string?, bool> _hasWindow;
    private readonly Func<double> _bounceHeight;
    private readonly Dictionary<DockIconView, DateTime> _deadlines = new();
    private readonly DispatcherTimer _timer;

    /// <param name="hasWindow">Returns true when the executable at the given path has a visible window.</param>
    public LaunchTracker(Func<string?, bool> hasWindow, Func<double> bounceHeight)
    {
        _hasWindow = hasWindow;
        _bounceHeight = bounceHeight;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Check();
    }

    public bool IsLaunching(DockIconView view) => _deadlines.ContainsKey(view);

    public void Begin(DockIconView view)
    {
        var duration = view.Item.TargetPath == null ? UnknownTargetDuration : Timeout;
        _deadlines[view] = DateTime.UtcNow + duration;
        view.StartBouncing(_bounceHeight());
        _timer.Start();
    }

    /// <summary>Stops bouncing for apps whose window appeared or whose time ran out.</summary>
    public void Check()
    {
        var now = DateTime.UtcNow;
        foreach (var (view, deadline) in _deadlines.ToList())
        {
            bool done = now >= deadline ||
                        (view.Item.TargetPath != null && _hasWindow(view.Item.TargetPath));
            if (!done) continue;
            view.StopBouncing();
            _deadlines.Remove(view);
        }
        if (_deadlines.Count == 0) _timer.Stop();
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var view in _deadlines.Keys) view.StopBouncing();
        _deadlines.Clear();
    }
}
