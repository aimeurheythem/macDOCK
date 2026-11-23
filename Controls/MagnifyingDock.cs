using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MacDock.Controls;

/// <summary>
/// Lays out dock items with macOS-style magnification: icons near the mouse grow (upward),
/// the background widens symmetrically around the screen center, and a name label floats
/// above the hovered icon. Sizes are animated every frame with exponential smoothing.
/// </summary>
public sealed class MagnifyingDock : Canvas
{
    // ---- Tunables (phase 8 will make some of these configurable) ----
    public double BaseSize { get; set; } = 48;
    public double MaxSize { get; set; } = 96;
    public double Spacing { get; set; } = 4;          // horizontal gap on each side of an icon

    private const double PadTop = 6, PadBottom = 4, PadSide = 6;
    private const double SeparatorWidth = 1, SeparatorMargin = 6;
    private const double LabelSpace = 40;              // room above the biggest icon for the label
    private const double LabelGap = 8;                 // gap between label and icon top
    private const double RangeInSlots = 2.5;           // how far (in base slots) the zoom spreads each side
    private const double Smoothing = 20;               // higher = snappier animation

    private readonly LiquidGlassSurface _background;
    private readonly Rectangle _hitArea;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private readonly List<Slot> _slots = new();

    private double _mouseX;
    private bool _mouseInside;
    private bool _animating;
    private TimeSpan _lastFrame;
    private Rect _lastBackground = Rect.Empty;

    /// <summary>Raised when the background rectangle (canvas coordinates) moves or resizes.</summary>
    public event Action<Rect>? BackgroundBoundsChanged;

    /// <summary>
    /// Rounded like the macOS dock: radius grows with the dock height. (On Windows 11 the system blur
    /// can't follow this curve, so there the dock draws its own frosted wallpaper instead: see SetFrost.)
    /// </summary>
    public double BackgroundCornerRadius => Math.Round(DockHeight * 0.34);

    /// <summary>Current background rectangle in canvas coordinates (Empty before first layout).</summary>
    public Rect BackgroundBounds => _lastBackground;

    /// <summary>Height of the dock background (unmagnified).</summary>
    public double DockHeight => PadTop + BaseSize + DockIconView.DotRowHeight + PadBottom;

    /// <summary>Canvas height needed for fully magnified icons plus the label.</summary>
    public double RequiredHeight => LabelSpace + MaxSize + DockIconView.DotRowHeight + PadBottom + PadTop;

    /// <summary>Canvas width needed for the widest magnified layout plus label overflow.</summary>
    public double RequiredWidth =>
        TotalBaseWidth + 2 * PadSide + (MaxSize - BaseSize) * (2 * RangeInSlots + 1) + 240;

    public MagnifyingDock()
    {
        _background = new LiquidGlassSurface();
        _background.SetCornerRadius(BackgroundCornerRadius);
        SetZIndex(_background, -1);

        // Nearly invisible (alpha 1) so the OS keeps sending mouse input to us in the gaps
        // between magnified icons; fully transparent pixels would be click-through.
        _hitArea = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Visibility = Visibility.Collapsed,
        };
        SetZIndex(_hitArea, -2);

        _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 13 };
        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x24, 0x24, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 3, 10, 4),
            Child = _labelText,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        SetZIndex(_label, 10);
        // Sharp ClearType text on the label bubble (WPF disables it on transparent windows otherwise).
        RenderOptions.SetClearTypeHint(_label, ClearTypeHint.Enabled);
        MacFonts.Smooth(_label);

        Children.Add(_hitArea);
        Children.Add(_background);
        Children.Add(_label);

        MouseMove += (_, e) =>
        {
            _mouseX = e.GetPosition(this).X;
            _mouseInside = true;
            StartAnimation();
        };
        MouseLeave += (_, _) =>
        {
            _mouseInside = false;
            StartAnimation();
        };
        SizeChanged += (_, _) => ArrangeSlots();
    }

    /// <summary>Replaces the dock items (icons and separators), keeping animation state of existing ones.</summary>
    /// <summary>
    /// Replaces the dock items. Like macOS, icons that appear grow in from zero width and icons that
    /// disappear shrink away (the dock never jumps); existing icons keep their animation state.
    /// </summary>
    public void SetItems(IReadOnlyList<FrameworkElement> elements)
    {
        bool firstFill = _slots.Count == 0;
        var previous = _slots.ToDictionary(s => s.Element);
        var next = new List<Slot>();
        foreach (var element in elements)
        {
            if (!Children.Contains(element)) Children.Add(element);
            if (previous.TryGetValue(element, out var slot))
            {
                slot.Removing = false;
                next.Add(slot);
            }
            else
            {
                bool scalable = element is DockIconView;
                next.Add(new Slot(element, scalable, firstFill || !scalable ? BaseSize : 0));
            }
        }

        // Icons that left: keep them in place, shrinking to nothing (separators just go).
        for (int i = 0; i < _slots.Count; i++)
        {
            var old = _slots[i];
            if (elements.Contains(old.Element)) continue;
            if (!old.Scalable)
            {
                Children.Remove(old.Element);
                continue;
            }
            old.Removing = true;
            next.Insert(Math.Min(i, next.Count), old);
        }

        _slots.Clear();
        _slots.AddRange(next);
        ArrangeSlots();
        StartAnimation();
    }

    // ---------------- Geometry ----------------

    private double SlotBaseWidth(Slot s) => s.Scalable ? BaseSize + 2 * Spacing : SeparatorWidth + 2 * SeparatorMargin;
    private double SlotWidth(Slot s) => s.Scalable ? s.Current + 2 * Spacing : SlotBaseWidth(s);
    private double TotalBaseWidth => _slots.Sum(SlotBaseWidth);
    private double IconBottom => ActualHeight - PadBottom - DockIconView.DotRowHeight;

    /// <summary>Left edge of the layout: always centered, so the dock grows evenly on both sides.</summary>
    private double LayoutStart(double totalWidth) => ActualWidth / 2 - totalWidth / 2;

    /// <summary>Smooth bell curve: 1 at the cursor, 0 at <see cref="RangeInSlots"/> base slots away.</summary>
    private double Falloff(double distance)
    {
        double range = (BaseSize + 2 * Spacing) * RangeInSlots;
        return distance >= range ? 0 : (Math.Cos(Math.PI * distance / range) + 1) / 2;
    }

    /// <summary>
    /// Target size of each icon from the mouse distance to the icon's *current* center.
    /// Because sizes are smoothed, this converges quickly and the biggest icon stays under the cursor.
    /// </summary>
    private void UpdateTargets()
    {
        double totalWidth = _slots.Sum(SlotWidth);
        double x = LayoutStart(totalWidth);
        foreach (var slot in _slots)
        {
            double width = SlotWidth(slot);
            if (slot.Removing)
            {
                slot.Target = 0;
            }
            else if (slot.Scalable)
            {
                double center = x + width / 2;
                slot.Target = _mouseInside
                    ? BaseSize + (MaxSize - BaseSize) * Falloff(Math.Abs(_mouseX - center))
                    : BaseSize;
            }
            x += width;
        }
    }

    private void ArrangeSlots()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double totalWidth = _slots.Sum(SlotWidth);
        double start = LayoutStart(totalWidth);
        double iconBottom = IconBottom;

        // At rest, snap to whole device pixels so icons and edges are perfectly sharp.
        // While animating, keep sub-pixel positions for smooth motion.
        var dpi = VisualTreeHelper.GetDpi(this);
        bool snap = !_animating;
        double SnapX(double v) => snap ? Math.Round(v * dpi.DpiScaleX) / dpi.DpiScaleX : v;
        double SnapY(double v) => snap ? Math.Round(v * dpi.DpiScaleY) / dpi.DpiScaleY : v;
        start = SnapX(start);
        iconBottom = SnapY(iconBottom);
        double tallest = BaseSize;
        DockIconView? hovered = null;

        double x = start;
        foreach (var slot in _slots)
        {
            double width = SlotWidth(slot);
            if (slot.Element is DockIconView view)
            {
                view.UpdateDisplay(slot.Current, BaseSize, dpi);
                SetLeft(view, SnapX(x + Spacing));
                SetTop(view, SnapY(iconBottom - slot.Current));
                tallest = Math.Max(tallest, slot.Current);
                if (_mouseInside && !slot.Removing && _mouseX >= x && _mouseX < x + width) hovered = view;
            }
            else
            {
                double height = BaseSize * 0.7;
                slot.Element.Height = height;
                slot.Element.Width = SeparatorWidth;
                SetLeft(slot.Element, SnapX(x + SeparatorMargin));
                SetTop(slot.Element, iconBottom - BaseSize / 2 - height / 2);
            }
            x += width;
        }

        // Background: always the unmagnified height, as wide as the magnified icons.
        var bg = new Rect(SnapX(start - PadSide), SnapY(ActualHeight - DockHeight),
                          SnapX(totalWidth + 2 * PadSide), SnapY(DockHeight));
        SetLeft(_background, bg.Left);
        SetTop(_background, bg.Top);
        _background.Width = bg.Width;
        _background.Height = bg.Height;
        if (_frostImage != null)
            _background.SetFrost(_frostImage, new Rect(_frostRect.X - bg.Left, _frostRect.Y - bg.Top, _frostRect.Width, _frostRect.Height));
        if (!AreClose(bg, _lastBackground))
        {
            _lastBackground = bg;
            BackgroundBoundsChanged?.Invoke(bg);
        }

        // Hit area covers the magnified icons while hovering so the cursor doesn't "fall through" gaps.
        bool magnified = _mouseInside || tallest > BaseSize + 0.5;
        _hitArea.Visibility = magnified ? Visibility.Visible : Visibility.Collapsed;
        if (magnified)
        {
            double top = iconBottom - tallest;
            SetLeft(_hitArea, bg.Left);
            SetTop(_hitArea, top);
            _hitArea.Width = bg.Width;
            _hitArea.Height = Math.Max(0, bg.Top - top);
        }

        UpdateLabel(hovered);
    }

    /// <summary>Sets the background tint (the blur is drawn behind it by the backdrop window).</summary>
    private ImageSource? _frostImage;
    private Rect _frostRect;

    /// <summary>Frosted wallpaper behind the glass (Windows 11); <paramref name="strip"/> is its place in this control's coordinates.</summary>
    public void SetFrost(ImageSource? image, Rect strip)
    {
        _frostImage = image;
        _frostRect = strip;
        if (image == null) _background.SetFrost(null, default);
        else if (!_lastBackground.IsEmpty)
            _background.SetFrost(image, new Rect(strip.X - _lastBackground.Left, strip.Y - _lastBackground.Top, strip.Width, strip.Height));
    }

    public void SetBackgroundTint(Color color, double opacity)
    {
        _background.SetTint(color, opacity);
    }

    /// <summary>Applies new <see cref="BaseSize"/>/<see cref="MaxSize"/>: snaps all icons to the base size.</summary>
    public void ResetSizes()
    {
        foreach (var gone in _slots.Where(s => s.Removing).ToList())
        {
            _slots.Remove(gone);
            Children.Remove(gone.Element);
        }
        foreach (var slot in _slots)
            if (slot.Scalable) slot.Current = slot.Target = BaseSize;
        _background.SetCornerRadius(BackgroundCornerRadius);
        _lastBackground = Rect.Empty;
        ArrangeSlots();
    }

    private bool _labelSuppressed;

    /// <summary>Hides the name label (e.g. while a context menu is open).</summary>
    public bool LabelSuppressed
    {
        get => _labelSuppressed;
        set { _labelSuppressed = value; ArrangeSlots(); }
    }

    /// <summary>Forgets the hover state and shrinks back; the next mouse move re-magnifies.</summary>
    public void ResetHover()
    {
        _mouseInside = false;
        StartAnimation();
    }

    private void UpdateLabel(DockIconView? hovered)
    {
        if (hovered == null || _labelSuppressed)
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }

        if (_labelText.Text != hovered.Item.Name)
            _labelText.Text = hovered.Item.Name;
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var size = _label.DesiredSize;
        double centerX = GetLeft(hovered) + hovered.IconSize / 2;
        SetLeft(_label, Math.Clamp(centerX - size.Width / 2, 0, Math.Max(0, ActualWidth - size.Width)));
        SetTop(_label, Math.Max(0, GetTop(hovered) - size.Height - LabelGap));
    }

    private static bool AreClose(Rect a, Rect b) =>
        !b.IsEmpty &&
        Math.Abs(a.X - b.X) < 0.25 && Math.Abs(a.Y - b.Y) < 0.25 &&
        Math.Abs(a.Width - b.Width) < 0.25 && Math.Abs(a.Height - b.Height) < 0.25;

    // ---------------- Animation ----------------

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // Rendering can fire more than once per frame
        double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Min((now - _lastFrame).TotalSeconds, 1.0 / 30);
        _lastFrame = now;

        UpdateTargets();

        double k = 1 - Math.Exp(-Smoothing * dt);
        bool settled = true;
        foreach (var slot in _slots)
        {
            double diff = slot.Target - slot.Current;
            if (Math.Abs(diff) > 0.05)
            {
                slot.Current += diff * k;
                settled = false;
            }
            else
            {
                slot.Current = slot.Target;
            }
        }

        // Icons that finished shrinking away are removed for good.
        foreach (var gone in _slots.Where(s => s.Removing && s.Current < 0.5).ToList())
        {
            _slots.Remove(gone);
            Children.Remove(gone.Element);
        }

        if (settled) StopAnimation(); // restarts on the next mouse move
        ArrangeSlots();               // after stopping: the final layout is pixel-snapped
    }

    private sealed class Slot
    {
        public Slot(FrameworkElement element, bool scalable, double size)
        {
            Element = element;
            Scalable = scalable;
            Current = Target = size;
        }

        public FrameworkElement Element { get; }
        public bool Scalable { get; }
        public bool Removing { get; set; }
        public double Current { get; set; }
        public double Target { get; set; }
    }
}
