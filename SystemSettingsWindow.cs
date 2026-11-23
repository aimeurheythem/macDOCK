using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using MacDock.Config;
using MacDock.Native;
using MacDock.Services;
using Path = System.IO.Path;

namespace MacDock;

/// <summary>
/// macOS "System Settings": a sidebar of panes (Wi-Fi, Bluetooth, Sound, Displays, Wallpaper...) on the
/// left and the selected pane on the right, laid out like macOS Sonoma. The panes change the real
/// Windows settings directly (see <see cref="WindowsSettings"/>), and "Desktop &amp; Dock" holds MacDock's
/// own settings. Only one window exists; <see cref="Open"/> brings it up on a pane.
/// </summary>
public sealed class SystemSettingsWindow : Window
{
    // ---------------- One window for the whole app ----------------

    private static SystemSettingsWindow? _instance;
    private static DockConfig? _config;
    private static Action? _changed;

    /// <summary>Called once by the dock: the settings it owns and how to apply them live.</summary>
    public static void Init(DockConfig config, Action changed)
    {
        _config = config;
        _changed = changed;
    }

    /// <summary>Shows the window on <paramref name="pane"/> (an id such as "sound"; null = the last one).</summary>
    public static void Open(string? pane = null)
    {
        if (_instance == null)
        {
            _instance = new SystemSettingsWindow();
            _instance.Closed += (_, _) =>
            {
                if (_config != null) ConfigService.Save(_config);
                _instance = null;
                MemoryTrim.Soon(); // its pages, pictures and icons go back to Windows
            };
            _instance.Navigate(pane ?? _lastPane, record: false);
            _instance.Show();
        }
        else if (pane != null) _instance.Navigate(pane);
        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        _instance.Activate();
        // Windows may refuse a plain Activate (another app had the focus): make sure it really comes to the front.
        var handle = new System.Windows.Interop.WindowInteropHelper(_instance).Handle;
        if (handle != IntPtr.Zero) WindowActivator.ForceForeground(handle);
    }

    /// <summary>
    /// Opens an "ms-settings:" link in the matching pane. Pages MacDock has no pane for open in
    /// Windows Settings (let through by <see cref="WindowsSettingsRedirect"/>).
    /// </summary>
    public static void OpenUri(string uri)
    {
        string page = uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? uri[12..].ToLowerInvariant() : uri;
        string? pane = page switch
        {
            "" => null,
            "bluetooth" or "connecteddevices" => "bluetooth",
            "network-wifi" or "network-airplanemode" or "network-wifisettings" => "wifi",
            "network" or "network-ethernet" or "network-status" or "network-proxy" or "network-vpn" => "network",
            "quiethours" or "quietmomentshome" => "focus",
            "notifications" => "notifications",
            "display" or "nightlight" => "displays",
            "sound" or "sound-devices" or "apps-volume" => "sound",
            "about" => "about",
            "regionlanguage" or "region" or "keyboard" => "keyboard",
            "dateandtime" => "datetime",
            "batterysaver" or "powersleep" => "battery",
            "taskbar" or "personalization-start" => "dock",
            "personalization" or "personalization-background" => "wallpaper",
            "colors" or "personalization-colors" => "appearance",
            "lockscreen" => "lock",
            "mousetouchpad" => "mouse",
            "printers" => "printers",
            "privacy" => "privacy",
            "yourinfo" or "otherusers" => "users",
            "storagesense" => "storage",
            "startupapps" => "login",
            "easeofaccess" => "accessibility",
            "windowsupdate" => "update",
            _ => "?",
        };
        if (pane == "?") WindowsSettingsRedirect.OpenInWindows(uri);
        else Open(pane);
    }

    /// <summary>True for Windows' Settings program (C:\Windows\ImmersiveControlPanel\SystemSettings.exe).</summary>
    public static bool IsSettingsExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return string.Equals(Path.GetFileName(path.Trim().Trim('"')), "SystemSettings.exe", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// What a shortcut to SystemSettings.exe (the dock, Start, a desktop icon) should do. Started directly,
    /// that program stops with a "buffer overrun" system error (it is a Store app and only runs when Windows
    /// starts it as one), so: MacDock's System Settings, or Windows Settings started the proper way.
    /// </summary>
    public static void OpenFromShortcut()
    {
        void Go()
        {
            if (_config?.MacSystemSettings == false) WindowsSettingsRedirect.OpenInWindows("ms-settings:");
            else Open();
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Go();
        else dispatcher.BeginInvoke(Go);
    }

    // ---------------- Panes ----------------

    private sealed record PaneInfo(string Id, string Title, string Glyph, uint Color, int Group, string Keywords = "");

    private static readonly PaneInfo[] Panes =
    {
        new("wifi", "Wi-Fi", "", 0x0A84FF, 0, "wireless internet network"),
        new("bluetooth", "Bluetooth", "", 0x0A84FF, 0, "devices headphones pair"),
        new("network", "Network", "", 0x0A84FF, 0, "ethernet ip adapter internet"),
        new("notifications", "Notifications", "", 0xFF3B30, 1, "alerts banners toast"),
        new("sound", "Sound", "", 0xFF2D55, 1, "volume speakers microphone output input audio"),
        new("focus", "Focus", "", 0x5E5CE6, 1, "do not disturb quiet"),
        new("general", "General", "", 0x8E8E93, 2, "about update storage login date time language"),
        new("appearance", "Appearance", "", 0x1C1C1E, 2, "dark light mode accent colour color theme"),
        new("accessibility", "Accessibility", "", 0x0A84FF, 2, "zoom narrator voiceover motion sticky"),
        new("dock", "Desktop & Dock", "", 0x1C1C1E, 2, "dock menu bar macdock finder taskbar title bar pointer cursor"),
        new("displays", "Displays", "", 0x0A84FF, 2, "resolution brightness monitor screen refresh"),
        new("wallpaper", "Wallpaper", "", 0x32ADE6, 2, "background desktop picture"),
        new("battery", "Battery", "", 0x34C759, 2, "energy power sleep plan"),
        new("performance", "Gaming & Performance", "\uE7FC", 0x30D158, 2, "game fps fast speed smooth tweak bloatware debloat apps ads telemetry"),
        new("lock", "Lock Screen", "", 0x1C1C1E, 3, "screen saver display off"),
        new("privacy", "Privacy & Security", "", 0x0A84FF, 3, "location camera microphone permissions"),
        new("users", "Users & Groups", "", 0x0A84FF, 3, "account password"),
        new("keyboard", "Keyboard", "", 0x8E8E93, 4, "key repeat input sources language"),
        new("mouse", "Mouse", "", 0x8E8E93, 4, "pointer tracking double click scroll"),
        new("printers", "Printers & Scanners", "", 0x8E8E93, 4, "print"),
        // Inside General (not in the sidebar).
        new("about", "About", "", 0x8E8E93, -1),
        new("update", "Software Update", "", 0x8E8E93, -1),
        new("storage", "Storage", "", 0x8E8E93, -1),
        new("login", "Login Items", "", 0x8E8E93, -1),
        new("language", "Language & Region", "", 0x0A84FF, -1),
        new("datetime", "Date & Time", "", 0x0A84FF, -1),
    };

    private static string _lastPane = "general";

    private static PaneInfo Info(string id) => Panes.FirstOrDefault(p => p.Id == id) ?? Panes[6];

    // ---------------- Palette (macOS Sonoma, light and dark) ----------------

    private bool _dark;
    private Color _accent;
    private Brush _sidebarBg = null!, _contentBg = null!, _groupBg = null!, _groupBorder = null!, _separator = null!,
        _text = null!, _sub = null!, _accentBrush = null!, _controlBg = null!, _controlBorder = null!, _hover = null!,
        _switchOff = null!, _track = null!, _menuBg = null!, _menuBorder = null!, _searchBg = null!, _frame = null!,
        _glassTint = null!, _glassRim = null!, _capsuleBg = null!;

    private static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    /// <summary>The bright edge of a glass surface: strongest at the top, where light hits it.</summary>
    private static Brush Rim(uint top, uint bottom)
    {
        var c = (uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        var b = new LinearGradientBrush(c(top), c(bottom), 90);
        b.Freeze();
        return b;
    }

    private static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private void LoadPalette()
    {
        _dark = WindowsSettings.IsDark;
        _accent = Rgb((uint)WindowsSettings.Accent);
        _accentBrush = new SolidColorBrush(_accent);
        _accentBrush.Freeze();
        if (_dark)
        {
            _sidebarBg = B(0xFF2A2A2D); _contentBg = B(0xFF1D1D1F); _groupBg = B(0x0FFFFFFF); _groupBorder = B(0x17FFFFFF);
            _separator = B(0x17FFFFFF); _text = B(0xEBFFFFFF); _sub = B(0x8CFFFFFF); _controlBg = B(0x2EFFFFFF);
            _controlBorder = B(0x1FFFFFFF); _hover = B(0x14FFFFFF); _switchOff = B(0x2EFFFFFF); _track = B(0x2EFFFFFF);
            _menuBg = B(0xD9262628); _menuBorder = B(0x33FFFFFF); _searchBg = B(0x1AFFFFFF); _frame = B(0x8C000000);
            _glassTint = B(0x8C2A2A30); _capsuleBg = B(0x24FFFFFF);
            _glassRim = Rim(0x52FFFFFF, 0x0FFFFFFF);
        }
        else
        {
            _sidebarBg = B(0xFFE9E8EC); _contentBg = B(0xFFF5F5F7); _groupBg = B(0xD9FFFFFF); _groupBorder = B(0x0F000000);
            _separator = B(0x12000000); _text = B(0xE0000000); _sub = B(0x80000000); _controlBg = B(0xFFFFFFFF);
            _controlBorder = B(0x1A000000); _hover = B(0x0D000000); _switchOff = B(0x1F000000); _track = B(0x1F000000);
            _menuBg = B(0xE0F7F7F9); _menuBorder = B(0x1F000000); _searchBg = B(0x12000000); _frame = B(0x33000000);
            _glassTint = B(0x99F3F3F6); _capsuleBg = B(0xF2FFFFFF);
            _glassRim = Rim(0xE6FFFFFF, 0x59FFFFFF);
        }
    }

    private static readonly FontFamily TextFont = MacFonts.Text;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // ---------------- Window ----------------

    private const double Shadow = 18;
    private readonly Grid _root = new();
    private readonly StackPanel _sidebarList = new() { Margin = new Thickness(10, 0, 10, 12) };
    private readonly TextBox _search = new();
    private readonly StackPanel _body = new() { Margin = new Thickness(20, 4, 20, 24) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _backButton = new(), _forwardButton = new();
    private readonly Stack<string> _back = new(), _forward = new();
    private string _current = "general";
    private int _pageToken;
    private readonly Dictionary<string, Border> _sidebarItems = new();

    private SystemSettingsWindow()
    {
        Title = "System Settings";
        Width = 760 + Shadow * 2;
        Height = 640 + Shadow * 2;
        MinWidth = 620 + Shadow * 2;
        MinHeight = 420 + Shadow * 2;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = TextFont;
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MacFonts.Smooth(this);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(Shadow), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/MacDock.ico")); } catch { }
        Content = _root;
        // Opens and closes like a Mac window; the glass blur only appears once it is fully open.
        MacMotion.AttachWindow(this, _root, shown: () => { _settled = true; PlaceBlur(); }, hiding: () => { _settled = false; _blur?.SetVisible(false); });
        MacMotion.SmoothScroll(_scroll);
        _search.TextChanged += (_, _) => BuildSidebar();
        BuildChrome();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _search.Text.Length > 0) _search.Text = "";
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) Close();
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) _search.Focus();
        };
        Activated += (_, _) => { SetLightsActive(true); PlaceBlur(); };
        Deactivated += (_, _) => SetLightsActive(false);
        LocationChanged += (_, _) => PlaceBlur();
        IsVisibleChanged += (_, _) => PlaceBlur();
        StateChanged += (_, _) => PlaceBlur();
        SourceInitialized += (_, _) =>
        {
            _blur = new BlurBackdrop(acrylic: false, topmost: false);
            if (_blur.Handle == IntPtr.Zero) _blur = null;
            _glassReady = true;
            if (_blur == null && _glassPanel != null) _glassPanel.Background = _sidebarBg; // no blur: a solid sidebar
            UpdateGlass();
        };
        // Light / dark or accent changed anywhere (here, in Windows, by a schedule): follow it.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            _blur?.Dispose();
            _blur = null;
        };
    }

    private void OnPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (WindowsSettings.IsDark != _dark || Rgb((uint)WindowsSettings.Accent) != _accent) Retheme();
        });

    // Liquid Glass (macOS 26+): a rounder window, and the sidebar is a floating pane of glass inset from
    // the window's edges; the desktop shows through it, blurred.
    private const double WindowRadius = 18, SidebarRadius = 14, SidebarInset = 8, SidebarWidth = 236;
    private Border _shadowHost = null!, _glassPanel = null!;
    private BlurBackdrop? _blur;
    private bool _settled; // opened (not animating in or out)
    private bool _glassReady;

    /// <summary>Builds (or rebuilds after a theme change) the frame, sidebar and header.</summary>
    private void BuildChrome()
    {
        LoadPalette();
        // Rebuilt after a theme change: the long-lived parts are taken out of the old frame first.
        foreach (var part in new FrameworkElement[] { _backButton, _forwardButton, _title, _scroll, _sidebarList, _search })
        {
            switch (part.Parent)
            {
                case Panel panel: panel.Children.Remove(part); break;
                case ContentControl control: control.Content = null; break;
                case Decorator decorator: decorator.Child = null; break;
            }
        }
        _root.Children.Clear();
        _root.Resources[typeof(ScrollBar)] = ThinScrollBar(_dark);

        // The window's body and shadow, with a hole where the glass sidebar is.
        _shadowHost = new Border
        {
            Margin = new Thickness(Shadow),
            CornerRadius = new CornerRadius(WindowRadius),
            Background = _contentBg,
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 8, Direction = 270, Opacity = _dark ? 0.6 : 0.3, Color = Colors.Black },
            CacheMode = new BitmapCache(), // the big soft shadow is drawn once, not on every frame
        };
        _root.Children.Add(_shadowHost);

        var frame = new Border
        {
            Margin = new Thickness(Shadow),
            CornerRadius = new CornerRadius(WindowRadius),
            BorderBrush = _frame,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
        };
        _root.Children.Add(frame);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth + SidebarInset) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        frame.Child = grid;

        // Sidebar: the floating glass pane.
        _glassPanel = new Border
        {
            Margin = new Thickness(SidebarInset, SidebarInset, 0, SidebarInset),
            CornerRadius = new CornerRadius(SidebarRadius),
            Background = _blur != null || !_glassReady ? _glassTint : _sidebarBg,
            BorderBrush = _glassRim,
            BorderThickness = new Thickness(1),
        };
        var side = new Grid();
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition());
        _glassPanel.Child = side;
        var lightsRow = new Grid { Background = Brushes.Transparent };
        lightsRow.MouseLeftButtonDown += DragWindow;
        lightsRow.Children.Add(BuildLights());
        side.Children.Add(lightsRow);
        var search = BuildSearch();
        Grid.SetRow(search, 1);
        side.Children.Add(search);
        var sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _sidebarList };
        MacMotion.SmoothScroll(sideScroll);
        Grid.SetRow(sideScroll, 2);
        side.Children.Add(sideScroll);
        grid.Children.Add(_glassPanel);
        _glassPanel.SizeChanged += (_, _) => UpdateGlass();

        // Content: a toolbar (back / forward in one glass capsule, then the pane's title) and the pane.
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        content.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumn(content, 1);
        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(16, 0, 20, 0), LastChildFill = true };
        header.MouseLeftButtonDown += DragWindow;
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (button, glyph, back) in new[] { (_backButton, "", true), (_forwardButton, "", false) })
        {
            button.Text = glyph;
            button.FontFamily = IconFont;
            button.FontSize = 12;
            button.Width = 30;
            button.Height = 30;
            button.Padding = new Thickness(0, 9, 0, 0);
            button.TextAlignment = TextAlignment.Center;
            button.Background = Brushes.Transparent;
            button.Cursor = Cursors.Hand;
            bool isBack = back;
            button.MouseLeftButtonDown += (_, e) => { e.Handled = true; GoHistory(isBack); };
            nav.Children.Add(button);
        }
        var capsule = new Border
        {
            CornerRadius = new CornerRadius(16), Height = 32, Padding = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = _capsuleBg, BorderBrush = _glassRim, BorderThickness = new Thickness(1), Child = nav,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = _dark ? 0.35 : 0.12 },
        };
        DockPanel.SetDock(capsule, Dock.Left);
        header.Children.Add(capsule);
        _title.Margin = new Thickness(12, 0, 0, 0);
        _title.FontSize = 15;
        _title.FontWeight = FontWeights.Bold;
        _title.Foreground = _text;
        header.Children.Add(_title);
        content.Children.Add(header);
        _scroll.Content = _body;
        Grid.SetRow(_scroll, 1);
        content.Children.Add(_scroll);
        grid.Children.Add(content);

        BuildSidebar();
        Dispatcher.BeginInvoke(UpdateGlass, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Cuts the window body open where the sidebar is and puts the blur right behind it.</summary>
    private void UpdateGlass()
    {
        if (_glassPanel == null || _shadowHost == null || _glassPanel.ActualWidth <= 0 || PresentationSource.FromVisual(_glassPanel) == null) return;
        try
        {
            var panel = _glassPanel.TransformToAncestor(_shadowHost).TransformBounds(new Rect(0, 0, _glassPanel.ActualWidth, _glassPanel.ActualHeight));
            _shadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-200, -200, _shadowHost.ActualWidth + 400, _shadowHost.ActualHeight + 400)),
                new RectangleGeometry(panel, SidebarRadius, SidebarRadius));
        }
        catch (InvalidOperationException) { return; } // not in the tree yet
        PlaceBlur();
    }

    private void PlaceBlur()
    {
        if (_blur == null || _glassPanel == null) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!_settled || !IsVisible || WindowState == WindowState.Minimized || PresentationSource.FromVisual(_glassPanel) == null || _glassPanel.ActualWidth <= 0)
        {
            _blur.SetVisible(false);
            return;
        }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var topLeft = _glassPanel.PointToScreen(new Point(0, 0));
        _blur.Place((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(_glassPanel.ActualWidth * scale), (int)Math.Round(_glassPanel.ActualHeight * scale), (int)Math.Round(SidebarRadius * scale));
        MacDock.Native.Win32.SetWindowPos(_blur.Handle, hwnd, 0, 0, 0, 0, MacDock.Native.Win32.SWP_NOMOVE | MacDock.Native.Win32.SWP_NOSIZE | MacDock.Native.Win32.SWP_NOACTIVATE);
        _blur.SetVisible(true);
    }

    /// <summary>A slim rounded macOS scroller instead of Windows' arrows-and-track scroll bar.</summary>
    private static Style ThinScrollBar(bool dark)
    {
        string thumb = dark ? "#59FFFFFF" : "#4D000000";
        string xaml = "<Style TargetType=\"ScrollBar\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            "<Setter Property=\"Width\" Value=\"10\"/><Setter Property=\"MinWidth\" Value=\"10\"/><Setter Property=\"Template\"><Setter.Value>" +
            "<ControlTemplate TargetType=\"ScrollBar\"><Track x:Name=\"PART_Track\" IsDirectionReversed=\"True\" Margin=\"0,4,2,4\"><Track.Thumb><Thumb><Thumb.Template>" +
            "<ControlTemplate TargetType=\"Thumb\"><Border CornerRadius=\"3\" Width=\"6\" HorizontalAlignment=\"Right\" Background=\"" + thumb + "\"/></ControlTemplate>" +
            "</Thumb.Template></Thumb></Track.Thumb></Track></ControlTemplate></Setter.Value></Setter></Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    // ---------------- Traffic lights ----------------

    private readonly List<(Ellipse Dot, Color Color)> _lights = new();

    private FrameworkElement BuildLights()
    {
        _lights.Clear();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var glyphs = new List<TextBlock>();
        var colors = new[] { Rgb(0xFF5F57), Rgb(0xFEBC2E), Rgb(0x28C840) };
        string[] marks = { "", "", "" };
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var dot = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(colors[i]), Stroke = B(0x1F000000), StrokeThickness = 0.5 };
            var mark = new TextBlock
            {
                Text = marks[i], FontFamily = IconFont, FontSize = 7, Foreground = B(0x99000000), Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            glyphs.Add(mark);
            var cell = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, i < 2 ? 8 : 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            cell.Children.Add(dot);
            cell.Children.Add(mark);
            cell.MouseLeftButtonDown += (_, e) => e.Handled = true;
            cell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (index == 0) Close();
                else if (index == 1) MacMotion.Minimize(this);
                else WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            };
            _lights.Add((dot, colors[i]));
            panel.Children.Add(cell);
        }
        panel.MouseEnter += (_, _) => { foreach (var g in glyphs) g.Opacity = 1; SetLightsActive(true, force: true); };
        panel.MouseLeave += (_, _) => { foreach (var g in glyphs) g.Opacity = 0; SetLightsActive(IsActive); };
        return panel;
    }

    private void SetLightsActive(bool active, bool force = false)
    {
        foreach (var (dot, color) in _lights)
            dot.Fill = active || force ? new SolidColorBrush(color) : (_dark ? B(0xFF4D4D4F) : B(0xFFCFCFD1));
    }

    // ---------------- Sidebar ----------------

    private FrameworkElement BuildSearch()
    {
        var box = new Border { Background = _searchBg, CornerRadius = new CornerRadius(15), Height = 30, Margin = new Thickness(12, 0, 12, 10) };
        var grid = new Grid();
        var glass = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = _sub, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var hint = new TextBlock { Text = "Search", Foreground = _sub, Margin = new Thickness(29, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = _text;
        _search.CaretBrush = _text;
        _search.Margin = new Thickness(26, 0, 8, 0);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.FontSize = 13;
        _search.FocusVisualStyle = null;
        if (_search.Parent is Panel old) old.Children.Remove(_search);
        hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        grid.Children.Add(glass);
        grid.Children.Add(hint);
        grid.Children.Add(_search);
        box.Child = grid;
        return box;
    }

    private void BuildSidebar()
    {
        _sidebarList.Children.Clear();
        _sidebarItems.Clear();
        string query = _search.Text.Trim();
        if (query.Length == 0)
        {
            _sidebarList.Children.Add(AccountRow());
        }
        int group = -2;
        foreach (var pane in Panes.Where(p => p.Group >= 0 || query.Length > 0))
        {
            if (query.Length > 0 && !(pane.Title + " " + pane.Keywords).Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            if (query.Length == 0 && pane.Group != group && group != -2) _sidebarList.Children.Add(new Border { Height = 12 });
            group = pane.Group;
            var item = SidebarItem(pane);
            _sidebarItems[pane.Id] = item;
            _sidebarList.Children.Add(item);
        }
        if (_sidebarList.Children.Count == 0)
            _sidebarList.Children.Add(new TextBlock { Text = "No Results", Foreground = _sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) });
        HighlightSidebar();
    }

    private ImageSource? _accountPicture;
    private string? _accountName, _accountKind;

    private FrameworkElement AccountRow()
    {
        var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var panel = new DockPanel();
        var avatar = new Ellipse { Width = 38, Height = 38, Margin = new Thickness(0, 0, 10, 0) };
        _accountPicture ??= UserInfo.LoadAccountPicture(96);
        var picture = _accountPicture;
        avatar.Fill = picture != null ? new ImageBrush(picture) { Stretch = Stretch.UniformToFill } : B(0xFF8E8E93);
        panel.Children.Add(avatar);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = _accountName ??= WindowsSettings.FullUserName(), FontWeight = FontWeights.SemiBold, Foreground = _text, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = _accountKind ??= (WindowsSettings.IsMicrosoftAccount() ? "Microsoft Account" : "Local Account"), FontSize = 11, Foreground = _sub });
        panel.Children.Add(texts);
        row.Child = panel;
        row.MouseEnter += (_, _) => { if (_current != "users") row.Background = _hover; };
        row.MouseLeave += (_, _) => { if (_current != "users") row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => Navigate("users");
        return row;
    }

    private Border SidebarItem(PaneInfo pane)
    {
        var row = new Border { CornerRadius = new CornerRadius(9), Height = 30, Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 1) };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(IconTile(pane.Glyph, pane.Color, 22));
        panel.Children.Add(new TextBlock { Text = pane.Title, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = _text });
        row.Child = panel;
        row.Tag = pane.Id;
        row.MouseEnter += (_, _) => { if (!IsSelected(pane.Id)) row.Background = _hover; };
        row.MouseLeave += (_, _) => { if (!IsSelected(pane.Id)) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => Navigate(pane.Id);
        return row;
    }

    private bool IsSelected(string id) => SidebarOwner(_current) == id;

    private static string SidebarOwner(string id) => Info(id).Group >= 0 ? id : (id is "language" or "datetime" or "about" or "update" or "storage" or "login" ? "general" : id);

    private void HighlightSidebar()
    {
        string owner = SidebarOwner(_current);
        foreach (var (id, row) in _sidebarItems)
        {
            bool on = id == owner;
            row.Background = on ? _accentBrush : Brushes.Transparent;
            if (row.Child is StackPanel p && p.Children[1] is TextBlock t) t.Foreground = on ? Brushes.White : _text;
        }
    }

    /// <summary>
    /// The rounded colored square with a white symbol that macOS puts next to each pane, in the Liquid
    /// Glass style: a soft gradient, a glossy highlight over the top half and a bright rim.
    /// </summary>
    private static FrameworkElement IconTile(string glyph, uint color, double size)
    {
        var c = Rgb(color);
        bool black = color == 0x1C1C1E; // dark tiles get a lighter top, or they vanish on dark backgrounds
        var top = black ? Rgb(0x55555A) : Color.FromRgb((byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40));
        double radius = size * 0.27;
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(radius), Background = new LinearGradientBrush(top, c, 90) });
        var gloss = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        gloss.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0));
        gloss.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.5));
        gloss.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55));
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(radius), Background = gloss, BorderBrush = Rim(0x80FFFFFF, 0x1AFFFFFF), BorderThickness = new Thickness(0.75) });
        if (Controls.MacSymbols.Get(SymbolFor(glyph)) is { } shape)
        {
            grid.Children.Add(new System.Windows.Shapes.Path
            {
                Data = shape, Fill = Brushes.White, Stretch = Stretch.Uniform, Width = size * 0.62, Height = size * 0.62,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = false,
            });
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = IconFont, FontSize = size * 0.56, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return grid;
    }

    /// <summary>The SF Symbols-style symbol drawn for each icon (the panes name their icon by its Segoe glyph).</summary>
    private static string SymbolFor(string glyph) => glyph switch
    {
        "\uE701" => "wifi-high", "\uE702" => "bluetooth", "\uE774" => "globe", "\uEA8F" => "bell", "\uE767" => "speaker-high",
        "\uE708" => "moon", "\uE713" => "gear", "\uE790" => "circle-half", "\uE776" => "person-arms-spread", "\uE8A9" => "app-window",
        "\uE7F4" => "monitor", "\uE91B" => "image", "\uE83F" => "battery-full", "\uE7FC" => "game-controller", "\uE72E" => "lock",
        "\uEA18" => "hand-palm", "\uE716" => "users", "\uE765" => "keyboard", "\uE962" => "mouse", "\uE749" => "printer",
        "\uE946" => "info", "\uE895" => "arrows-clockwise", "\uEDA2" => "hard-drives", "\uE768" => "list-bullets", "\uE787" => "calendar",
        "\uE7E8" => "power", "\uE7F8" => "laptop", "\uE839" => "network", "\uE720" => "microphone", "\uE722" => "camera",
        "\uE81D" => "navigation-arrow", "\uE77B" => "address-book", "\uE8B7" => "folder", "\uE74D" => "trash", "\uE71E" => "magnifying-glass-plus",
        "\uE7AD" => "sparkle", "\uE7F6" => "headphones", "\uE8EA" => "device-mobile", "\uE992" => "speaker-simple-x", "\uE995" => "speaker-high",
        "\uE706" => "sun",
        _ => "",
    };

    // ---------------- Navigation ----------------

    private void Navigate(string id, bool record = true)
    {
        if (Panes.All(p => p.Id != id)) id = "general";
        if (record && id != _current)
        {
            _back.Push(_current);
            _forward.Clear();
        }
        _current = id;
        _lastPane = id;
        BuildPage();
    }

    private void GoHistory(bool back)
    {
        var from = back ? _back : _forward;
        var to = back ? _forward : _back;
        if (from.Count == 0) return;
        to.Push(_current);
        _current = from.Pop();
        _lastPane = _current;
        BuildPage();
    }

    private void BuildPage()
    {
        _pageToken++;
        _body.Children.Clear();
        _title.Text = Info(_current).Title;
        _backButton.Foreground = _back.Count > 0 ? _text : _sub;
        _backButton.Opacity = _back.Count > 0 ? 1 : 0.45;
        _forwardButton.Foreground = _forward.Count > 0 ? _text : _sub;
        _forwardButton.Opacity = _forward.Count > 0 ? 1 : 0.45;
        _scroll.ScrollToTop();
        HighlightSidebar();
        try
        {
            switch (_current)
            {
                case "wifi": WifiPane(); break;
                case "bluetooth": BluetoothPane(); break;
                case "network": NetworkPane(); break;
                case "notifications": NotificationsPane(); break;
                case "sound": SoundPane(); break;
                case "focus": FocusPane(); break;
                case "appearance": AppearancePane(); break;
                case "accessibility": AccessibilityPane(); break;
                case "dock": DockPane(); break;
                case "displays": DisplaysPane(); break;
                case "wallpaper": WallpaperPane(); break;
                case "battery": BatteryPane(); break;
                case "performance": PerformancePane(); break;
                case "lock": LockPane(); break;
                case "privacy": PrivacyPane(); break;
                case "users": UsersPane(); break;
                case "keyboard": KeyboardPane(); break;
                case "mouse": MousePane(); break;
                case "printers": PrintersPane(); break;
                case "about": AboutPane(); break;
                case "update": UpdatePane(); break;
                case "storage": StoragePane(); break;
                case "login": LoginItemsPane(); break;
                case "language": LanguagePane(); break;
                case "datetime": DateTimePane(); break;
                default: GeneralPane(); break;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("SystemSettings." + _current, ex);
            Note("This pane could not be loaded.");
        }
        MacMotion.Reveal(_body, 6, 170);
    }

    /// <summary>Work off the UI thread, then <paramref name="then"/> back on it (skipped if the user moved to another pane).</summary>
    private void Later<T>(Func<T> work, Action<T> then)
    {
        int token = _pageToken;
        Task.Run(() =>
        {
            T result;
            try { result = work(); }
            catch (Exception ex) { CrashLog.Write("SystemSettings.Later", ex); return; }
            Dispatcher.BeginInvoke(() =>
            {
                if (token != _pageToken) return;
                try { then(result); }
                catch (Exception ex) { CrashLog.Write("SystemSettings.Later.then", ex); }
            });
        });
    }

    private static void InBackground(Action work) => Task.Run(() =>
    {
        try { work(); }
        catch (Exception ex) { CrashLog.Write("SystemSettings.Background", ex); }
    });

    private void ApplyDock(Action<DockConfig> change)
    {
        if (_config == null) return;
        change(_config);
        _config.Normalize();
        _changed?.Invoke();
    }

    // ---------------- Building blocks ----------------

    private sealed class Group
    {
        private readonly StackPanel _rows = new();
        private readonly Brush _separator;
        public Border Box { get; }

        public Group(Brush background, Brush border, Brush separator)
        {
            _separator = separator;
            Box = new Border { Background = background, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 14), Child = _rows };
        }

        public T Add<T>(T row) where T : UIElement
        {
            if (_rows.Children.Count > 0) _rows.Children.Add(new Border { Height = 1, Background = _separator, Margin = new Thickness(12, 0, 12, 0) });
            _rows.Children.Add(row);
            return row;
        }

        public void Clear() => _rows.Children.Clear();
        public int Count => _rows.Children.Count;
    }

    private Group NewGroup(StackPanel? into = null)
    {
        var g = new Group(_groupBg, _groupBorder, _separator);
        (into ?? _body).Children.Add(g.Box);
        return g;
    }

    private void Header(string text) =>
        _body.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Foreground = _text, Margin = new Thickness(4, 4, 0, 8) });

    private void Note(string text) =>
        _body.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = _sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, -8, 4, 14) });

    /// <summary>The big icon, title and explanation centered at the top of a pane (macOS 26 style).</summary>
    private void Hero(string glyph, uint color, string title, string text)
    {
        var g = NewGroup();
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18), HorizontalAlignment = HorizontalAlignment.Center };
        var tile = IconTile(glyph, color, 58);
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(tile);
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = _text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = _sub, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420, Margin = new Thickness(0, 4, 0, 0) });
        g.Add(panel);
    }
    /// <summary>A settings row: optional icon, label (and smaller line under it), control on the right.</summary>
    private FrameworkElement Row(string label, UIElement? control = null, string? sub = null, string? glyph = null, uint glyphColor = 0x8E8E93,
        Action? click = null, bool chevron = false, Brush? labelBrush = null)
    {
        var row = new Border { Padding = new Thickness(12, 0, 12, 0), MinHeight = sub == null ? 38 : 46, Background = Brushes.Transparent };
        var dock = new DockPanel { LastChildFill = true };
        if (glyph != null)
        {
            var tile = IconTile(glyph, glyphColor, 22);
            tile.Margin = new Thickness(0, 0, 10, 0);
            tile.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tile, Dock.Left);
            dock.Children.Add(tile);
        }
        if (chevron)
        {
            var arrow = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 10, Foreground = _sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(arrow, Dock.Right);
            dock.Children.Add(arrow);
        }
        if (control != null)
        {
            if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(12, 6, 0, 6); }
            DockPanel.SetDock(control, Dock.Right);
            dock.Children.Add(control);
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 7, 0, 7) };
        texts.Children.Add(new TextBlock { Text = label, Foreground = labelBrush ?? _text, TextTrimming = TextTrimming.CharacterEllipsis });
        if (sub != null) texts.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = _sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        dock.Children.Add(texts);
        row.Child = dock;
        if (click != null)
        {
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => row.Background = _hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && IsInside(d, control)) return; click(); };
        }
        return row;
    }

    private static bool IsInside(DependencyObject d, UIElement? container)
    {
        if (container == null) return false;
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x == container) return true;
        return false;
    }

    private TextBlock Value(string text) => new() { Text = text, Foreground = _sub, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340, TextAlignment = TextAlignment.Right };

    /// <summary>A capsule push button (macOS 26), accent-filled when it is the main action.</summary>
    private FrameworkElement Push(string text, Action click, bool primary = false)
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(13), Padding = new Thickness(14, 4, 14, 5), Cursor = Cursors.Hand,
            Background = primary ? _accentBrush : _controlBg, BorderBrush = primary ? null : _controlBorder, BorderThickness = new Thickness(primary ? 0 : 0.5),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = _dark ? 0.3 : 0.12 },
            Child = new TextBlock { Text = text, Foreground = primary ? Brushes.White : _text, FontSize = 13 },
        };
        button.MouseLeftButtonDown += (_, e) => { e.Handled = true; button.Opacity = 0.7; };
        button.MouseLeave += (_, _) => button.Opacity = 1;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (button.Opacity < 1) { button.Opacity = 1; click(); } };
        return button;
    }

    private MacSwitch Switch(bool on, Action<bool> changed) => new(on, changed, _accentBrush, _switchOff);

    /// <summary>A switch for a Windows setting: the change runs off the UI thread (Windows may take a while to tell every app).</summary>
    private MacSwitch SysSwitch(bool on, Action<bool> set) => Switch(on, v => InBackground(() => set(v)));

    private MacSlider Slider(double value, Action<double> changed, string? left = null, string? right = null, bool leftGlyph = false) =>
        new(value, changed, _accentBrush, _track, _sub, left, right, leftGlyph, _dark);

    /// <summary>A macOS pop-up button (a capsule with up/down chevrons); a click shows the choices.</summary>
    private FrameworkElement Popup(IList<string> options, int selected, Action<int> changed)
    {
        var text = new TextBlock { Text = selected >= 0 && selected < options.Count ? options[selected] : "", Foreground = _text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        var chevrons = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chevrons.Children.Add(new TextBlock { Text = "\uE70E", FontFamily = IconFont, FontSize = 6.5, Foreground = _sub, HorizontalAlignment = HorizontalAlignment.Center });
        chevrons.Children.Add(new TextBlock { Text = "\uE70D", FontFamily = IconFont, FontSize = 6.5, Foreground = _sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) });
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(text);
        panel.Children.Add(chevrons);
        var button = new Border
        {
            CornerRadius = new CornerRadius(13), Padding = new Thickness(12, 3, 9, 4), Cursor = Cursors.Hand, Child = panel,
            Background = _controlBg, BorderBrush = _controlBorder, BorderThickness = new Thickness(0.5),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = _dark ? 0.3 : 0.12 },
        };
        int current = selected;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(button, options, current, i =>
            {
                current = i;
                text.Text = options[i];
                changed(i);
            });
        };
        return button;
    }

    /// <summary>The choices of a pop-up button, on a pane of glass (the desktop blurred behind it).</summary>
    private void ShowMenu(FrameworkElement target, IList<string> options, int selected, Action<int> picked)
    {
        var list = new StackPanel { Margin = new Thickness(6) };
        var popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Bottom, VerticalOffset = 4, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var check = new TextBlock { Text = i == selected ? "\uE73E" : "", FontFamily = IconFont, FontSize = 10, Width = 18, VerticalAlignment = VerticalAlignment.Center, Foreground = _text };
            var label = new TextBlock { Text = options[i], Foreground = _text, VerticalAlignment = VerticalAlignment.Center };
            var item = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 4, 16, 5), Background = Brushes.Transparent };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(check);
            row.Children.Add(label);
            item.Child = row;
            item.MouseEnter += (_, _) => { item.Background = _accentBrush; label.Foreground = check.Foreground = Brushes.White; };
            item.MouseLeave += (_, _) => { item.Background = Brushes.Transparent; label.Foreground = check.Foreground = _text; };
            item.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; if (index != selected) picked(index); };
            list.Children.Add(item);
        }
        const double radius = 12;
        var scroll = new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        MacMotion.SmoothScroll(scroll);
        var surface = new Border
        {
            Background = _menuBg, BorderBrush = _glassRim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Glass.Radius(radius)),
            MinWidth = Math.Max(150, target.ActualWidth), Child = scroll,
        };
        surface.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, TextFont);
        surface.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 13.0);
        scroll.Resources[typeof(ScrollBar)] = ThinScrollBar(_dark);
        popup.Child = surface;
        Glass.Attach(popup, surface, radius);
        popup.IsOpen = true;
        if (selected >= 0 && selected < list.Children.Count)
            popup.Opened += (_, _) => (list.Children[selected] as FrameworkElement)?.BringIntoView();
    }
    private TextBlock Dot(bool on) => new() { Text = "●", FontSize = 9, Foreground = on ? B(0xFF34C759) : B(0xFF8E8E93), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

    private static readonly string[] MinuteNames = { "1 minute", "2 minutes", "5 minutes", "10 minutes", "15 minutes", "20 minutes", "30 minutes", "1 hour", "2 hours", "3 hours", "Never" };
    private static readonly int[] MinuteValues = { 1, 2, 5, 10, 15, 20, 30, 60, 120, 180, 0 };

    private static int NearestMinutes(int minutes)
    {
        if (minutes <= 0) return MinuteValues.Length - 1;
        int best = 0;
        for (int i = 0; i < MinuteValues.Length - 1; i++)
            if (Math.Abs(MinuteValues[i] - minutes) < Math.Abs(MinuteValues[best] - minutes)) best = i;
        return best;
    }

    private static void Run(string file, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
        catch (Exception ex) { CrashLog.Write("SystemSettings.Run " + file, ex); }
    }

    // ---------------- Wi-Fi ----------------

    private void WifiPane()
    {
        var top = NewGroup();
        var wifiSwitch = Switch(false, on => InBackground(() => WindowsSettings.SetRadio("WiFi", on)));
        wifiSwitch.IsEnabled = false;
        top.Add(Row("Wi-Fi", wifiSwitch, glyph: "", glyphColor: 0x0A84FF));
        var currentRow = new StackPanel();
        top.Add(currentRow);
        Later(WindowsSettings.Radios, radios =>
        {
            if (radios.TryGetValue("WiFi", out bool on)) { wifiSwitch.IsOn = on; wifiSwitch.IsEnabled = true; }
        });

        Header("Known Networks");
        var known = NewGroup();
        known.Add(Row("Loading…", labelBrush: _sub));
        Header("Other Networks");
        var other = NewGroup();
        other.Add(Row("Searching…", labelBrush: _sub));

        Later(() => (SystemStatus.GetNetwork(), SystemStatus.GetWifiNetworks(), WindowsSettings.KnownWifi()), data =>
        {
            var (state, networks, saved) = data;
            currentRow.Children.Clear();
            if (state.Kind == NetworkKind.Wifi)
            {
                var name = new StackPanel { Orientation = Orientation.Horizontal };
                currentRow.Children.Add(Row(state.Name ?? "Wi-Fi", Push("Details…", () => Navigate("network")), sub: $"Connected · signal {state.SignalPercent}%"));
            }
            else if (!state.HasWifiAdapter) currentRow.Children.Add(Row("No Wi-Fi hardware found", labelBrush: _sub));

            known.Clear();
            foreach (var n in saved)
            {
                string network = n;
                known.Add(Row(n, Push("Forget…", () =>
                {
                    if (MessageBox.Show($"Forget Wi-Fi network \"{network}\"?\n\nThis PC will no longer join it automatically.", "Wi-Fi",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                    InBackground(() => WindowsSettings.ForgetWifi(network));
                    Dispatcher.BeginInvoke(() => Navigate("wifi", record: false));
                })));
            }
            if (known.Count == 0) known.Add(Row("No saved networks", labelBrush: _sub));

            other.Clear();
            foreach (var net in networks.Where(n => !n.Connected))
            {
                var network = net;
                var holder = new StackPanel();
                holder.Children.Add(Row(net.Ssid, Push("Connect", () => JoinWifi(network, holder)), sub: (net.Secured ? "Secured · " : "Open · ") + $"signal {net.Signal}%"));
                other.Add(holder);
            }
            if (other.Count == 0) other.Add(Row("No other networks found", labelBrush: _sub));
        });
    }

    private void JoinWifi(WifiNetwork network, StackPanel holder)
    {
        if (!network.Secured || network.Profile != null)
        {
            Later(() => SystemStatus.ConnectWifi(network), ok => Navigate("wifi", record: false));
            return;
        }
        // Password, right in the list (macOS asks in a sheet).
        if (holder.Children.Count > 1) return;
        var password = new PasswordBox { Width = 200, Padding = new Thickness(6, 3, 6, 3), BorderBrush = _controlBorder, Background = _controlBg, Foreground = _text, CaretBrush = _text };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(password);
        var join = Push("Join", () =>
        {
            string secret = password.Password;
            Later(() => WindowsSettings.JoinWifi(network.Ssid, secret), _ => Navigate("wifi", record: false));
        }, primary: true);
        join.Margin = new Thickness(8, 0, 0, 0);
        panel.Children.Add(join);
        holder.Children.Add(Row("Password", panel));
        password.Focus();
    }

    // ---------------- Bluetooth ----------------

    private void BluetoothPane()
    {
        Hero("", 0x0A84FF, "Bluetooth", "Connect to accessories you can use for activities such as streaming music, typing and gaming.");
        var top = NewGroup();
        bool has = WindowsSettings.HasBluetooth();
        var toggle = Switch(has, on => InBackground(() => WindowsSettings.SetRadio("Bluetooth", on)));
        toggle.IsEnabled = false;
        top.Add(Row("Bluetooth", toggle, sub: has ? null : "No Bluetooth found (or it is off)"));
        Later(WindowsSettings.Radios, radios =>
        {
            if (radios.TryGetValue("Bluetooth", out bool on)) { toggle.IsOn = on; toggle.IsEnabled = true; }
        });

        Header("My Devices");
        var devices = NewGroup();
        devices.Add(Row("Loading…", labelBrush: _sub));
        Later(WindowsSettings.BluetoothDevices, list =>
        {
            devices.Clear();
            foreach (var d in list)
            {
                var device = d;
                devices.Add(Row(d.Name, Push("Forget…", () =>
                {
                    if (MessageBox.Show($"Forget \"{device.Name}\"?\n\nYou will need to pair it again to use it.", "Bluetooth",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                    Later(() => { WindowsSettings.ForgetBluetooth(device.Address); return 0; }, _ => Navigate("bluetooth", record: false));
                }), sub: d.Connected ? "Connected" : "Not Connected", glyph: DeviceGlyph(d.Class), glyphColor: 0x0A84FF));
            }
            if (devices.Count == 0) devices.Add(Row("No devices", labelBrush: _sub));
        });

        var pair = NewGroup();
        pair.Add(Row("Pair a new device", Push("Pair…", () => Run(Path.Combine(Environment.SystemDirectory, "DevicePairingWizard.exe"))),
            sub: "Make sure the device is on and ready to pair."));
    }

    private static string DeviceGlyph(uint cls) => ((cls >> 8) & 0x1F) switch
    {
        4 => "", // audio / headphones
        5 => (cls & 0x40) != 0 ? "" : "", // keyboard / mouse
        2 => "", // phone
        1 => "", // computer
        _ => "",
    };

    // ---------------- Network ----------------

    private void NetworkPane()
    {
        var g = NewGroup();
        g.Add(Row("Loading…", labelBrush: _sub));
        Later(() =>
        {
            var rows = new List<(string Name, string Sub, bool Up, string Glyph)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                bool up = nic.OperationalStatus == OperationalStatus.Up;
                string ip = "";
                try { ip = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? ""; } catch { }
                bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                string sub = up ? (ip.Length > 0 ? $"Connected · {ip}" : "Connected") : "Not Connected";
                rows.Add((nic.Name, sub + " · " + nic.Description, up, wifi ? "" : ""));
            }
            return rows.OrderByDescending(r => r.Up).ToList();
        }, rows =>
        {
            g.Clear();
            foreach (var r in rows)
            {
                var status = new StackPanel { Orientation = Orientation.Horizontal };
                status.Children.Add(Dot(r.Up));
                status.Children.Add(Value(r.Up ? "Connected" : "Off"));
                g.Add(Row(r.Name, status, sub: r.Sub, glyph: r.Glyph, glyphColor: r.Up ? 0x0A84FFu : 0x8E8E93u,
                    click: r.Glyph == "" ? () => Navigate("wifi") : null));
            }
        });
        Header("Firewall");
        var fw = NewGroup();
        fw.Add(Row("Windows Defender Firewall", Push("Open…", () => Run("windowsdefender://network/")), sub: "Protects this PC from unwanted incoming connections."));
    }

    // ---------------- Notifications & Focus ----------------

    private void NotificationsPane()
    {
        var g = NewGroup();
        g.Add(Row("Allow notifications", SysSwitch(WindowsSettings.NotificationsOn, on => WindowsSettings.NotificationsOn = on)));
        g.Add(Row("Show previews on Lock Screen", SysSwitch(WindowsSettings.NotificationsOnLockScreen, on => WindowsSettings.NotificationsOnLockScreen = on)));
        g.Add(Row("Play sound for notifications", SysSwitch(WindowsSettings.NotificationSounds, on => WindowsSettings.NotificationSounds = on)));
        Header("Application Notifications");
        var apps = NewGroup();
        foreach (var app in WindowsSettings.NotifyingApps())
        {
            var a = app;
            apps.Add(Row(a.Name, SysSwitch(a.Enabled, on => WindowsSettings.SetAppNotifications(a.Id, on)), glyph: "", glyphColor: 0xFF3B30));
        }
        if (apps.Count == 0) apps.Add(Row("No apps have sent notifications yet", labelBrush: _sub));
    }

    private void FocusPane()
    {
        Hero("", 0x5E5CE6, "Focus", "Silence notifications so you can concentrate. Apps can't show banners or play sounds while Do Not Disturb is on.");
        var g = NewGroup();
        g.Add(Row("Do Not Disturb", SysSwitch(!WindowsSettings.NotificationsOn, on => WindowsSettings.NotificationsOn = !on), glyph: "", glyphColor: 0x5E5CE6));
        var n = NewGroup();
        n.Add(Row("Notifications", click: () => Navigate("notifications"), chevron: true, glyph: "", glyphColor: 0xFF3B30));
    }

    // ---------------- Sound ----------------

    private void SoundPane()
    {
        var volume = new AudioVolume();
        Header("Output");
        var outputs = NewGroup();
        foreach (var d in AudioDevices.List(true))
        {
            var device = d;
            outputs.Add(Row(d.Name, d.IsDefault ? Value("✓") : null, glyph: "", glyphColor: 0xFF2D55,
                click: () => { AudioDevices.SetDefault(device.Id); Navigate("sound", record: false); }));
        }
        if (outputs.Count == 0) outputs.Add(Row("No output devices found", labelBrush: _sub));
        var level = NewGroup();
        var slider = Slider(volume.Level ?? 0.5, v => volume.Level = (float)v, "", "", leftGlyph: true);
        slider.Width = 260;
        level.Add(Row("Output volume", slider));
        level.Add(Row("Mute", Switch(volume.Muted, on => volume.Muted = on)));

        Header("Input");
        var inputs = NewGroup();
        foreach (var d in AudioDevices.List(false))
        {
            var device = d;
            inputs.Add(Row(d.Name, d.IsDefault ? Value("✓") : null, glyph: "", glyphColor: 0xFF2D55,
                click: () => { AudioDevices.SetDefault(device.Id); Navigate("sound", record: false); }));
        }
        if (inputs.Count == 0) inputs.Add(Row("No input devices found", labelBrush: _sub));
        if (AudioDevices.InputLevel is float input)
        {
            var mic = NewGroup();
            var micSlider = Slider(input, v => AudioDevices.InputLevel = (float)v, "", "", leftGlyph: true);
            micSlider.Width = 260;
            mic.Add(Row("Input volume", micSlider));
        }
    }

    // ---------------- General ----------------

    private void GeneralPane()
    {
        Hero("", 0x8E8E93, "General", "Manage your overall setup and preferences for this PC, such as software updates, storage, login items and more.");
        var a = NewGroup();
        a.Add(Row("About", click: () => Navigate("about"), chevron: true, glyph: "", glyphColor: 0x8E8E93));
        a.Add(Row("Software Update", click: () => Navigate("update"), chevron: true, glyph: "", glyphColor: 0x8E8E93));
        a.Add(Row("Storage", click: () => Navigate("storage"), chevron: true, glyph: "", glyphColor: 0x8E8E93));
        var b = NewGroup();
        b.Add(Row("Login Items", click: () => Navigate("login"), chevron: true, glyph: "", glyphColor: 0x8E8E93));
        var c = NewGroup();
        c.Add(Row("Language & Region", click: () => Navigate("language"), chevron: true, glyph: "", glyphColor: 0x0A84FF));
        c.Add(Row("Date & Time", click: () => Navigate("datetime"), chevron: true, glyph: "", glyphColor: 0x0A84FF));
        var d = NewGroup();
        d.Add(Row("Shut down this PC", Push("Shut Down…", () =>
        {
            if (MessageBox.Show("Are you sure you want to shut down your computer now?", "Shut Down", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                PowerActions.ShutDown();
        }), glyph: "", glyphColor: 0x8E8E93));
    }

    private void AboutPane()
    {
        var hero = NewGroup();
        var stack = new StackPanel { Margin = new Thickness(0, 22, 0, 20), HorizontalAlignment = HorizontalAlignment.Center };
        var logo = IconTile("", 0x0A84FF, 72);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(logo);
        stack.Children.Add(new TextBlock { Text = WindowsSettings.Model(), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = _text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
        stack.Children.Add(new TextBlock { Text = WindowsSettings.WindowsVersion(), Foreground = _sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        hero.Add(stack);

        var g = NewGroup();
        g.Add(Row("Name", Value(Environment.MachineName)));
        g.Add(Row("Processor", Value(WindowsSettings.Processor())));
        g.Add(Row("Memory", Value(WindowsSettings.Memory())));
        var graphics = g.Add(Row("Graphics", Value("…")));
        g.Add(Row("Windows", Value(WindowsSettings.WindowsVersion())));
        g.Add(Row("User", Value(WindowsSettings.FullUserName() + (WindowsSettings.IsAdmin() ? " (Administrator)" : ""))));
        Later(WindowsSettings.Graphics, text =>
        {
            if (graphics is Border { Child: DockPanel dock } && dock.Children.OfType<TextBlock>().FirstOrDefault(t => t.Text == "…") is { } value) value.Text = text;
        });

        Header("Displays");
        var displays = NewGroup();
        foreach (var d in WindowsSettings.Displays())
            displays.Add(Row(d.Name, Value($"{d.Current.Width} × {d.Current.Height} · {d.Current.Frequency} Hz"), sub: d.Primary ? "Main display" : null, glyph: "", glyphColor: 0x0A84FF));
        Header("Storage");
        var storage = NewGroup();
        storage.Add(Row("Storage", click: () => Navigate("storage"), chevron: true, glyph: "", glyphColor: 0x8E8E93));
        var report = NewGroup();
        report.Add(Row("System Report", Push("System Report…", () => Run("msinfo32.exe"))));

        Header("MacDock");
        var app = NewGroup();
        app.Add(Row("Version", Value($"{Installer.Version} (built {BuildDate():d MMM yyyy, HH:mm})")));
        var copied = Value("");
        app.Add(Row("Diagnostics", Push("Copy Diagnostics", () =>
        {
            try { Clipboard.SetText(Diagnostics()); copied.Text = "Copied"; }
            catch { copied.Text = "Couldn't copy"; }
        }), sub: "Copies this PC's details and MacDock's error log, to send when something looks wrong."));
        app.Add(Row("", copied));
    }

    private static DateTime BuildDate()
    {
        try { return File.GetLastWriteTime(Environment.ProcessPath!); } catch { return DateTime.MinValue; }
    }

    /// <summary>Everything that helps to fix a problem seen on another PC: Windows, screens, scaling, MacDock's log.</summary>
    private static string Diagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"MacDock {Installer.Version}, built {BuildDate():yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Windows: {WindowsSettings.WindowsVersion()}");
        sb.AppendLine($"PC: {WindowsSettings.Model()} | {WindowsSettings.Processor()} | {WindowsSettings.Memory()} | {WindowsSettings.Graphics()}");
        sb.AppendLine($"Theme: {(WindowsSettings.IsDark ? "dark" : "light")}, accent #{WindowsSettings.Accent:X6}, transparency {(WindowsSettings.Transparency ? "on" : "off")}");
        foreach (var d in WindowsSettings.Displays())
            sb.AppendLine($"Display: {d.Name} {d.Current.Width}x{d.Current.Height} @{d.Current.Frequency}Hz at {d.Current.PositionX},{d.Current.PositionY}{(d.Primary ? " (main)" : "")}");
        sb.AppendLine($"System DPI scale: {VisualTreeHelper.GetDpi(Application.Current.MainWindow!).DpiScaleX * 100:0}%");
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "crash.log");
        if (File.Exists(log))
        {
            var lines = File.ReadAllLines(log);
            sb.AppendLine().AppendLine("--- crash.log (last 60 lines) ---");
            foreach (var line in lines.Skip(Math.Max(0, lines.Length - 60))) sb.AppendLine(line);
        }
        else sb.AppendLine("No errors logged.");
        return sb.ToString();
    }

    private void UpdatePane()
    {
        Hero("", 0x8E8E93, "Software Update", WindowsSettings.WindowsVersion());
        var g = NewGroup();
        var status = Value("");
        g.Add(Row("Check for updates", Push("Check Now", () =>
        {
            status.Text = "Checking…";
            Later(() => WindowsSettings.Run("UsoClient.exe", "StartScan", 8000), _ => status.Text = "Windows will download and install updates in the background.");
        }), sub: "Windows Update keeps this PC up to date."));
        g.Add(Row("", status));
        g.Add(Row("Update history and options", Push("More Info…", () => WindowsSettingsRedirect.OpenInWindows("ms-settings:windowsupdate"))));
    }

    private void StoragePane()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                double total = drive.TotalSize, free = drive.AvailableFreeSpace, used = total - free;
                var g = NewGroup();
                var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
                var head = new DockPanel();
                var size = new TextBlock { Text = $"{Gb(used)} of {Gb(total)} used", Foreground = _sub };
                DockPanel.SetDock(size, Dock.Right);
                head.Children.Add(size);
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                head.Children.Add(new TextBlock { Text = $"{label} ({drive.Name.TrimEnd('\\')})", FontWeight = FontWeights.SemiBold, Foreground = _text });
                panel.Children.Add(head);
                var bar = new Grid { Height = 8, Margin = new Thickness(0, 10, 0, 0) };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, used / total), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, free / total), GridUnitType.Star) });
                var usedBar = new Border { Background = used / total > 0.9 ? B(0xFFFF3B30) : _accentBrush, CornerRadius = new CornerRadius(4, 0, 0, 4) };
                var freeBar = new Border { Background = _track, CornerRadius = new CornerRadius(0, 4, 4, 0) };
                Grid.SetColumn(freeBar, 1);
                bar.Children.Add(usedBar);
                bar.Children.Add(freeBar);
                panel.Children.Add(bar);
                panel.Children.Add(new TextBlock { Text = $"{Gb(free)} available", FontSize = 11.5, Foreground = _sub, Margin = new Thickness(0, 6, 0, 0) });
                g.Add(panel);
            }
            catch { /* drive went away */ }
        }
        var tools = NewGroup();
        tools.Add(Row("Free up space", Push("Disk Clean-up…", () => Run("cleanmgr.exe")), sub: "Removes temporary files, caches and old updates."));
    }

    private static string Gb(double bytes) => bytes >= 1e12 ? $"{bytes / 1e12:0.##} TB" : $"{bytes / 1e9:0.#} GB";

    private void LoginItemsPane()
    {
        Header("Open at Login");
        var g = NewGroup();
        foreach (var item in WindowsSettings.LoginItems())
        {
            var i = item;
            g.Add(Row(Path.GetFileNameWithoutExtension(i.Name), SysSwitch(i.Enabled, on => WindowsSettings.SetLoginItem(i, on)), sub: i.FromFolder ? "Startup folder" : "Application", glyph: "", glyphColor: 0x8E8E93));
        }
        if (g.Count == 0) g.Add(Row("No login items", labelBrush: _sub));
        var add = NewGroup();
        add.Add(Row("Add an app", Push("Add…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Open at Login" };
            if (dialog.ShowDialog(this) == true) { WindowsSettings.AddLoginItem(dialog.FileName); Navigate("login", record: false); }
        })));
        Note("These items open automatically when you log in.");
    }

    private void LanguagePane()
    {
        var g = NewGroup();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        g.Add(Row("Region", Value(System.Globalization.RegionInfo.CurrentRegion.DisplayName)));
        g.Add(Row("Format", Value(culture.DisplayName)));
        g.Add(Row("Date format", Value(DateTime.Now.ToString(culture.DateTimeFormat.ShortDatePattern))));
        g.Add(Row("24-hour time", SysSwitch(WindowsSettings.Clock24, on => WindowsSettings.Clock24 = on)));
        Header("Input Sources");
        var inputs = NewGroup();
        foreach (var layout in SystemStatus.GetInstalledLayouts())
            inputs.Add(Row(layout.Name, Value(layout.Code), glyph: "", glyphColor: 0x8E8E93));
        inputs.Add(Row("Add or remove languages", Push("Edit…", () => WindowsSettingsRedirect.OpenInWindows("ms-settings:regionlanguage"))));
    }

    private void DateTimePane()
    {
        var g = NewGroup();
        var now = Value(DateTime.Now.ToString("F"));
        g.Add(Row("Date and time", now));
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int token = _pageToken;
        clock.Tick += (_, _) => { if (token != _pageToken) clock.Stop(); else now.Text = DateTime.Now.ToString("F"); };
        clock.Start();
        g.Add(Row("24-hour time", SysSwitch(WindowsSettings.Clock24, on => WindowsSettings.Clock24 = on)));
        var zones = TimeZoneInfo.GetSystemTimeZones();
        var names = zones.Select(z => z.DisplayName).ToList();
        int current = zones.ToList().FindIndex(z => z.Id == TimeZoneInfo.Local.Id);
        g.Add(Row("Time zone", Popup(names, current, i => InBackground(() => WindowsSettings.SetTimeZone(zones[i].Id)))));
    }

    // ---------------- Appearance ----------------

    private static readonly (string Name, uint Rgb)[] AccentColors =
    {
        ("Blue", 0x007AFF), ("Purple", 0xA550A7), ("Pink", 0xF74F9E), ("Red", 0xFF5257),
        ("Orange", 0xF7821B), ("Yellow", 0xFFC600), ("Green", 0x62BA46), ("Graphite", 0x8C8C8C),
    };

    private void AppearancePane()
    {
        var g = NewGroup();
        var modes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (name, dark) in new[] { ("Light", false), ("Dark", true) })
        {
            bool isDark = dark;
            var tile = ModeTile(name, dark, _dark == dark);
            tile.MouseLeftButtonUp += (_, _) =>
            {
                if (isDark == _dark) return;
                Later(() => { WindowsSettings.SetDark(isDark); return 0; }, _ => Retheme());
            };
            modes.Children.Add(tile);
        }
        g.Add(Row("Appearance", modes));

        var colors = new StackPanel { Orientation = Orientation.Horizontal };
        int accent = WindowsSettings.Accent;
        foreach (var (name, rgb) in AccentColors)
        {
            uint value = rgb;
            bool chosen = Math.Abs(((accent >> 16) & 0xFF) - (int)((rgb >> 16) & 0xFF)) + Math.Abs(((accent >> 8) & 0xFF) - (int)((rgb >> 8) & 0xFF)) + Math.Abs((accent & 0xFF) - (int)(rgb & 0xFF)) < 24;
            var dot = new Grid { Width = 22, Height = 22, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, ToolTip = name };
            dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(Rgb(rgb)), Stroke = B(0x26000000), StrokeThickness = 0.5, Width = 16, Height = 16 });
            if (chosen) dot.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White });
            dot.MouseLeftButtonUp += (_, _) => Later(() => { WindowsSettings.SetAccent((int)value); return 0; }, _ => Retheme());
            colors.Children.Add(dot);
        }
        g.Add(Row("Accent colour", colors));
        g.Add(Row("Show accent colour on title bars", SysSwitch(WindowsSettings.AccentOnTitleBars, on => WindowsSettings.AccentOnTitleBars = on)));
        var t = NewGroup();
        t.Add(Row("Transparency effects", SysSwitch(WindowsSettings.Transparency, on => WindowsSettings.Transparency = on), sub: "Lets the desktop show through Windows' own menus and panels."));
    }

    private FrameworkElement ModeTile(string name, bool dark, bool selected)
    {
        var stack = new StackPanel { Margin = new Thickness(8, 4, 0, 4), Cursor = Cursors.Hand };
        var screen = new Border
        {
            Width = 68, Height = 46, CornerRadius = new CornerRadius(6),
            BorderBrush = selected ? _accentBrush : _controlBorder, BorderThickness = new Thickness(selected ? 2.5 : 1),
            Background = dark ? new LinearGradientBrush(Rgb(0x2C3E66), Rgb(0x10131C), 90) : new LinearGradientBrush(Rgb(0x8EC5FF), Rgb(0xE8F1FF), 90),
        };
        var window = new Border
        {
            Width = 42, Height = 26, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 6, 0, 0),
            Background = dark ? B(0xFF2B2B2D) : B(0xFFF7F7F7), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3) };
        foreach (uint c in new uint[] { 0xFF5F57, 0xFEBC2E, 0x28C840 }) bar.Children.Add(new Ellipse { Width = 3.5, Height = 3.5, Fill = new SolidColorBrush(Rgb(c)), Margin = new Thickness(0, 0, 1.5, 0) });
        window.Child = bar;
        screen.Child = window;
        stack.Children.Add(screen);
        stack.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = selected ? _text : _sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal });
        return stack;
    }

    /// <summary>The theme or accent changed: redraw the window in the new colors, on the same pane.</summary>
    private void Retheme()
    {
        Dispatcher.BeginInvoke(() =>
        {
            BuildChrome();
            BuildPage();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------------- Accessibility ----------------

    private void AccessibilityPane()
    {
        Hero("", 0x0A84FF, "Accessibility", "Personalise this PC in ways that work for you: zoom in, have the screen read aloud, reduce motion and more.");
        Header("Vision");
        var v = NewGroup();
        v.Add(Row("Zoom", SysSwitch(WindowsSettings.IsRunning("Magnify"), on => WindowsSettings.SetRunning("Magnify", on)), sub: "Magnifies the screen (Windows Magnifier).", glyph: "", glyphColor: 0x0A84FF));
        v.Add(Row("Narrator", SysSwitch(WindowsSettings.IsRunning("Narrator"), on => WindowsSettings.SetRunning("Narrator", on)), sub: "Reads aloud what is on the screen.", glyph: "", glyphColor: 0x1C1C1E));
        v.Add(Row("Reduce motion", SysSwitch(WindowsSettings.ReduceMotion, on => WindowsSettings.ReduceMotion = on), sub: "Turns off window and control animations.", glyph: "", glyphColor: 0x0A84FF));
        v.Add(Row("Reduce transparency", SysSwitch(!WindowsSettings.Transparency, on => WindowsSettings.Transparency = !on), glyph: "", glyphColor: 0x0A84FF));
        Header("Motor");
        var m = NewGroup();
        m.Add(Row("Sticky Keys", SysSwitch(WindowsSettings.StickyKeysOn, on => WindowsSettings.StickyKeysOn = on), sub: "Press keyboard shortcuts one key at a time.", glyph: "", glyphColor: 0x0A84FF));
    }

    // ---------------- Desktop & Dock (MacDock itself) ----------------

    private void DockPane()
    {
        var c = _config;
        if (c == null) { Note("Not available."); return; }
        Header("Dock");
        var dock = NewGroup();
        var size = Slider((c.IconSize - DockConfig.MinIconSize) / (DockConfig.MaxIconSize - DockConfig.MinIconSize),
            v => ApplyDock(x =>
            {
                double old = x.IconSize;
                x.IconSize = Math.Round(DockConfig.MinIconSize + v * (DockConfig.MaxIconSize - DockConfig.MinIconSize));
                if (x.MagnifiedSize <= old) x.MagnifiedSize = x.IconSize; // magnification stays off
            }), "Small", "Large");
        size.Width = 240;
        dock.Add(Row("Size", size));
        bool magnify = c.MagnifiedSize > c.IconSize;
        var magnifySlider = Slider(magnify ? (c.MagnifiedSize - c.IconSize) / Math.Max(1, DockConfig.MaxMagnifiedSize - c.IconSize) : 0.5,
            v => ApplyDock(x => x.MagnifiedSize = Math.Round(x.IconSize + Math.Max(0.05, v) * (DockConfig.MaxMagnifiedSize - x.IconSize))), "Small", "Large");
        magnifySlider.Width = 240;
        magnifySlider.IsEnabled = magnify;
        dock.Add(Row("Magnification", Switch(magnify, on =>
        {
            magnifySlider.IsEnabled = on;
            ApplyDock(x => x.MagnifiedSize = on ? Math.Max(x.IconSize * 2, x.IconSize + 16) : x.IconSize);
            if (on) magnifySlider.Value = (c.MagnifiedSize - c.IconSize) / Math.Max(1, DockConfig.MaxMagnifiedSize - c.IconSize);
        })));
        dock.Add(Row("", magnifySlider));
        dock.Add(Row("Minimise windows using", Popup(new[] { "Genie Effect", "Scale Effect" }, c.GenieEffect ? 0 : 1, i => ApplyDock(x => x.GenieEffect = i == 0))));
        dock.Add(Row("Automatically hide and show the Dock", Switch(c.AutoHide, on => ApplyDock(x => x.AutoHide = on))));
        dock.Add(Row("Show Start button in Dock", Switch(c.ShowStartButton, on => ApplyDock(x => x.ShowStartButton = on))));
        dock.Add(Row("Show Trash in Dock", Switch(c.ShowTrash, on => ApplyDock(x => x.ShowTrash = on))));

        var look = NewGroup();
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (string hex in new[] { "#FFFFFF", "#D6D6D6", "#7A7A7A", "#1E1E1E", "#000000", "#2F6FDB" })
        {
            string value = hex;
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool chosen = string.Equals(c.DockColor, hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Grid { Width = 22, Height = 22, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand };
            dot.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(color), Stroke = B(0x40000000), StrokeThickness = 0.5 });
            if (chosen) dot.Children.Add(new Ellipse { Width = 22, Height = 22, Stroke = _accentBrush, StrokeThickness = 2 });
            dot.MouseLeftButtonUp += (_, _) => { ApplyDock(x => x.DockColor = value); Navigate("dock", record: false); };
            swatches.Children.Add(dot);
        }
        look.Add(Row("Dock colour", swatches));
        var tint = Slider(c.DockOpacity, v => ApplyDock(x => x.DockOpacity = v), "Clear", "Solid");
        tint.Width = 240;
        look.Add(Row("Dock tint", tint));

        Header("Menu Bar & Windows");
        var windows = NewGroup();
        windows.Add(Row("Show macOS menu bar", Switch(c.ShowMenuBar, on => ApplyDock(x => x.ShowMenuBar = on)), sub: "App menus, Wi-Fi, sound and the clock across the top of the screen."));
        windows.Add(Row("macOS title bars on all apps", Switch(c.MacWindowButtons, on => ApplyDock(x => x.MacWindowButtons = on)), sub: "Red, yellow and green buttons; app menus move to the menu bar."));
        windows.Add(Row("macOS mouse pointers", Switch(c.MacCursors, on => ApplyDock(x => x.MacCursors = on))));
        windows.Add(Row("Hide the Windows taskbar", Switch(c.HideTaskbar, on => ApplyDock(x => x.HideTaskbar = on))));
        windows.Add(Row("Windows key opens Launchpad", Switch(c.WinKeyOpensStart, on => ApplyDock(x => x.WinKeyOpensStart = on))));

        Header("Finder");
        var finder = NewGroup();
        finder.Add(Row("Open folders in Finder", Switch(c.FinderOpensFolders, on => ApplyDock(x => x.FinderOpensFolders = on)), sub: "Instead of File Explorer: desktop, Win+E, \"show in folder\"…"));
        MacSwitch? blue = null;
        blue = Switch(c.BlueDesktopFolders || Finder.WindowsFolderIcons.IsApplied(), async on =>
        {
            blue!.IsEnabled = false;
            ApplyDock(x => x.BlueDesktopFolders = on);
            if (on != Finder.WindowsFolderIcons.IsApplied()) await Finder.WindowsFolderIcons.SetAsync(on);
            blue.IsOn = c.BlueDesktopFolders || Finder.WindowsFolderIcons.IsApplied();
            blue.IsEnabled = true;
        });
        finder.Add(Row("Blue macOS folders everywhere", blue));

        Header("MacDock");
        var app = NewGroup();
        app.Add(Row("Open MacDock at login", Switch(StartupService.IsEnabled, on => StartupService.SetEnabled(on))));
        app.Add(Row("Use these settings instead of Windows Settings", Switch(c.MacSystemSettings, on => ApplyDock(x => x.MacSystemSettings = on)),
            sub: "Windows Settings opens here, in System Settings."));
        app.Add(Row("Custom dock icons", Push("Open Folder…", () =>
        {
            CustomIcons.EnsureFolder();
            Run("explorer.exe", $"\"{CustomIcons.Folder}\"");
        })));
        app.Add(Row("Advanced settings file", Push("Edit…", () => Run("notepad.exe", $"\"{ConfigService.ConfigPath}\""))));
    }

    // ---------------- Displays ----------------

    private void DisplaysPane()
    {
        var brightnessGroup = NewGroup();
        var placeholder = brightnessGroup.Add(Row("Brightness", Value("…")));
        Later(Brightness.Get, level =>
        {
            brightnessGroup.Clear();
            if (level is double value)
            {
                var slider = Slider(value, Throttled(v => Brightness.Set(v)), "", "", leftGlyph: true);
                slider.Width = 260;
                brightnessGroup.Add(Row("Brightness", slider));
            }
            else brightnessGroup.Add(Row("Brightness", Value("Set on the display itself")));
        });

        foreach (var display in WindowsSettings.Displays())
        {
            var d = display;
            Header(d.Name + (d.Primary ? " (Main display)" : ""));
            var g = NewGroup();
            var sizes = d.Modes.Select(m => (m.Width, m.Height)).Distinct().ToList();
            int currentSize = sizes.FindIndex(s => s.Width == d.Current.Width && s.Height == d.Current.Height);
            g.Add(Row("Resolution", Popup(sizes.Select(s => $"{s.Width} × {s.Height}").ToList(), currentSize, i =>
            {
                var target = d.Modes.Where(m => m.Width == sizes[i].Width && m.Height == sizes[i].Height).OrderByDescending(m => m.Frequency).First();
                ChangeMode(d, target);
            })));
            var rates = d.Modes.Where(m => m.Width == d.Current.Width && m.Height == d.Current.Height).Select(m => m.Frequency).Distinct().OrderByDescending(f => f).ToList();
            g.Add(Row("Refresh rate", Popup(rates.Select(r => $"{r} Hz").ToList(), rates.IndexOf(d.Current.Frequency), i =>
            {
                var target = d.Modes.First(m => m.Width == d.Current.Width && m.Height == d.Current.Height && m.Frequency == rates[i]);
                ChangeMode(d, target);
            })));
        }
        var more = NewGroup();
        more.Add(Row("Scale, Night Light and arrangement", Push("Advanced…", () => WindowsSettingsRedirect.OpenInWindows("ms-settings:display"))));
    }

    /// <summary>Switches resolution, then asks (15 s) whether to keep it, like every OS does.</summary>
    private void ChangeMode(WindowsSettings.Display display, WindowsSettings.DevMode mode)
    {
        var old = display.Current;
        if (!WindowsSettings.SetDisplayMode(display.Device, mode)) { MessageBox.Show("This display mode could not be used.", "Displays"); return; }
        if (!KeepDialog()) WindowsSettings.SetDisplayMode(display.Device, old);
        Navigate("displays", record: false);
    }

    private bool KeepDialog()
    {
        bool keep = false;
        int seconds = 15;
        var dialog = new Window
        {
            Owner = this, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Topmost = true, FontFamily = TextFont, FontSize = 13, ResizeMode = ResizeMode.NoResize,
        };
        var countdown = new TextBlock { Foreground = _sub, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 16), FontSize = 12 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var revert = Push("Revert", () => dialog.Close());
        var ok = Push("Keep Changes", () => { keep = true; dialog.Close(); }, primary: true);
        ok.Margin = new Thickness(10, 0, 0, 0);
        buttons.Children.Add(revert);
        buttons.Children.Add(ok);
        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 20), Width = 280 };
        stack.Children.Add(new TextBlock { Text = "Keep these display settings?", FontWeight = FontWeights.Bold, Foreground = _text, TextAlignment = TextAlignment.Center });
        stack.Children.Add(countdown);
        stack.Children.Add(buttons);
        dialog.Content = new Border
        {
            Background = _menuBg, CornerRadius = new CornerRadius(12), BorderBrush = _menuBorder, BorderThickness = new Thickness(0.5), Margin = new Thickness(20), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.35 },
        };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Tick() { countdown.Text = $"Reverting to the previous settings in {seconds} seconds."; if (seconds-- <= 0) dialog.Close(); }
        timer.Tick += (_, _) => Tick();
        Tick();
        timer.Start();
        dialog.ShowDialog();
        timer.Stop();
        return keep;
    }

    /// <summary>For slow setters (monitor brightness over DDC): only the latest value is applied, off the UI thread.</summary>
    private static Action<double> Throttled(Action<double> set)
    {
        double? pending = null;
        bool running = false;
        object gate = new();
        return v =>
        {
            lock (gate)
            {
                pending = v;
                if (running) return;
                running = true;
            }
            Task.Run(() =>
            {
                while (true)
                {
                    double value;
                    lock (gate)
                    {
                        if (pending == null) { running = false; return; }
                        value = pending.Value;
                        pending = null;
                    }
                    try { set(value); } catch { }
                }
            });
        };
    }

    // ---------------- Wallpaper ----------------

    private void WallpaperPane()
    {
        string? current = WindowsSettings.Wallpaper;
        var top = NewGroup();
        var preview = new Border { Width = 160, Height = 100, CornerRadius = new CornerRadius(8), Background = _track, Margin = new Thickness(0, 0, 16, 0), ClipToBounds = true };
        if (current != null && File.Exists(current))
        {
            // MacDock's own wallpapers have a small copy; any other picture is decoded off the UI thread.
            string small = MacWallpapers.CurrentDesign() is { } design ? MacWallpapers.ThumbFor(design, _dark) : current;
            if (File.Exists(small) && small != current) preview.Background = new ImageBrush(LoadThumb(small, 320)) { Stretch = Stretch.UniformToFill };
            else Later(() => LoadThumb(current, 320), image => { if (image != null) preview.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill }; });
        }
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = current != null ? Path.GetFileNameWithoutExtension(current) : "Wallpaper", FontWeight = FontWeights.SemiBold, Foreground = _text, TextTrimming = TextTrimming.CharacterEllipsis });
        var fit = Popup(WindowsSettings.WallpaperFits, WindowsSettings.WallpaperFit, i => { if (current != null) InBackground(() => WindowsSettings.SetWallpaper(current, i)); });
        fit.HorizontalAlignment = HorizontalAlignment.Left;
        fit.Margin = new Thickness(0, 8, 0, 0);
        info.Children.Add(fit);
        var add = Push("Add Photo…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp", Title = "Choose a Wallpaper" };
            if (dialog.ShowDialog(this) == true) { string file = dialog.FileName; WindowsSettings.SetWallpaperSmoothAsync(file).ContinueWith(_ => Dispatcher.BeginInvoke(() => Navigate("wallpaper", record: false))); }
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 0);
        info.Children.Add(add);
        var panel = new DockPanel { Margin = new Thickness(14) };
        panel.Children.Add(preview);
        panel.Children.Add(info);
        top.Add(panel);

        Header("macOS");
        var macWrap = new WrapPanel { Margin = new Thickness(8) };
        var macGroup = NewGroup();
        macGroup.Add(macWrap);
        Note("Made by MacDock in 4K. Like macOS's dynamic wallpapers, each switches between its light and dark version with the appearance.");
        if (_config?.PreviousWallpaper is { } previous && File.Exists(previous))
        {
            var back = NewGroup();
            back.Add(Row("Your previous wallpaper", Push("Restore", () =>
                WindowsSettings.SetWallpaperSmoothAsync(previous).ContinueWith(_ => Dispatcher.BeginInvoke(() => Navigate("wallpaper", record: false)))), sub: Path.GetFileName(previous)));
        }
        string? currentDesign = MacWallpapers.CurrentDesign();
        var making = new TextBlock { Text = "Creating wallpapers…", Foreground = _sub, Margin = new Thickness(8) };
        macWrap.Children.Add(making);
        int token = _pageToken;
        MacWallpapers.EnsureAsync().ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (token != _pageToken) return;
            macWrap.Children.Clear();
            foreach (var design in MacWallpapers.Designs)
            {
                var d = design;
                bool chosen = d.Id == currentDesign;
                var thumb = LoadThumb(MacWallpapers.ThumbFor(d.Id, _dark), 264);
                var stack = new StackPanel { Margin = new Thickness(6), Cursor = Cursors.Hand };
                stack.Children.Add(new Border
                {
                    Width = 132, Height = 76, CornerRadius = new CornerRadius(9),
                    Background = thumb != null ? new ImageBrush(thumb) { Stretch = Stretch.UniformToFill } : _track,
                    BorderBrush = chosen ? _accentBrush : _controlBorder, BorderThickness = new Thickness(chosen ? 3 : 0.5),
                });
                stack.Children.Add(new TextBlock { Text = d.Name, FontSize = 11.5, Foreground = chosen ? _text : _sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
                stack.MouseLeftButtonUp += async (_, _) =>
                {
                    await MacWallpapers.ApplyAsync(d.Id);
                    if (token == _pageToken) Navigate("wallpaper", record: false);
                };
                macWrap.Children.Add(stack);
            }
        }));

        Header("Pictures");
        var wrap = new WrapPanel { Margin = new Thickness(8) };
        var grid = NewGroup();
        grid.Add(wrap);
        Later(() => WindowsSettings.WallpaperChoices().Select(f => (File: f, Thumb: LoadThumb(f, 200))).Where(x => x.Thumb != null).ToList(), pictures =>
        {
            foreach (var (file, thumb) in pictures)
            {
                string path = file;
                bool chosen = string.Equals(path, current, StringComparison.OrdinalIgnoreCase);
                var tile = new Border
                {
                    Width = 104, Height = 66, CornerRadius = new CornerRadius(6), Margin = new Thickness(5), Cursor = Cursors.Hand, ToolTip = Path.GetFileName(path),
                    Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill },
                    BorderBrush = chosen ? _accentBrush : _controlBorder, BorderThickness = new Thickness(chosen ? 3 : 0.5),
                };
                tile.MouseLeftButtonUp += (_, _) => { _ = WindowsSettings.SetWallpaperSmoothAsync(path); Dispatcher.BeginInvoke(() => Navigate("wallpaper", record: false), System.Windows.Threading.DispatcherPriority.Background); };
                wrap.Children.Add(tile);
            }
        });
    }

    private static BitmapSource? LoadThumb(string file, int width)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    // ---------------- Battery / Energy ----------------

    private void BatteryPane()
    {
        var battery = SystemStatus.GetBattery();
        if (battery.Present)
        {
            var g = NewGroup();
            g.Add(Row("Battery level", Value($"{battery.Percent}%{(battery.Charging ? " · Charging" : "")}"), glyph: "", glyphColor: 0x34C759));
        }
        Header("Energy");
        var energy = NewGroup();
        var planRow = energy.Add(new StackPanel());
        var display = energy.Add(new StackPanel());
        var sleep = energy.Add(new StackPanel());
        Later(() => (WindowsSettings.PowerPlans(), WindowsSettings.PowerMinutes("SUB_VIDEO", "VIDEOIDLE"), WindowsSettings.PowerMinutes("SUB_SLEEP", "STANDBYIDLE")), data =>
        {
            var (plans, screenOff, sleepAfter) = data;
            if (plans.Count > 0)
                planRow.Children.Add(Row("Energy mode", Popup(plans.Select(p => p.Name).ToList(), plans.FindIndex(p => p.Active), i => InBackground(() => WindowsSettings.SetPowerPlan(plans[i].Guid)))));
            display.Children.Add(Row("Turn display off when inactive", Popup(MinuteNames, NearestMinutes(screenOff), i => InBackground(() => WindowsSettings.SetPowerMinutes("monitor-timeout", MinuteValues[i])))));
            sleep.Children.Add(Row("Put the computer to sleep when inactive", Popup(MinuteNames, NearestMinutes(sleepAfter), i => InBackground(() => WindowsSettings.SetPowerMinutes("standby-timeout", MinuteValues[i])))));
        });
    }

    // ---------------- Gaming & Performance ----------------

    private void PerformancePane()
    {
        Hero("\uE7FC", 0x30D158, "Gaming & Performance",
            "Make Windows lighter and faster: fewer background tasks, no ads, and more of your PC for the game you are playing. Every change can be turned back.");
        var top = NewGroup();
        top.Add(Row("Recommended tweaks", Push("Apply All", ApplyRecommended, primary: true),
            sub: "Turns on every recommended tweak below. Windows asks for permission once."));
        top.Add(Row("MacDock during games", Value("Steps aside automatically"),
            sub: "While a game or video is fullscreen, MacDock pauses its background work and runs at low priority."));

        Header("Gaming");
        TweakGroup(WindowsTweaks.Gaming);
        var power = NewGroup();
        var planValue = Value("…");
        power.Add(Row("Ultimate Performance power plan", Push("Turn On", () =>
        {
            planValue.Text = "Turning on…";
            Later(() => { WindowsTweaks.AddUltimatePlan(); return WindowsSettings.PowerPlans().FirstOrDefault(p => p.Active)?.Name ?? ""; }, name => planValue.Text = name);
        }), sub: "Keeps the processor at full speed (desktop PCs; uses more power)."));
        power.Add(Row("Current power plan", planValue));
        Later(() => WindowsSettings.PowerPlans().FirstOrDefault(p => p.Active)?.Name ?? "Unknown", name => planValue.Text = name);

        Header("Smoother Windows");
        TweakGroup(WindowsTweaks.Smoothness);

        Header("Clean Up");
        var clean = NewGroup();
        var freed = Value("");
        freed.Margin = new Thickness(0, 0, 10, 0);
        freed.VerticalAlignment = VerticalAlignment.Center;
        var cleanControls = new StackPanel { Orientation = Orientation.Horizontal };
        cleanControls.Children.Add(freed);
        cleanControls.Children.Add(Push("Clean Up", () =>
        {
            freed.Text = "Cleaning…";
            Later(WindowsTweaks.CleanTemporaryFiles, bytes => freed.Text = bytes > 0 ? $"{bytes / 1048576.0:0} MB freed" : "Already clean");
        }));
        clean.Add(Row("Temporary files", cleanControls, sub: "Deletes leftover temporary files more than a day old."));
        clean.Add(Row("Apps that open at login", click: () => Navigate("login"), chevron: true, sub: "Fewer login items: a faster start and more free memory."));

        Header("Remove Preinstalled Apps");
        var bloat = NewGroup();
        bloat.Add(Row("Looking for preinstalled apps…", labelBrush: _sub));
        Note("Removed apps can be installed again from the Microsoft Store at any time.");
        Later(WindowsTweaks.InstalledBloat, apps =>
        {
            bloat.Clear();
            if (apps.Count == 0) { bloat.Add(Row("No unwanted preinstalled apps found", labelBrush: _sub)); return; }
            var chosen = new HashSet<string>(apps.Select(a => a.Package));
            foreach (var app in apps)
            {
                var a = app;
                bloat.Add(Row(a.Name, Switch(true, on => { if (on) chosen.Add(a.Package); else chosen.Remove(a.Package); }), glyph: "\uE74D", glyphColor: 0xFF3B30));
            }
            bloat.Add(Row("", Push("Remove Selected…", () =>
            {
                var names = apps.Where(x => chosen.Contains(x.Package)).Select(x => x.Name).ToList();
                if (names.Count == 0) return;
                if (MessageBox.Show(this, "Remove these apps from Windows?\n\n" + string.Join("\n", names) +
                        "\n\nThey can be installed again from the Microsoft Store.", "Remove Preinstalled Apps",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                bloat.Clear();
                bloat.Add(Row($"Removing {names.Count} apps…", labelBrush: _sub));
                var packages = chosen.ToList();
                Later(() => { WindowsTweaks.Remove(packages); return 0; }, _ => Navigate("performance", record: false));
            }, primary: true)));
        });
    }

    private void TweakGroup(WindowsTweaks.Tweak[] tweaks)
    {
        var g = NewGroup();
        foreach (var tweak in tweaks)
        {
            var tw = tweak;
            MacSwitch? toggle = null;
            toggle = Switch(tw.IsOn(), on =>
            {
                toggle!.IsEnabled = false;
                // Read back afterwards: an administrator tweak the user declined stays as it was.
                Later(() => { tw.Apply(on); return tw.IsOn(); }, now => { toggle.IsOn = now; toggle.IsEnabled = true; });
            });
            g.Add(Row(tw.Title, toggle, sub: tw.Description + (tw.NeedsAdmin ? " Windows asks for permission." : "")));
        }
    }

    private void ApplyRecommended()
    {
        Later(WindowsTweaks.ApplyRecommended, _ => Navigate("performance", record: false));
    }

    // ---------------- Lock Screen ----------------

    private void LockPane()
    {
        var g = NewGroup();
        int saver = WindowsSettings.ScreenSaverMinutes;
        g.Add(Row("Start screen saver when inactive", Popup(MinuteNames, NearestMinutes(saver), i => InBackground(() => WindowsSettings.ScreenSaverMinutes = MinuteValues[i]))));
        var display = g.Add(new StackPanel());
        Later(() => WindowsSettings.PowerMinutes("SUB_VIDEO", "VIDEOIDLE"), minutes =>
            display.Children.Add(Row("Turn display off when inactive", Popup(MinuteNames, NearestMinutes(minutes), i => InBackground(() => WindowsSettings.SetPowerMinutes("monitor-timeout", MinuteValues[i]))))));
        var now = NewGroup();
        now.Add(Row("Lock the screen now", Push("Lock Screen", PowerActions.Lock), sub: "Shortcut: Windows key + L"));
    }

    // ---------------- Privacy & Security ----------------

    private void PrivacyPane()
    {
        Header("Privacy");
        var p = NewGroup();
        foreach (var (title, capability, glyph, color) in new[]
        {
            ("Location Services", "location", "", 0x0A84FFu), ("Camera", "webcam", "", 0x34C759u), ("Microphone", "microphone", "", 0xFF9500u),
            ("Contacts", "contacts", "", 0x8E8E93u), ("Calendars", "appointments", "", 0xFF3B30u), ("Photos", "picturesLibrary", "", 0xFF9500u),
            ("Files and Folders", "documentsLibrary", "", 0x0A84FFu),
        })
        {
            string cap = capability;
            p.Add(Row(title, SysSwitch(WindowsSettings.AppAccess(cap), on => WindowsSettings.SetAppAccess(cap, on)), glyph: glyph, glyphColor: color));
        }
        Note("When off, apps can't use it. Some desktop apps may still ask Windows directly.");
        Header("Security");
        var s = NewGroup();
        s.Add(Row("Personalised ads", SysSwitch(WindowsSettings.AdvertisingId, on => WindowsSettings.AdvertisingId = on), sub: "Lets apps use your advertising ID."));
        s.Add(Row("Windows Security", Push("Open…", () => Run("windowsdefender:")), sub: "Virus protection, firewall and device security."));
    }

    // ---------------- Users & Groups ----------------

    private void UsersPane()
    {
        // The picture, big and centered, like the top of macOS's Users & Groups.
        var g = NewGroup();
        var me = new StackPanel { Margin = new Thickness(20, 22, 20, 18), HorizontalAlignment = HorizontalAlignment.Center };
        var avatar = new Ellipse { Width = 84, Height = 84, Stroke = _glassRim, StrokeThickness = 1 };
        var picture = UserInfo.LoadAccountPicture(256);
        avatar.Fill = picture != null ? new ImageBrush(picture) { Stretch = Stretch.UniformToFill } : B(0xFF8E8E93);
        me.Children.Add(avatar);
        me.Children.Add(new TextBlock { Text = WindowsSettings.FullUserName(), FontSize = 17, FontWeight = FontWeights.Bold, Foreground = _text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
        me.Children.Add(new TextBlock { Text = (WindowsSettings.IsAdmin() ? "Admin" : "Standard") + " · " + Environment.UserName, Foreground = _sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        g.Add(me);

        Header("Picture");
        var pic = NewGroup();
        pic.Add(Row("Photo", Push("Choose Photo…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp", Title = "Choose a Picture" };
            if (dialog.ShowDialog(this) != true) return;
            try { UserInfo.SetPhoto(dialog.FileName); } catch (Exception ex) { CrashLog.Write("SetPhoto", ex); }
            PictureChanged();
        }), sub: "Any photo; it is cropped to a circle."));
        var monograms = new StackPanel { Orientation = Orientation.Horizontal };
        string letters = UserInfo.Initials();
        for (int i = 0; i < UserInfo.MonogramColors.Length; i++)
        {
            int index = i;
            var dot = new Ellipse { Width = 30, Height = 30, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, ToolTip = "Monogram",
                Fill = new ImageBrush(UserInfo.Monogram(letters, i, 60)) };
            dot.MouseLeftButtonUp += (_, _) => { UserInfo.SetMonogram(index); PictureChanged(); };
            monograms.Children.Add(dot);
        }
        pic.Add(Row("Monogram", monograms));
        pic.Add(Row("Windows account picture", Push("Use", () => { UserInfo.ResetPicture(); PictureChanged(); })));

        var actions = NewGroup();
        actions.Add(Row("Account details", Push("Edit…", () => WindowsSettingsRedirect.OpenInWindows("ms-settings:yourinfo"))));
        actions.Add(Row("Password and sign-in options", Push("Change…", () => WindowsSettingsRedirect.OpenInWindows("ms-settings:signinoptions"))));
        actions.Add(Row("Other users", Push("Manage…", () => WindowsSettingsRedirect.OpenInWindows("ms-settings:otherusers"))));
    }

    /// <summary>The user's picture changed: redraw the sidebar's account row and this pane.</summary>
    private void PictureChanged()
    {
        _accountPicture = null;
        BuildSidebar();
        Navigate(_current, record: false);
    }

    // ---------------- Keyboard & Mouse ----------------

    private void KeyboardPane()
    {
        var g = NewGroup();
        var repeat = Slider(WindowsSettings.KeyRepeat / 31.0, Throttled(v => WindowsSettings.KeyRepeat = (int)Math.Round(v * 31)), "Slow", "Fast");
        repeat.Width = 240;
        g.Add(Row("Key repeat rate", repeat));
        var delay = Slider(1 - WindowsSettings.KeyDelay / 3.0, Throttled(v => WindowsSettings.KeyDelay = 3 - (int)Math.Round(v * 3)), "Long", "Short");
        delay.Width = 240;
        g.Add(Row("Delay until repeat", delay));
        Header("Text Input");
        var inputs = NewGroup();
        foreach (var layout in SystemStatus.GetInstalledLayouts())
            inputs.Add(Row(layout.Name, Value(layout.Code), glyph: "", glyphColor: 0x8E8E93));
        inputs.Add(Row("Input sources", Push("Edit…", () => WindowsSettingsRedirect.OpenInWindows("ms-settings:regionlanguage"))));
        Note("Switch input source with Windows key + Space.");
    }

    private void MousePane()
    {
        var g = NewGroup();
        var speed = Slider((WindowsSettings.MouseSpeed - 1) / 19.0, Throttled(v => WindowsSettings.MouseSpeed = 1 + (int)Math.Round(v * 19)), "Slow", "Fast");
        speed.Width = 240;
        g.Add(Row("Tracking speed", speed));
        var click = Slider((900 - WindowsSettings.DoubleClickTime) / 700.0, Throttled(v => WindowsSettings.DoubleClickTime = 900 - (int)Math.Round(v * 700)), "Slow", "Fast");
        click.Width = 240;
        g.Add(Row("Double-click speed", click));
        var scroll = Slider((WindowsSettings.ScrollLines - 1) / 11.0, Throttled(v => WindowsSettings.ScrollLines = 1 + (int)Math.Round(v * 11)), "Slow", "Fast");
        scroll.Width = 240;
        g.Add(Row("Scrolling speed", scroll));
        var b = NewGroup();
        b.Add(Row("Primary mouse button", Popup(new[] { "Left", "Right" }, WindowsSettings.SwapButtons ? 1 : 0, i => InBackground(() => WindowsSettings.SwapButtons = i == 1))));
    }

    // ---------------- Printers ----------------

    private void PrintersPane()
    {
        var g = NewGroup();
        string? def = WindowsSettings.DefaultPrinter();
        foreach (var printer in WindowsSettings.Printers())
        {
            string name = printer;
            bool isDefault = name == def;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (!isDefault)
            {
                var make = Push("Make Default", () => { WindowsSettings.MakeDefaultPrinter(name); Navigate("printers", record: false); });
                make.Margin = new Thickness(0, 0, 8, 0);
                buttons.Children.Add(make);
            }
            buttons.Children.Add(Push("Open Queue…", () => Run("rundll32.exe", $"printui.dll,PrintUIEntry /o /n \"{name}\"")));
            g.Add(Row(name, buttons, sub: isDefault ? "Default printer" : null, glyph: "", glyphColor: 0x8E8E93));
        }
        if (g.Count == 0) g.Add(Row("No printers", labelBrush: _sub));
        var add = NewGroup();
        add.Add(Row("Add printer, scanner or fax", Push("Add…", () => Run("rundll32.exe", "printui.dll,PrintUIEntry /il"))));
    }

    // ---------------- Controls ----------------

    /// <summary>
    /// The macOS 26 switch: a pill track (accent when on) with a white capsule knob that slides, and
    /// stretches a little while it is pressed, like Liquid Glass controls do.
    /// </summary>
    private sealed class MacSwitch : Grid
    {
        private const double TrackWidth = 40, TrackHeight = 22, KnobWidth = 24, KnobHeight = 18, Stretch = 6;
        private readonly Border _track, _knob;
        private readonly TranslateTransform _move = new();
        private readonly Brush _onBrush, _offBrush;
        private readonly Action<bool> _changed;
        private bool _on, _pressed;

        public MacSwitch(bool on, Action<bool> changed, Brush onBrush, Brush offBrush)
        {
            _changed = changed;
            _onBrush = onBrush;
            _offBrush = offBrush;
            Width = TrackWidth;
            Height = TrackHeight;
            Cursor = Cursors.Hand;
            _track = new Border { CornerRadius = new CornerRadius(TrackHeight / 2) };
            _knob = new Border
            {
                Width = KnobWidth, Height = KnobHeight, CornerRadius = new CornerRadius(KnobHeight / 2), Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0), RenderTransform = _move,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.28 },
            };
            Children.Add(_track);
            Children.Add(_knob);
            _on = on;
            Draw(false);
            MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!IsEnabled) return;
                _pressed = true;
                Draw(true);
            };
            MouseLeave += (_, _) => { if (_pressed) { _pressed = false; Draw(true); } };
            MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!IsEnabled || !_pressed) return;
                _pressed = false;
                _on = !_on;
                Draw(true);
                _changed(_on);
            };
            IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.45;
        }

        public bool IsOn
        {
            get => _on;
            set { if (_on == value) return; _on = value; Draw(true); }
        }

        private void Draw(bool animate)
        {
            _track.Background = _on ? _onBrush : _offBrush;
            double width = KnobWidth + (_pressed ? Stretch : 0);
            double x = _on ? TrackWidth - 4 - width : 0;
            var time = TimeSpan.FromMilliseconds(animate ? 180 : 0);
            var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
            _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, time) { EasingFunction = ease });
            _knob.BeginAnimation(WidthProperty, new DoubleAnimation(width, time) { EasingFunction = ease });
        }
    }

    /// <summary>The macOS 26 slider: thin track, accent fill up to a white capsule knob; labels at both ends.</summary>
    private sealed class MacSlider : DockPanel
    {
        private const double KnobWidth = 26, KnobHeight = 18;
        private readonly Grid _area = new() { Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        private readonly Border _fill, _knob;
        private readonly Action<double> _changed;
        private double _value;

        public MacSlider(double value, Action<double> changed, Brush accent, Brush track, Brush labels, string? left, string? right, bool glyphs, bool dark)
        {
            _changed = changed;
            _value = Math.Clamp(value, 0, 1);
            LastChildFill = true;
            TextBlock Label(string text) => new()
            {
                Text = text, Foreground = labels, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = glyphs ? IconFont : TextFont, FontSize = glyphs ? 12 : 11, Margin = new Thickness(6, 0, 6, 0),
            };
            if (left != null) { var l = Label(left); if (glyphs) l.FontSize = 10; SetDock(l, Dock.Left); Children.Add(l); }
            if (right != null) { var r = Label(right); SetDock(r, Dock.Right); Children.Add(r); }
            _area.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = track, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
            _fill = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = accent, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
            _area.Children.Add(_fill);
            _knob = new Border
            {
                Width = KnobWidth, Height = KnobHeight, CornerRadius = new CornerRadius(KnobHeight / 2), HorizontalAlignment = HorizontalAlignment.Left,
                Background = dark ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEA)) : Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)), BorderThickness = new Thickness(0.5),
                Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Direction = 270, Opacity = 0.3 },
            };
            _area.Children.Add(_knob);
            Children.Add(_area);
            _area.SizeChanged += (_, _) => Layout();
            _area.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!IsEnabled) return; _area.CaptureMouse(); SetFrom(e.GetPosition(_area).X); };
            _area.MouseMove += (_, e) => { if (_area.IsMouseCaptured) SetFrom(e.GetPosition(_area).X); };
            _area.MouseLeftButtonUp += (_, e) => { e.Handled = true; _area.ReleaseMouseCapture(); };
            IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
        }

        public double Value
        {
            get => _value;
            set { _value = Math.Clamp(value, 0, 1); Layout(); }
        }

        private void SetFrom(double x)
        {
            double usable = Math.Max(1, _area.ActualWidth - KnobWidth);
            double v = Math.Clamp((x - KnobWidth / 2) / usable, 0, 1);
            if (Math.Abs(v - _value) < 0.002) return;
            _value = v;
            Layout();
            _changed(v);
        }

        private void Layout()
        {
            double usable = Math.Max(0, _area.ActualWidth - KnobWidth);
            double x = usable * _value;
            _knob.Margin = new Thickness(x, 0, 0, 0);
            _fill.Width = Math.Max(0, x + KnobWidth / 2 - 2);
        }
    }
}
