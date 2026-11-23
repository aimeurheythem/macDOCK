using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MacDock.Native;
using MacDock.Services;

namespace MacDock.Controls;

/// <summary>
/// macOS Tahoe-style Control Center: glass "modules" on a blurred glass panel.
///   Wi-Fi/Network pill, Bluetooth and Airplane round buttons, Now Playing (media keys),
///   Focus pill, Screen mirroring and Screenshot buttons, Display (brightness) and Sound sliders.
/// Follows the wallpaper like the menu bar: clear glass with white text on dark wallpapers,
/// light glass with dark text on light ones. With soundOnly, just the Sound module.
/// </summary>
internal sealed class ControlCenterPanel : Border
{
    private const double Unit = 66, Gap = 12;
    public const double PanelWidth = Unit * 4 + Gap * 5 + 2; // four module units, gaps, 1 px rim each side
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly Brush _text, _subText, _module, _moduleRim, _round, _track;
    private readonly ThinSlider _sound;
    private readonly AudioVolume _volume;

    /// <summary>Raised after a module opened something (the panel should close).</summary>
    public event Action? Done;

    public ControlCenterPanel(AudioVolume volume, NetworkState network, bool soundOnly, Action openNetwork, bool dark)
    {
        _volume = volume;
        _text = dark ? Brushes.White : Frozen(0xFF, 0x1D, 0x1D, 0x1F);
        _subText = dark ? Frozen(0xB3, 0xFF, 0xFF, 0xFF) : Frozen(0x99, 0x1D, 0x1D, 0x1F);
        _module = dark ? Frozen(0x2E, 0xFF, 0xFF, 0xFF) : Frozen(0x8C, 0xFF, 0xFF, 0xFF);
        _moduleRim = dark ? Frozen(0x40, 0xFF, 0xFF, 0xFF) : Frozen(0xCC, 0xFF, 0xFF, 0xFF);
        _round = dark ? Frozen(0x33, 0xFF, 0xFF, 0xFF) : Frozen(0x99, 0xFF, 0xFF, 0xFF);
        _track = dark ? Frozen(0x40, 0xFF, 0xFF, 0xFF) : Frozen(0x2E, 0x00, 0x00, 0x00);

        double radius = Glass.Radius(24);
        Width = soundOnly ? 300 : PanelWidth;
        Padding = new Thickness(Gap);
        CornerRadius = new CornerRadius(radius);
        Background = GlassTheme.Background; // exactly the dock's glass
        BorderBrush = GlassTheme.Rim;
        BorderThickness = new Thickness(1);
        MacFonts.Smooth(this);
        TextElement.SetForeground(this, _text);
        TextElement.SetFontFamily(this, MacFonts.Text);

        _sound = new ThinSlider(_track, v => { volume.Level = (float)v; if (volume.Muted && v > 0) volume.Muted = false; })
        {
            Value = volume.Level ?? 0,
        };

        var stack = new StackPanel();
        if (!soundOnly)
        {
            // Row 1: connectivity (left) + Now Playing (right).
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Unit * 2 + Gap) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
            top.ColumnDefinitions.Add(new ColumnDefinition());

            var connectivity = new StackPanel();
            bool online = network.Kind != NetworkKind.None;
            connectivity.Children.Add(Pill(
                network.Kind == NetworkKind.Ethernet ? "\uE839" : "\uE701",
                network.Kind == NetworkKind.Ethernet ? "Network" : "Wi-Fi",
                online ? network.Name ?? "Connected" : "Off", online, openNetwork));
            var rounds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, Gap, 0, 0) };
            rounds.Children.Add(Round("\uE702", "Bluetooth", () => SystemSettingsWindow.OpenUri("ms-settings:bluetooth")));
            rounds.Children.Add(Round("\uE709", "Airplane mode", () => SystemSettingsWindow.OpenUri("ms-settings:network-airplanemode"), left: Gap));
            connectivity.Children.Add(rounds);
            top.Children.Add(connectivity);

            var media = NowPlaying();
            Grid.SetColumn(media, 2);
            top.Children.Add(media);
            stack.Children.Add(top);

            // Row 2: Focus pill + two round buttons.
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, Gap, 0, 0) };
            row2.Children.Add(Pill("\uE708", "Focus", "Do not disturb", false, () => SystemSettingsWindow.OpenUri("ms-settings:quiethours")));
            row2.Children.Add(Round("\uE7F4", "Screen mirroring", () => PressKeys(0x5B, (byte)'K'), left: Gap));
            row2.Children.Add(Round("\uE722", "Screenshot", () => Shell.Open("ms-screenclip:"), left: Gap));
            stack.Children.Add(row2);

            // Row 3: Display brightness (hidden until we know the screen supports it).
            var display = new ThinSlider(_track, v => Task.Run(() => Brightness.Set(v))) { Value = 0.5, Visibility = Visibility.Hidden };
            stack.Children.Add(SliderModule("Display", "\uE706", display, "\uE7F4", () => SystemSettingsWindow.OpenUri("ms-settings:display"), Gap));
            Task.Run(Brightness.Get).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is double level)
                {
                    display.Value = level;
                    display.Visibility = Visibility.Visible;
                }
                else
                {
                    display.Visibility = Visibility.Collapsed; // not adjustable: the module's button opens Display settings
                }
            }));
        }

        var soundModule = SliderModule("Sound", "\uE767", _sound, "\uE7F5", () => SystemSettingsWindow.OpenUri("ms-settings:sound"), soundOnly ? 0 : Gap);
        if (soundOnly)
        {
            // The panel itself is the surface here: no second box inside it.
            soundModule.Background = Brushes.Transparent;
            soundModule.BorderThickness = new Thickness(0);
            stack.Children.Add(soundModule);
            AddMixer(stack);
        }
        else
        {
            stack.Children.Add(soundModule);
        }
        Child = stack;
    }

    /// <summary>Per-app volume list ("Applications"), like the Windows Volume Mixer.</summary>
    private void AddMixer(StackPanel stack)
    {
        var apps = AudioMixer.GetApps();
        stack.Children.Add(new Border { Height = 1, Background = _track, Margin = new Thickness(14, 4, 14, 8), Opacity = 0.6 });
        stack.Children.Add(new TextBlock
        {
            Text = apps.Count == 0 ? "No apps are playing sound" : "Applications",
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _subText, Margin = new Thickness(14, 0, 14, 6),
        });
        foreach (var app in apps)
        {
            var row = new DockPanel { Margin = new Thickness(14, 3, 10, 3) };
            var iconHost = new Border { Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "Mute / unmute" };
            var image = app.ExePath != null ? IconService.GetIconForParsingName(app.ExePath, 32) : null;
            iconHost.Child = image != null
                ? new Image { Source = image, Width = 22, Height = 22 }
                : Icon("\uE767", 14, _text);
            void ShowMute() => iconHost.Opacity = app.Muted ? 0.35 : 1;
            ShowMute();
            iconHost.MouseLeftButtonUp += (_, e) => { e.Handled = true; app.Muted = !app.Muted; ShowMute(); };
            row.Children.Add(iconHost);

            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = app.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            var slider = new ThinSlider(_track, v => { app.Level = (float)v; if (app.Muted && v > 0) { app.Muted = false; ShowMute(); } })
            {
                Value = app.Level, Height = 18,
            };
            texts.Children.Add(slider);
            row.Children.Add(texts);
            stack.Children.Add(row);
        }
        stack.Children.Add(new TextBlock
        {
            Text = "Browsers play every tab through one channel, so each browser has one slider.",
            FontSize = 10.5, Foreground = _subText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 4),
        });
    }

    /// <summary>Re-reads the volume (called when the panel opens).</summary>
    public void Refresh() => _sound.Value = _volume.Level ?? 0;

    // ---------------- Modules ----------------

    private Border Module(UIElement content, double width, double height, double radius) => new()
    {
        Width = width, Height = height,
        CornerRadius = new CornerRadius(radius),
        Background = _module, BorderBrush = _moduleRim, BorderThickness = new Thickness(0.8),
        Child = content,
    };

    /// <summary>Wide pill: white circle with an icon, title and subtitle.</summary>
    private Border Pill(string glyph, string title, string subtitle, bool on, Action action)
    {
        var circle = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Margin = new Thickness(12, 0, 10, 0),
            Background = on ? Brushes.White : _round,
            Child = Icon(glyph, 16, on ? Frozen(0xFF, 0x0A, 0x84, 0xFF) : _text),
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = _subText, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 76 });
        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(circle);
        row.Children.Add(texts);
        return Clickable(Module(row, Unit * 2 + Gap, Unit, Unit / 2), action);
    }

    /// <summary>Round glass button with an icon.</summary>
    private Border Round(string glyph, string tip, Action action, double left = 0)
    {
        var button = Module(Icon(glyph, 20, _text), Unit, Unit, Unit / 2);
        button.Margin = new Thickness(left, 0, 0, 0);
        button.ToolTip = tip;
        return Clickable(button, action);
    }

    /// <summary>Now Playing: works with any player through the media keys.</summary>
    private Border NowPlaying()
    {
        var art = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0x5E, 0x7E), Color.FromRgb(0xFF, 0x9F, 0x43), 45),
            Child = Icon("\uE8D6", 20, Brushes.White), HorizontalAlignment = HorizontalAlignment.Left,
        };
        var title = new TextBlock { Text = "Now Playing", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
        var sub = new TextBlock { Text = "Media controls", FontSize = 11.5, Foreground = _subText };
        var controls = new Columns(MediaButton("\uE892", 0xB1), MediaButton("\uE768", 0xB3, 18), MediaButton("\uE893", 0xB0))
        {
            Margin = new Thickness(0, 6, 0, 0),
        };
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(art);
        stack.Children.Add(title);
        stack.Children.Add(sub);
        stack.Children.Add(controls);
        return Module(stack, Unit * 2 + Gap, Unit * 2 + Gap, 20);
    }

    private FrameworkElement MediaButton(string glyph, byte key, double size = 14)
    {
        var host = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Height = 26, Child = Icon(glyph, size, _text) };
        host.MouseLeftButtonUp += (_, e) => { e.Handled = true; PressKeys(key); };
        return host;
    }

    /// <summary>Title, a thin slider after a small icon, and a round button at the end (settings).</summary>
    private Border SliderModule(string title, string leading, ThinSlider slider, string trailing, Action trailingAction, double top)
    {
        var grid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(row, 1);
        var trail = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = _round, Cursor = Cursors.Hand,
            Margin = new Thickness(10, 0, 0, 0), Child = Icon(trailing, 12, _text),
        };
        trail.MouseLeftButtonUp += (_, e) => { e.Handled = true; Done?.Invoke(); trailingAction(); };
        DockPanel.SetDock(trail, Dock.Right);
        row.Children.Add(trail);
        var lead = Icon(leading, 12, _subText);
        lead.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(lead, Dock.Left);
        row.Children.Add(lead);
        row.Children.Add(slider);
        grid.Children.Add(row);
        var module = Module(grid, double.NaN, double.NaN, 18);
        module.Margin = new Thickness(0, top, 0, 0);
        return module;
    }

    private Border Clickable(Border element, Action action)
    {
        element.Cursor = Cursors.Hand;
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Done?.Invoke();
            action();
        };
        return element;
    }

    private static TextBlock Icon(string glyph, double size, Brush brush) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Presses (and releases) the given keys in order, e.g. a media key or Win+K.</summary>
    private static void PressKeys(params byte[] keys)
    {
        var marker = WinKeyInterceptor.Marker; // our Win-key hook ignores these
        static uint Extended(byte k) => k is 0x5B or >= 0xA6 ? Win32.KEYEVENTF_EXTENDEDKEY : 0u;
        foreach (var k in keys) Win32.keybd_event(k, 0, Extended(k), marker);
        for (int i = keys.Length - 1; i >= 0; i--)
            Win32.keybd_event(keys[i], 0, Extended(keys[i]) | Win32.KEYEVENTF_KEYUP, marker);
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Equal-width columns.</summary>
    private sealed class Columns : Grid
    {
        public Columns(params FrameworkElement[] children)
        {
            for (int i = 0; i < children.Length; i++)
            {
                ColumnDefinitions.Add(new ColumnDefinition());
                SetColumn(children[i], i);
                Children.Add(children[i]);
            }
        }
    }

    /// <summary>macOS slider: thin rounded track, white fill and a white knob; click or drag anywhere, or scroll.</summary>
    private sealed class ThinSlider : Grid
    {
        private const double Knob = 16;
        private readonly Border _fill;
        private readonly Border _knob;
        private readonly Action<double> _changed;
        private double _value;

        public double Value
        {
            get => _value;
            set { _value = Math.Clamp(value, 0, 1); Layout(); }
        }

        public ThinSlider(Brush track, Action<double> changed)
        {
            _changed = changed;
            Height = 22;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = track, VerticalAlignment = VerticalAlignment.Center });
            _fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            _knob = new Border
            {
                Width = Knob, Height = Knob, CornerRadius = new CornerRadius(Knob / 2), Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = Frozen(0x26, 0, 0, 0), BorderThickness = new Thickness(0.5),
            };
            Children.Add(_fill);
            Children.Add(_knob);
            SizeChanged += (_, _) => Layout();
            MouseLeftButtonDown += (_, e) => { CaptureMouse(); SetFrom(e.GetPosition(this).X); e.Handled = true; };
            MouseMove += (_, e) => { if (IsMouseCaptured) SetFrom(e.GetPosition(this).X); };
            MouseLeftButtonUp += (_, e) => { ReleaseMouseCapture(); e.Handled = true; };
            MouseWheel += (_, e) => { Set(_value + Math.Sign(e.Delta) * 0.04); e.Handled = true; };
        }

        private void SetFrom(double x) => Set((x - Knob / 2) / Math.Max(1, ActualWidth - Knob));

        private void Set(double value)
        {
            Value = value;
            _changed(_value);
        }

        private void Layout()
        {
            double travel = Math.Max(0, ActualWidth - Knob);
            _knob.Margin = new Thickness(travel * _value, 0, 0, 0);
            _fill.Width = travel * _value + Knob / 2;
        }
    }
}
