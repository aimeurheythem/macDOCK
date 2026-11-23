using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MacDock.Native;

namespace MacDock;

/// <summary>
/// macOS-style auto-hide: the dock slides below the screen edge when the mouse leaves it,
/// and slides back up when the mouse touches the bottom edge of the primary screen.
/// </summary>
public partial class MainWindow
{
    private const double AutoHideSmoothing = 14;
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromMilliseconds(700);

    private readonly TranslateTransform _dockShift = new();
    private DispatcherTimer? _autoHideTimer;
    private double _hideOffset, _hideTarget;
    private bool _hideAnimating;
    private TimeSpan _hideLastFrame;
    private DateTime? _mouseLeftAt;
    private bool _menuOpen;
    private bool _dragging;
    private Win32.RECT _bgScreenRect;

    /// <summary>
    /// How far down the dock moves so its background is fully below the screen edge (DIPs).
    /// Works whether the dock sits on the screen edge (taskbar hidden) or above a visible taskbar.
    /// </summary>
    private double HiddenOffset
    {
        get
        {
            double backgroundTop = Top + ActualHeight - Dock.DockHeight;
            return Math.Max(0, SystemParameters.PrimaryScreenHeight - backgroundTop) + 2;
        }
    }

    private void InitAutoHide()
    {
        Dock.RenderTransform = _dockShift;
        DragEnter += (_, _) => _dragging = true;
        DragLeave += (_, _) => _dragging = false;
        Drop += (_, _) => _dragging = false;
    }

    private void SetAutoHide(bool enabled)
    {
        if (enabled)
        {
            if (_autoHideTimer == null)
            {
                _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                _autoHideTimer.Tick += (_, _) => AutoHideTick();
            }
            _mouseLeftAt = null;
            _autoHideTimer.Start();
        }
        else
        {
            _autoHideTimer?.Stop();
            AnimateDockOffset(0);
        }
    }

    private void AutoHideTick()
    {
        Win32.GetCursorPos(out var pt);
        int screenWidth = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
        int screenHeight = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
        bool atEdge = pt.Y >= screenHeight - 2 && pt.X >= 0 && pt.X < screenWidth;

        bool shown = _hideTarget == 0;
        if (!shown)
        {
            if (atEdge)
            {
                _mouseLeftAt = null;
                AnimateDockOffset(0);
            }
            return;
        }

        bool keepShown = atEdge || _menuOpen || _dragging || IsStartMenuOpen
                         || Dock.IsMouseOver || IsCursorNearDock(pt, screenHeight);
        if (keepShown)
        {
            _mouseLeftAt = null;
            return;
        }

        _mouseLeftAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _mouseLeftAt >= AutoHideDelay)
            AnimateDockOffset(HiddenOffset);
    }

    /// <summary>Cursor over the dock background or the area magnified icons can occupy above it.</summary>
    private bool IsCursorNearDock(Win32.POINT pt, int screenHeight)
    {
        var r = _bgScreenRect;
        if (r.Right <= r.Left) return false;
        double extra = (Dock.MaxSize - Dock.BaseSize + 8) * VisualTreeHelper.GetDpi(this).DpiScaleY;
        return pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top - extra && pt.Y <= screenHeight;
    }

    private void AnimateDockOffset(double target)
    {
        _hideTarget = target;
        if (_hideAnimating) return;
        _hideAnimating = true;
        _hideLastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnHideFrame;
    }

    private void OnHideFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _hideLastFrame) return;
        double dt = _hideLastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Min((now - _hideLastFrame).TotalSeconds, 1.0 / 30);
        _hideLastFrame = now;

        double diff = _hideTarget - _hideOffset;
        bool done = Math.Abs(diff) < 0.3;
        _hideOffset = done ? _hideTarget : _hideOffset + diff * (1 - Math.Exp(-AutoHideSmoothing * dt));
        _dockShift.Y = _hideOffset;
        UpdateBackdrop();

        if (!done) return;
        _hideAnimating = false;
        CompositionTarget.Rendering -= OnHideFrame;
    }
}
