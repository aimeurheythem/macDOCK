using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MacDock.Models;

namespace MacDock.Controls;

/// <summary>Visual for one dock item: the icon plus the "running" dot under it.</summary>
public sealed class DockIconView : StackPanel
{
    private const double DotSize = 4;
    private const double DotTopMargin = 3;

    /// <summary>Height of the dot row under the icon.</summary>
    public const double DotRowHeight = DotSize + DotTopMargin;

    private readonly Image _image;
    private readonly Ellipse _dot;

    // Full-resolution icon (used while magnified) and a crisp copy pre-scaled to the exact
    // resting size in device pixels (used at rest: 1 image pixel = 1 screen pixel, no blur).
    private ImageSource? _fullSource;
    private BitmapSource? _restSource;
    private int _restPx = -1;

    public DockItem Item { get; }

    public DockIconView(DockItem item, double iconSize)
    {
        Item = item;
        Orientation = Orientation.Vertical;
        Cursor = Cursors.Hand;

        _fullSource = item.Icon;
        _image = new Image { Source = item.Icon };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _dot = new Ellipse
        {
            Width = DotSize,
            Height = DotSize,
            Margin = new Thickness(0, DotTopMargin, 0, 0),
            Fill = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
            StrokeThickness = 0.5,
            Visibility = Visibility.Hidden, // Hidden (not Collapsed) keeps the row height constant
        };

        Children.Add(_image);
        Children.Add(_dot);
        IconSize = iconSize;
    }

    public void SetIcon(ImageSource? icon)
    {
        _fullSource = icon;
        _restSource = null;
        _restPx = -1;
        _image.Source = icon;
    }

    /// <summary>
    /// Sets the displayed size. At the resting size, bitmap icons use a high-quality pre-scaled copy
    /// that maps 1:1 to screen pixels; while magnified, the full-resolution source is scaled live.
    /// </summary>
    public void UpdateDisplay(double size, double restSize, DpiScale dpi)
    {
        IconSize = size;
        bool atRest = Math.Abs(size - restSize) < 0.01;
        if (atRest && _fullSource is BitmapSource bitmap)
        {
            int px = (int)Math.Round(restSize * dpi.DpiScaleX);
            if (_restSource == null || _restPx != px)
            {
                _restSource = Prescale(bitmap, restSize, px, dpi);
                _restPx = px;
            }
            _image.Source = _restSource;
            // 1 image pixel = 1 screen pixel: never resample it (a sub-pixel offset would blur it).
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        }
        else if (!ReferenceEquals(_image.Source, _fullSource))
        {
            _image.Source = _fullSource;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        }
    }

    /// <summary>Exact-pixel copy for the resting size, shrunk with area averaging (crisp, no jagged edges).</summary>
    private static BitmapSource Prescale(BitmapSource source, double sizeDip, int px, DpiScale dpi) =>
        Services.IconService.Resize(source, px, 96 * dpi.DpiScaleX);

    /// <summary>Current (possibly magnified) icon size in DIPs.</summary>
    public double IconSize
    {
        get => _image.Width;
        set { _image.Width = value; _image.Height = value; }
    }

    public bool IsRunning
    {
        get => _dot.Visibility == Visibility.Visible;
        set => _dot.Visibility = value ? Visibility.Visible : Visibility.Hidden;
    }

    // ---------------- Launch bounce ----------------

    private static readonly Duration BounceHalf = TimeSpan.FromMilliseconds(320);
    private readonly TranslateTransform _bounceTransform = new();
    private bool _bounceRequested;
    private bool _bounceRunning;

    /// <summary>True while a launch bounce is requested (the current hop still finishes after <see cref="StopBouncing"/>).</summary>
    public bool IsBouncing => _bounceRequested;

    /// <summary>Starts hopping the icon (not the dot) up and down until <see cref="StopBouncing"/>.</summary>
    public void StartBouncing(double height)
    {
        _bounceRequested = true;
        if (_bounceRunning) return;
        _image.RenderTransform = _bounceTransform;
        RunBounceCycle(height);
    }

    /// <summary>Stops after the current hop lands, so the icon never snaps mid-air.</summary>
    public void StopBouncing() => _bounceRequested = false;

    private void RunBounceCycle(double height)
    {
        _bounceRunning = true;
        var hop = new DoubleAnimationUsingKeyFrames { Duration = BounceHalf.TimeSpan * 2 };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-height, KeyTime.FromTimeSpan(BounceHalf.TimeSpan),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(BounceHalf.TimeSpan * 2),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        hop.Completed += (_, _) =>
        {
            if (_bounceRequested)
            {
                RunBounceCycle(height);
                return;
            }
            _bounceRunning = false;
            _bounceTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _bounceTransform.Y = 0;
        };
        _bounceTransform.BeginAnimation(TranslateTransform.YProperty, hop);
    }
}
