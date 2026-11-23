using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using MacDock.Controls;
using MacDock.Services;
using MacDock.Models;

namespace MacDock;

/// <summary>
/// The installer, in Windows 11 "Fluent" style. Left: an animated scene where a Windows desktop turns
/// into a macOS one (the taskbar slides away, the menu bar and the dock arrive, icons pop in, a window
/// genies into the dock). Right: install options, then progress, then a "ready" check mark.
/// </summary>
internal sealed class SetupWindow : Window
{
    private static readonly Brush Accent = Frozen(0xFF, 0x00, 0x5F, 0xB8);
    private static readonly Brush AccentHover = Frozen(0xFF, 0x19, 0x6E, 0xBF);
    private static readonly Brush TextPrimary = Frozen(0xFF, 0x1A, 0x1A, 0x1A);
    private static readonly Brush TextSecondary = Frozen(0xFF, 0x5D, 0x5D, 0x5D);
    private static readonly Brush CardBack = Frozen(0xFF, 0xFB, 0xFB, 0xFB);
    private static readonly Brush Stroke = Frozen(0x0F, 0x00, 0x00, 0x00);
    private static readonly FontFamily Display = MacFonts.Text;
    private static readonly FontFamily TextFont = MacFonts.Text;

    private readonly ContentControl _page = new();
    private readonly CheckBox _startup = FluentCheck("Start MacDock when Windows starts", true);
    private readonly CheckBox _desktop = FluentCheck("Add a shortcut to the desktop", true);
    private readonly Canvas _scene = new() { Width = 380, Height = 440, ClipToBounds = true };

    public SetupWindow()
    {
        Title = "MacDock Setup";
        Width = 880;
        Height = 520;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.SingleBorderWindow;
        Background = Frozen(0xFF, 0xF3, 0xF3, 0xF3);
        FontFamily = TextFont;
        UseLayoutRounding = true;
        MacFonts.Smooth(this);
        Icon = AppLogo.Render(64);
        // Our own title area; keeps the system shadow and (on Windows 11) the rounded corners.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(412) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // Left: the animated scene in a rounded card.
        var sceneCard = new Border
        {
            Margin = new Thickness(16, 16, 16, 16),
            CornerRadius = new CornerRadius(10),
            Child = _scene,
            ClipToBounds = true,
        };
        sceneCard.Clip = new RectangleGeometry(new Rect(0, 0, 380, 488), 12, 12);
        root.Children.Add(sceneCard);
        BuildScene();

        // Right: pages.
        var right = new Grid { Margin = new Thickness(12, 24, 36, 28) };
        Grid.SetColumn(right, 1);
        right.Children.Add(_page);
        root.Children.Add(right);

        // Title-bar buttons.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(buttons, 1);
        buttons.Children.Add(CaptionButton("\uE921", false, () => WindowState = WindowState.Minimized));
        buttons.Children.Add(CaptionButton("\uE8BB", true, Close));
        root.Children.Add(buttons);

        Content = root;
        ShowWelcome();
        Loaded += (_, _) => PlayIntro();
    }

    // ---------------- Pages ----------------

    private void ShowWelcome()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
        header.Children.Add(new Image { Source = AppLogo.Create(), Width = 52, Height = 52 });
        var titles = new StackPanel { Margin = new Thickness(14, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text("MacDock", 26, FontWeights.SemiBold, TextPrimary, Display));
        titles.Children.Add(Text($"Version {Installer.Version}", 12, FontWeights.Normal, TextSecondary));
        header.Children.Add(titles);
        stack.Children.Add(header);

        stack.Children.Add(Text("Turn Windows into macOS", 22, FontWeights.SemiBold, TextPrimary, Display));
        stack.Children.Add(Wrap("A Mac-style dock with magnification, a menu bar, Control Center, a clean Start menu and the Genie effect, made to feel native on your PC.", 14, TextSecondary, new Thickness(0, 8, 0, 22)));

        var options = Card(new StackPanel { Children = { _startup, _desktop } });
        stack.Children.Add(options);
        stack.Children.Add(Wrap($"Installs for you only, in {Installer.InstallDir}. No administrator rights needed.", 12, TextSecondary, new Thickness(2, 10, 0, 26)));

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(AccentButton(Installer.IsInstalled ? "Update" : "Install", StartInstall));
        actions.Children.Add(StandardButton("Cancel", Close, new Thickness(8, 0, 0, 0)));
        stack.Children.Add(actions);
        SetPage(stack);
    }

    private async void StartInstall()
    {
        var status = Text("Preparing…", 14, FontWeights.Normal, TextSecondary);
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Frozen(0x33, 0, 0, 0), Margin = new Thickness(0, 14, 0, 0) };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var bar = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bar.Children.Add(track);
        bar.Children.Add(fill);
        track.Margin = new Thickness(0);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Text("Installing MacDock", 24, FontWeights.SemiBold, TextPrimary, Display));
        stack.Children.Add(Wrap("Your taskbar is about to get a Mac makeover. This only takes a moment.", 14, TextSecondary, new Thickness(0, 8, 0, 26)));
        stack.Children.Add(status);
        stack.Children.Add(bar);
        SetPage(stack);

        void Report(string message, double value) => Dispatcher.Invoke(() =>
        {
            status.Text = message;
            double target = Math.Max(0, bar.ActualWidth) * value;
            fill.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        });

        bool startup = _startup.IsChecked == true, desktop = _desktop.IsChecked == true;
        try
        {
            await Task.Run(() =>
            {
                Installer.Install(startup, desktop, (message, value) =>
                {
                    Report(message, value);
                    Thread.Sleep(450); // unhurried steps read better than a flash
                });
            });
            ShowDone();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowDone()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(CheckMark());
        stack.Children.Add(Text("MacDock is ready", 26, FontWeights.SemiBold, TextPrimary, Display, new Thickness(0, 22, 0, 0)));
        stack.Children.Add(Wrap("Launch it now and say hello to your new desktop. Press Ctrl+Alt+Shift+T at any time to bring the Windows taskbar back.", 14, TextSecondary, new Thickness(0, 8, 0, 28)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(AccentButton("Launch MacDock", () =>
        {
            Installer.LaunchInstalled();
            Close();
        }));
        actions.Children.Add(StandardButton("Close", Close, new Thickness(8, 0, 0, 0)));
        stack.Children.Add(actions);
        SetPage(stack);
    }

    private void ShowError(string message)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Text("Something went wrong", 24, FontWeights.SemiBold, TextPrimary, Display));
        stack.Children.Add(Wrap(message, 14, TextSecondary, new Thickness(0, 8, 0, 26)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(AccentButton("Try again", StartInstall));
        actions.Children.Add(StandardButton("Close", Close, new Thickness(8, 0, 0, 0)));
        stack.Children.Add(actions);
        SetPage(stack);
    }

    /// <summary>Swaps the right-hand content with a short fade and slide.</summary>
    private void SetPage(FrameworkElement page)
    {
        page.Opacity = 0;
        page.RenderTransform = new TranslateTransform(18, 0);
        _page.Content = page;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        page.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
    }

    /// <summary>Accent circle that pops in, then a check mark that draws itself.</summary>
    private static FrameworkElement CheckMark()
    {
        var circle = new Ellipse { Width = 64, Height = 64, Fill = Accent };
        var check = new Path
        {
            Data = Geometry.Parse("M 19,33 L 28,42 L 46,23"),
            Stroke = Brushes.White, StrokeThickness = 4.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, StrokeDashArray = new DoubleCollection { 20, 20 }, StrokeDashOffset = 20,
        };
        var host = new Grid { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left, RenderTransformOrigin = new Point(0.5, 0.5) };
        host.Children.Add(circle);
        host.Children.Add(check);
        var scale = new ScaleTransform(0, 0);
        host.RenderTransform = scale;
        host.Loaded += (_, _) =>
        {
            var pop = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            check.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(420))
            {
                BeginTime = TimeSpan.FromMilliseconds(320), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        };
        return host;
    }

    // ---------------- The animated scene ----------------
    //
    // One continuous "magic move": the Windows taskbar itself becomes the dock. Its dark full-width
    // strip shrinks, rounds its corners, lifts and turns to glass, while its icons fly to their dock
    // places with a soft spring. The wallpaper crossfades to a macOS-style one and the transparent
    // menu bar fades in. Then a virtual cursor glides over the dock with real magnification.

    private const double SceneW = 380, SceneH = 488;
    private const double TaskbarH = 40, TaskIcon = 20, TaskGap = 14;
    private const double DockIcon = 40, DockGap = 8, DockPad = 10, DockH = 58, DockBottom = 16;

    private System.Windows.Shapes.Rectangle _bar = null!;
    private SolidColorBrush _barFill = null!, _barStroke = null!;
    private Grid _macWallpaper = null!;
    private FrameworkElement _menuBar = null!;
    private readonly List<FrameworkElement> _icons = new();
    private bool _magnifying;
    private TimeSpan _magnifyStart;

    private void BuildScene()
    {
        _scene.Width = SceneW;
        _scene.Height = SceneH;

        // Windows wallpaper: deep navy with a blue bloom.
        var windowsWall = new Grid { Width = SceneW, Height = SceneH, Background = Solid(0xFF, 0x06, 0x12, 0x3A) };
        windowsWall.Children.Add(Glow(0.55, 0.62, 0.75, System.Windows.Media.Color.FromArgb(0xFF, 0x2B, 0x7B, 0xFF)));
        windowsWall.Children.Add(Glow(0.45, 0.70, 0.40, System.Windows.Media.Color.FromArgb(0xC0, 0x9F, 0xD4, 0xFF)));
        _scene.Children.Add(windowsWall);

        // macOS-style wallpaper (fades in): soft violet, blue and peach light.
        _macWallpaper = new Grid { Width = SceneW, Height = SceneH, Opacity = 0, Background = new LinearGradientBrush(
            System.Windows.Media.Color.FromRgb(0x1E, 0x1B, 0x4B), System.Windows.Media.Color.FromRgb(0x5B, 0x3F, 0x9E), 90) };
        _macWallpaper.Children.Add(Glow(0.15, 0.25, 0.85, System.Windows.Media.Color.FromArgb(0xD0, 0x3B, 0x82, 0xF6)));
        _macWallpaper.Children.Add(Glow(0.90, 0.80, 0.80, System.Windows.Media.Color.FromArgb(0xC8, 0xFF, 0x9A, 0x7A)));
        _macWallpaper.Children.Add(Glow(0.70, 0.30, 0.55, System.Windows.Media.Color.FromArgb(0x90, 0xE0, 0x7B, 0xFF)));
        _scene.Children.Add(_macWallpaper);

        // Transparent macOS menu bar with white text.
        var bar = new DockPanel { Width = SceneW, Height = 22, LastChildFill = false, Opacity = 0, RenderTransform = new TranslateTransform(0, -6) };
        var logo = new Image { Source = WindowsGlyph(), Width = 11, Height = 11, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(logo);
        bar.Children.Add(Text("Finder", 10.5, FontWeights.Bold, Brushes.White, null, new Thickness(12, 0, 0, 0), VerticalAlignment.Center));
        bar.Children.Add(Text("File     Edit     View     Window", 10.5, FontWeights.Normal, Brushes.White, null, new Thickness(14, 0, 0, 0), VerticalAlignment.Center));
        var clock = Text(DateTime.Now.ToString("ddd h:mm"), 10.5, FontWeights.Medium, Brushes.White, null, new Thickness(0, 0, 12, 0), VerticalAlignment.Center);
        DockPanel.SetDock(clock, Dock.Right);
        bar.Children.Insert(0, clock);
        _menuBar = bar;
        _scene.Children.Add(bar);

        // The bar that is first the taskbar, then the dock.
        _barFill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, 0x1C, 0x1C, 0x22));
        _barStroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
        _bar = new System.Windows.Shapes.Rectangle { Width = SceneW, Height = TaskbarH, Fill = _barFill, Stroke = _barStroke, StrokeThickness = 1 };
        Canvas.SetLeft(_bar, 0);
        Canvas.SetTop(_bar, SceneH - TaskbarH);
        _scene.Children.Add(_bar);

        // Icons: Start, then five apps.
        // Real app icons, made exactly like the dock makes them (custom icons, clean system icons, app logos).
        var icons = SceneIcons();
        for (int i = 0; i < _iconCount; i++)
        {
            var tile = new Grid
            {
                Width = DockIcon, Height = DockIcon,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(TaskIcon / DockIcon, TaskIcon / DockIcon),
            };
            var image = new Image { Source = icons[i], Width = DockIcon, Height = DockIcon };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            if (i == 0)
            {
                // The Start button turns into Finder during the morph.
                _startGlyph = new Image { Source = StartGlyph(), Width = DockIcon * 0.8, Height = DockIcon * 0.8 };
                _finderGlyph = image;
                image.Opacity = 0;
                tile.Children.Add(_startGlyph);
            }
            tile.Children.Add(image);
            var (x, y) = TaskbarSpot(i);
            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);
            _icons.Add(tile);
            _scene.Children.Add(tile);
        }
    }

    private Image _startGlyph = null!, _finderGlyph = null!;

    /// <summary>Finder, then five real apps found on this PC (with built-in fallbacks).</summary>
    private static ImageSource?[] SceneIcons()
    {
        var result = new ImageSource?[_iconCount];
        result[0] = FinderIcon.Create();
        var candidates = new List<Func<ImageSource?>>
        {
            () => FromExe(@"C:\Windows\explorer.exe"),
            () => FromExe(@"C:\Program Files\Google\Chrome\Application\chrome.exe")
                  ?? FromExe(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
            () => SystemIcons.TryRender("Settings", null, null, 96),
            () => FromExe(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe")
                  ?? SystemIcons.TryRender("Calculator", null, null, 96),
            () => SystemIcons.TryRender("Terminal", null, null, 96),
            () => SystemIcons.TryRender("Notepad", null, null, 96),
            () => SystemIcons.TryRender("Calculator", null, null, 96),
        };
        int slot = 1;
        foreach (var get in candidates)
        {
            if (slot >= _iconCount) break;
            ImageSource? icon = null;
            try { icon = get(); } catch { }
            if (icon != null && !result.Contains(icon)) result[slot++] = icon;
        }
        return result;
    }

    private static ImageSource? FromExe(string path) =>
        System.IO.File.Exists(path) ? DockItemFactory.FromPinned(new PinnedApp { Path = path })?.Icon : null;

    /// <summary>Top-left of icon <paramref name="i"/> (full 40 px tile, scaled about its center) in the taskbar row.</summary>
    private (double X, double Y) TaskbarSpot(int i)
    {
        double row = _iconCount * TaskIcon + (_iconCount - 1) * TaskGap;
        double cx = (SceneW - row) / 2 + i * (TaskIcon + TaskGap) + TaskIcon / 2;
        double cy = SceneH - TaskbarH / 2;
        return (cx - DockIcon / 2, cy - DockIcon / 2);
    }

    private const int _iconCount = 6;
    private static double DockWidth => _iconCount * DockIcon + (_iconCount - 1) * DockGap + 2 * DockPad;
    private static double DockLeft => (SceneW - DockWidth) / 2;
    private static double DockTop => SceneH - DockBottom - DockH;

    private static (double X, double Y) DockSpot(int i) =>
        (DockLeft + DockPad + i * (DockIcon + DockGap), DockTop + (DockH - DockIcon) / 2);

    private void PlayIntro()
    {
        var morph = new SpringEase { Damping = 0.82, Stiffness = 9 };
        var smooth = new CubicEase { EasingMode = EasingMode.EaseInOut };
        const double start = 900, duration = 1000;

        // The strip becomes the dock.
        Animate(_bar, Canvas.LeftProperty, DockLeft, start, duration, morph);
        Animate(_bar, Canvas.TopProperty, DockTop, start, duration, morph);
        Animate(_bar, WidthProperty, DockWidth, start, duration, morph);
        Animate(_bar, HeightProperty, DockH, start, duration, morph);
        Animate(_bar, System.Windows.Shapes.Rectangle.RadiusXProperty, 18, start, duration, smooth);
        Animate(_bar, System.Windows.Shapes.Rectangle.RadiusYProperty, 18, start, duration, smooth);
        _barFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(duration))
            { BeginTime = TimeSpan.FromMilliseconds(start), EasingFunction = smooth });
        _barStroke.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(System.Windows.Media.Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(duration))
            { BeginTime = TimeSpan.FromMilliseconds(start + 200), EasingFunction = smooth });

        // Icons fly to their dock places, one after another.
        for (int i = 0; i < _icons.Count; i++)
        {
            var (x, y) = DockSpot(i);
            double delay = start + 60 + i * 45;
            var spring = new SpringEase { Damping = 0.7, Stiffness = 9 };
            Animate(_icons[i], Canvas.LeftProperty, x, delay, duration, spring);
            Animate(_icons[i], Canvas.TopProperty, y, delay, duration, spring);
            var scale = (ScaleTransform)_icons[i].RenderTransform;
            Animate(scale, ScaleTransform.ScaleXProperty, 1, delay, duration, spring);
            Animate(scale, ScaleTransform.ScaleYProperty, 1, delay, duration, spring);
        }

        // The Start button becomes Finder.
        Animate(_startGlyph, OpacityProperty, 0, start + 150, 450, smooth);
        Animate(_finderGlyph, OpacityProperty, 1, start + 150, 550, smooth);

        // New wallpaper and the menu bar.
        Animate(_macWallpaper, OpacityProperty, 1, start - 100, 1400, smooth);
        Animate(_menuBar, OpacityProperty, 1, start + 500, 600, smooth);
        Animate(_menuBar.RenderTransform, TranslateTransform.YProperty, 0, start + 500, 700, new CubicEase { EasingMode = EasingMode.EaseOut });

        // Then the dock comes alive under a gliding cursor.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(start + duration + 900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var icon in _icons)
            {
                // Hand the icons from the intro animations to per-frame layout.
                icon.BeginAnimation(Canvas.LeftProperty, null);
                icon.BeginAnimation(Canvas.TopProperty, null);
                ((ScaleTransform)icon.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ((ScaleTransform)icon.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ((ScaleTransform)icon.RenderTransform).ScaleX = ((ScaleTransform)icon.RenderTransform).ScaleY = 1;
            }
            _bar.BeginAnimation(Canvas.LeftProperty, null);
            _bar.BeginAnimation(WidthProperty, null);
            _bar.Width = DockWidth;
            Canvas.SetLeft(_bar, DockLeft);
            _magnifying = true;
            _magnifyStart = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnMagnifyFrame;
            Closed += (_, _) => CompositionTarget.Rendering -= OnMagnifyFrame;
        };
        timer.Start();
    }

    /// <summary>
    /// macOS magnification, every frame: a virtual cursor glides across the dock and back every few
    /// seconds; icons near it grow (Gaussian falloff), lift, and push their neighbours, and the dock widens.
    /// </summary>
    private void OnMagnifyFrame(object? sender, EventArgs e)
    {
        if (!_magnifying) return;
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_magnifyStart == TimeSpan.MinValue) _magnifyStart = now;
        double t = (now - _magnifyStart).TotalSeconds % 6.0; // 6 s cycle: glide 2.6 s, rest

        // Cursor path (scene x) and how strongly it magnifies (fades in/out at the dock's ends).
        double glide = Math.Clamp(t / 2.6, 0, 1);
        double eased = glide * glide * (3 - 2 * glide);
        double cursor = DockLeft - 20 + eased * (DockWidth + 40);
        double strength = Math.Sin(glide * Math.PI);
        if (t > 2.6) strength = 0;

        // Sizes, then a centered layout of the wider dock.
        var sizes = new double[_icons.Count];
        double total = 0;
        for (int i = 0; i < _icons.Count; i++)
        {
            var (x, _) = DockSpot(i);
            double d = (x + DockIcon / 2 - cursor) / 46;
            sizes[i] = DockIcon * (1 + 0.62 * strength * Math.Exp(-d * d));
            total += sizes[i];
        }
        double width = total + (_icons.Count - 1) * DockGap + 2 * DockPad;
        double left = (SceneW - width) / 2;
        Canvas.SetLeft(_bar, left);
        _bar.Width = width;

        double xPos = left + DockPad;
        double baseline = DockTop + (DockH + DockIcon) / 2; // icons sit on the dock's baseline and grow upwards
        for (int i = 0; i < _icons.Count; i++)
        {
            var scale = (ScaleTransform)_icons[i].RenderTransform;
            double s = sizes[i] / DockIcon;
            scale.ScaleX = scale.ScaleY = s;
            // RenderTransform scales about the center: place the center where the grown icon belongs.
            double cx = xPos + sizes[i] / 2, cy = baseline - sizes[i] / 2;
            Canvas.SetLeft(_icons[i], cx - DockIcon / 2);
            Canvas.SetTop(_icons[i], cy - DockIcon / 2);
            xPos += sizes[i] + DockGap;
        }
    }

    private static Ellipse Glow(double cx, double cy, double size, System.Windows.Media.Color color)
    {
        double d = size * SceneW * 1.4;
        var glow = new Ellipse
        {
            Width = d, Height = d, IsHitTestVisible = false,
            Fill = new RadialGradientBrush(color, System.Windows.Media.Color.FromArgb(0, color.R, color.G, color.B)),
            Margin = new Thickness(cx * SceneW - d / 2, cy * SceneH - d / 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        return glow;
    }

    private static Brush Solid(byte a, byte r, byte g, byte b) => Frozen(a, r, g, b);

    private static void Animate(DependencyObject target, DependencyProperty property, double to, double delayMs, double durationMs, IEasingFunction ease)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs)) { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = ease };
        if (target is UIElement element) element.BeginAnimation(property, animation);
        else if (target is Animatable animatable) animatable.BeginAnimation(property, animation);
    }

    /// <summary>The Windows 11 Start logo (blue), as on the taskbar.</summary>
    private static DrawingImage StartGlyph()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var blue = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x2F, 0xA4, 0xFF), System.Windows.Media.Color.FromRgb(0x00, 0x67, 0xC0), 90);
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 2; c++)
                    dc.DrawRoundedRectangle(blue, null, new Rect(c * 8.6, r * 8.6, 7.6, 7.6), 1.2, 1.2);
        }
        group.Freeze();
        return new DrawingImage(group);
    }

    private static DrawingImage WindowsGlyph()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 2; c++)
                    dc.DrawRoundedRectangle(Brushes.White, null, new Rect(c * 8.6, r * 8.6, 7.6, 7.6), 1.2, 1.2);
        }
        group.Freeze();
        return new DrawingImage(group);
    }

    /// <summary>A damped spring (like Apple's UI springs): fast start, gentle settle, a hint of overshoot.</summary>
    private sealed class SpringEase : EasingFunctionBase
    {
        public double Damping { get; set; } = 0.8;   // 1 = no overshoot
        public double Stiffness { get; set; } = 9;  // higher = settles sooner

        public SpringEase() => EasingMode = EasingMode.EaseIn; // EaseInCore is the whole curve

        protected override double EaseInCore(double t)
        {
            if (t >= 1) return 1;
            double w = Stiffness, z = Damping;
            double wd = w * Math.Sqrt(Math.Max(1e-6, 1 - z * z));
            return 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + z * w / wd * Math.Sin(wd * t));
        }

        protected override Freezable CreateInstanceCore() => new SpringEase { Damping = Damping, Stiffness = Stiffness };
    }

    // ---------------- Fluent building blocks ----------------

    private static TextBlock Text(string text, double size, FontWeight weight, Brush brush, FontFamily? font = null,
        Thickness margin = default, VerticalAlignment vertical = VerticalAlignment.Top) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = brush, Margin = margin, VerticalAlignment = vertical,
        FontFamily = font ?? TextFont,
    };

    private static TextBlock Wrap(string text, double size, Brush brush, Thickness margin) => new()
    {
        Text = text, FontSize = size, Foreground = brush, Margin = margin, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.45,
    };

    private static Border Card(UIElement content) => new()
    {
        Background = CardBack, BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 12, 16, 12), Child = content,
    };

    private static Button AccentButton(string text, Action click) => MakeButton(text, click, Accent, AccentHover, Brushes.White, null, default);

    private static Button StandardButton(string text, Action click, Thickness margin) =>
        MakeButton(text, click, Frozen(0xFF, 0xFB, 0xFB, 0xFB), Frozen(0xFF, 0xF5, 0xF5, 0xF5), TextPrimary, Frozen(0x29, 0, 0, 0), margin);

    /// <summary>Fluent button: 4 px corners, subtle hover, slight press scale.</summary>
    private static Button MakeButton(string text, Action click, Brush back, Brush hover, Brush fore, Brush? border, Thickness margin)
    {
        var template = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        bd.SetValue(Border.BackgroundProperty, back);
        bd.SetValue(Border.BorderBrushProperty, border ?? Brushes.Transparent);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(border == null ? 0 : 1));
        bd.SetValue(Border.PaddingProperty, new Thickness(22, 7, 22, 8));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        bd.AppendChild(presenter);
        template.VisualTree = bd;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Bd"));
        template.Triggers.Add(over);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "Bd"));
        template.Triggers.Add(pressed);

        var button = new Button
        {
            Content = text, Template = template, Foreground = fore, FontSize = 14, MinWidth = 120, Margin = margin,
            Cursor = Cursors.Hand, FontWeight = back == Accent ? FontWeights.SemiBold : FontWeights.Normal,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Fluent check box: 20 px rounded square, accent fill with a white check.</summary>
    private static CheckBox FluentCheck(string text, bool isChecked)
    {
        var template = new ControlTemplate(typeof(CheckBox));
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var box = new FrameworkElementFactory(typeof(Border), "Box");
        box.SetValue(FrameworkElement.WidthProperty, 20.0);
        box.SetValue(FrameworkElement.HeightProperty, 20.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(Border.BorderBrushProperty, Frozen(0x72, 0, 0, 0));
        box.SetValue(Border.BackgroundProperty, Frozen(0x05, 0, 0, 0));
        var mark = new FrameworkElementFactory(typeof(Path), "Mark");
        mark.SetValue(Path.DataProperty, Geometry.Parse("M 5,10.5 L 8.5,14 L 15,6.5"));
        mark.SetValue(Shape.StrokeProperty, Brushes.White);
        mark.SetValue(Shape.StrokeThicknessProperty, 1.8);
        mark.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        mark.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(mark);
        row.AppendChild(box);
        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(label);
        template.VisualTree = row;
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, Accent, "Box"));
        on.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Box"));
        on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Mark"));
        template.Triggers.Add(on);
        return new CheckBox
        {
            Content = text, IsChecked = isChecked, Template = template, FontSize = 14, Foreground = TextPrimary,
            Margin = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand,
        };
    }

    private Button CaptionButton(string glyph, bool isClose, Action click)
    {
        var template = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var text = new FrameworkElementFactory(typeof(TextBlock), "Glyph");
        text.SetValue(TextBlock.TextProperty, glyph);
        text.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        text.SetValue(TextBlock.FontSizeProperty, 10.0);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(text);
        template.VisualTree = bd;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, isClose ? Frozen(0xFF, 0xC4, 0x2B, 0x1C) : Frozen(0x0F, 0, 0, 0), "Bd"));
        if (isClose) over.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White, "Glyph"));
        template.Triggers.Add(over);
        var button = new Button { Template = template, Width = 46, Height = 32, Foreground = TextPrimary };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => click();
        return button;
    }

    private static System.Windows.Media.Color Argb(uint argb) => System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Brush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Argb(argb));
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
