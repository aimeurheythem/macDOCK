using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDock.Native;
using MacDock.Services;
using Microsoft.Win32;

namespace MacDock;

/// <summary>
/// Windows 11: the system blur behind a window can only be rounded by 8 px, which made the dock look
/// like two shapes (or forced small corners). There the dock draws its own frosted copy of the
/// wallpaper inside its glass, clipped to the real macOS radius, and the system blur window is unused.
/// (Behind the dock there is normally only wallpaper: maximized windows stop above it.)
/// </summary>
public partial class MainWindow
{
    private const double FrostStripHeight = 260; // DIPs at the bottom of the screen: covers the dock in every state

    private BitmapSource? _frostImage;
    private int _frostVersion;

    private void InitFrost()
    {
        if (!Glass.IsWindows11) return;
        _backdrop.Suppressed = true;
        _backdrop.SetVisible(false);
        RefreshFrost();
        SystemEvents.UserPreferenceChanged += OnFrostPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnFrostDisplayChanged;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnFrostPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnFrostDisplayChanged;
        };
    }

    private void OnFrostPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop) Dispatcher.BeginInvoke(RefreshFrost); // wallpaper changed
    }

    private void OnFrostDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshFrost);

    /// <summary>Re-blurs the wallpaper strip in the background, then shows it in the dock.</summary>
    private void RefreshFrost()
    {
        if (!Glass.IsWindows11 || !TryGetPrimaryMonitor(out var monitor)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int strip = (int)Math.Round(FrostStripHeight * scale);
        int version = ++_frostVersion;
        Task.Run(() => WallpaperFrost.CreateBottomStrip(monitor, strip)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (version != _frostVersion || t.Status != TaskStatus.RanToCompletion) return;
            _frostImage = t.Result;
            if (_frostImage == null)
            {
                // No wallpaper image (solid color desktop): fall back to the system blur.
                _backdrop.Suppressed = false;
                Dock.SetFrost(null, default);
                UpdateBackdrop();
                return;
            }
            _backdrop.Suppressed = true;
            _backdrop.SetVisible(false);
            PlaceFrost();
        }));
    }

    /// <summary>Keeps the frost fixed to the screen: the strip's place in the dock's own coordinates.</summary>
    private void PlaceFrost()
    {
        if (_frostImage == null || PresentationSource.FromVisual(Dock) == null || !TryGetPrimaryMonitor(out var m)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int stripPx = (int)Math.Round(FrostStripHeight * dpi.DpiScaleY);
        var topLeft = Dock.PointFromScreen(new Point(m.X, m.Y + m.Height - stripPx));
        Dock.SetFrost(_frostImage, new Rect(topLeft.X, topLeft.Y, m.Width / dpi.DpiScaleX, stripPx / dpi.DpiScaleY));
    }

    private static bool TryGetPrimaryMonitor(out Int32Rect monitor)
    {
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        bool ok = Win32.GetMonitorInfo(Win32.MonitorFromPoint(new Win32.POINT(), Win32.MONITOR_DEFAULTTOPRIMARY), ref info);
        var r = info.rcMonitor;
        monitor = ok ? new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : default;
        return ok;
    }
}
