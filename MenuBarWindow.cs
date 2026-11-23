using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacDock.Controls;
using MacDock.Native;
using MacDock.Services;

namespace MacDock;

/// <summary>
/// macOS-style menu bar across the top of the primary monitor.
/// Left: logo menu (about, settings, power), the app in front (bold) with its menu, Window menu.
/// Right: input language, sound, battery (laptops), network, search, Control Center, date and time.
///
/// It never takes focus (so "the app in front" stays the app the user was using) and reserves its
/// strip through <see cref="TopAppBar"/>, so maximized windows start below it.
/// </summary>
internal sealed class MenuBarWindow : Window
{
    public const double BarHeight = 26;

    private static readonly uint AppBarMessage = Win32.RegisterWindowMessage("MacDock.MenuBarMessage");
    private static readonly uint TaskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x1D, 0x1D, 0x1F));

    // The bar has no background (macOS Tahoe): its text and icons are white on dark wallpapers and
    // near-black on light ones. These two brushes are recolored when the wallpaper changes.
    private readonly SolidColorBrush _barForeground = new(Color.FromRgb(0x1D, 0x1D, 0x1F));
    private readonly SolidColorBrush _barPressed = new(Color.FromArgb(0x26, 0x00, 0x00, 0x00));
    private readonly System.Windows.Media.Effects.DropShadowEffect _textShadow = new()
    {
        BlurRadius = 6, ShadowDepth = 0, Opacity = 0, Color = Colors.Black,
    };
    private readonly DispatcherTimer _toneTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _darkWallpaper;
    private Grid _root = null!;
    private bool _forceTone = true;
    // The dock's glass (color + opacity): menus and panels under the bar use exactly the same.
    private Color _glassColor = Colors.White;
    private double _glassOpacity = 0.25;
    private bool _menuDark;
    private Panel? _left, _right;
    private readonly AudioVolume _volume = new();
    private readonly ConcurrentDictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _clockTimer = new();
    private readonly DispatcherTimer _fastTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly DispatcherTimer _slowTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Win32.WinEventProc _foregroundProc; // keep alive while hooked
    private IntPtr _foregroundHook;
    private IntPtr _hwnd;

    // Left
    private readonly TextBlock _appName;
    // Right
    private readonly TextBlock _language;
    private readonly MenuBarIcon _speaker = new() { Glyph = MenuBarGlyph.Speaker };
    private readonly MenuBarIcon _battery = new() { Glyph = MenuBarGlyph.Battery };
    private readonly TextBlock _batteryText;
    private readonly MenuBarIcon _network = new() { Glyph = MenuBarGlyph.Offline };
    private readonly TextBlock _clock;
    private readonly Border _batteryButton;
    private readonly Border _networkButton;
    private readonly DispatcherTimer _outsideClick = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private bool _mouseWasDown;

    private readonly StackPanel _menuStrip = new() { Orientation = Orientation.Horizontal };
    private bool? _menusForFinder;
    private IntPtr _appWindow;            // the app window the user was last using
    private string? _appExe;
    private NetworkState _networkState = NetworkState.Offline;
    private bool _networkBusy;
    // The one menu or panel currently open under a bar item (macOS shows one at a time).
    private Border? _openAnchor;
    private FrameworkElement? _openContent;     // its visible part, for "click outside" checks
    private Action? _closeOpen;
    private Border? _lastClosedAnchor;
    private DateTime _lastClosedAt;
    private bool _fullscreenHidden;

    /// <summary>The magnifier was clicked: open MacDock's Start menu search.</summary>
    public event Action? SearchRequested;

    public MenuBarWindow()
    {
        // Fades in when MacDock starts, like the menu bar after logging in on a Mac.
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "MacDock Menu Bar";
        FontFamily = MacFonts.Text;
        FontSize = 13.5;
        UseLayoutRounding = true;
        MacFonts.Smooth(this);
        _appName = BarText("", bold: true);
        _language = BarText("", size: 12);
        _batteryText = BarText("", size: 12);
        _clock = BarText("");

        // ---- Left ----
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        left.Children.Add(Button(new MenuBarIcon { Glyph = MenuBarGlyph.Logo }, OpenLogoMenu, padding: 11));
        left.Children.Add(Button(_appName, OpenAppMenu));
        left.Children.Add(_menuStrip);
        BuildMenus(finder: true);

        // ---- Right ----
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        right.Children.Add(Button(new MenuBarIcon { Glyph = MenuBarGlyph.Chevron }, OpenTrayIcons, padding: 7));
        right.Children.Add(Button(_language, OpenLanguageMenu));
        var speakerButton = Button(_speaker, b => OpenControlCenter(b, soundOnly: true));
        speakerButton.MouseWheel += (_, e) => NudgeVolume(e.Delta);
        right.Children.Add(speakerButton);
        var batteryContent = new StackPanel { Orientation = Orientation.Horizontal };
        _batteryText.Margin = new Thickness(0, 0, 5, 0);
        batteryContent.Children.Add(_batteryText);
        batteryContent.Children.Add(_battery);
        _batteryButton = Button(batteryContent, OpenBatteryMenu);
        _batteryButton.Visibility = Visibility.Collapsed;
        right.Children.Add(_batteryButton);
        _networkButton = Button(_network, OpenNetworkMenu);
        right.Children.Add(_networkButton);
        right.Children.Add(Button(new MenuBarIcon { Glyph = MenuBarGlyph.Search }, _ => SearchRequested?.Invoke()));
        right.Children.Add(Button(new MenuBarIcon { Glyph = MenuBarGlyph.ControlCenter }, b => OpenControlCenter(b, soundOnly: false)));
        right.Children.Add(Button(_clock, _ => OpenNotifications()));

        var root = new Grid
        {
            // Invisible but not fully transparent: fully transparent pixels would let clicks fall through.
            Background = Frozen(Color.FromArgb(0x01, 0x00, 0x00, 0x00)),
            Height = BarHeight,
        };
        _left = left;
        _right = right;
        root.Children.Add(left);
        root.Children.Add(right);
        _root = root;
        Content = root;

        _foregroundProc = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject == Win32.OBJID_WINDOW && idChild == 0) OnForeground(hwnd);
        };

        _outsideClick.Tick += (_, _) => WatchOutsideClick();

        SourceInitialized += (_, _) => OnSourceInitialized();
        Loaded += (_, _) => Start();
        Closed += (_, _) => Stop();
    }

    // ---------------- Lifetime ----------------

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.AddExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        PlaceOnPrimary();
    }

    private void Start()
    {
        _foregroundHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT); // own windows too: Finder is an app of its own
        OnForeground(Win32.GetForegroundWindow());

        UpdateClock();
        _clockTimer.Tick += (_, _) => UpdateClock();
        _fastTimer.Tick += (_, _) => { if (GameMode.Active) return; UpdateLanguage(); UpdateVolume(); };
        _slowTimer.Tick += (_, _) => { if (GameMode.Active) return; UpdateBattery(); UpdateNetwork(); };
        _fastTimer.Start();
        _slowTimer.Start();
        _toneTimer.Tick += (_, _) => UpdateTone();
        _toneTimer.Start();
        UpdateTone();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        UpdateLanguage();
        UpdateVolume();
        UpdateBattery();
        UpdateNetwork();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;
    }

    private void Stop()
    {
        _clockTimer.Stop();
        _fastTimer.Stop();
        _slowTimer.Stop();
        if (_foregroundHook != IntPtr.Zero) Win32.UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _toneTimer.Stop();
        TopAppBar.Remove();
    }

    /// <summary>Hides the bar while a fullscreen app (video, game) is in front.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (fullscreen == _fullscreenHidden) return;
        _fullscreenHidden = fullscreen;
        if (fullscreen)
        {
            ClosePopup();
            Hide();
        }
        else
        {
            Show();
            PlaceOnPrimary();
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(PlaceOnPrimary);

    private void OnTimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateClock);

    /// <summary>Full width of the primary monitor, at its very top; reserves the strip for maximized windows.</summary>
    private void PlaceOnPrimary()
    {
        if (_hwnd == IntPtr.Zero) return;
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(Win32.MonitorFromPoint(new Win32.POINT(), Win32.MONITOR_DEFAULTTOPRIMARY), ref info)) return;
        var m = info.rcMonitor;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int heightPx = (int)Math.Ceiling(BarHeight * scale);

        Left = m.Left / scale;
        Top = m.Top / scale;
        Width = (m.Right - m.Left) / scale;
        Height = heightPx / scale;

        var hwnd = _hwnd;
        Task.Run(() => TopAppBar.Reserve(hwnd, AppBarMessage, heightPx));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == AppBarMessage && AppBarMessage != 0)
        {
            if (wParam.ToInt32() == Win32.ABN_POSCHANGED) Task.Run(() => TopAppBar.Refresh());
            handled = true;
        }
        else if (msg == TaskbarCreatedMessage && TaskbarCreatedMessage != 0)
        {
            Task.Run(() => TopAppBar.Refresh(explorerRestarted: true));
        }
        return IntPtr.Zero;
    }

    // ---------------- Building blocks ----------------

    private TextBlock BarText(string text, bool bold = false, double size = 13.5) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Medium,
        Foreground = _barForeground,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// The dock's glass settings. The bar itself stays transparent (macOS); the menus and panels that
    /// open from it (Control Center, app, network, sound, background apps) use exactly this glass.
    /// </summary>
    public void SetAppearance(Color color, double opacity)
    {
        _glassColor = color;
        _glassOpacity = Math.Clamp(opacity, 0, 1);
        _forceTone = true;
        UpdateTone();
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.Desktop or Microsoft.Win32.UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(UpdateTone);
    }

    /// <summary>White text on dark wallpapers, near-black on light ones (with a soft shadow on white for legibility).</summary>
    private void UpdateTone()
    {
        Task.Run(WallpaperTone.TopBrightness).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            if (task.Status != TaskStatus.RanToCompletion) return;
            double wallpaper = task.Result;
            // Menus: what the eye sees is the (blurred) wallpaper through the dock's tint.
            double tint = (0.2126 * _glassColor.R + 0.7152 * _glassColor.G + 0.0722 * _glassColor.B) / 255.0;
            _menuDark = wallpaper * (1 - _glassOpacity) + tint * _glassOpacity < 0.58;
            GlassTheme.Apply(_glassColor, _glassOpacity, _menuDark);

            bool dark = wallpaper < 0.58; // the bar itself is transparent
            if (!_forceTone && dark == _darkWallpaper) return;
            _forceTone = false;
            _darkWallpaper = dark;
            var duration = TimeSpan.FromMilliseconds(400);
            _barForeground.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(dark ? Colors.White : Color.FromRgb(0x1D, 0x1D, 0x1F), duration));
            _barPressed.Color = dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00);
            // Soft shadow only behind white text (an effect re-renders text as a bitmap: keep it off when not needed).
            _textShadow.Opacity = 0.35;
            if (_left != null) _left.Effect = dark ? _textShadow : null;
            if (_right != null) _right.Effect = dark ? _textShadow : null;
        }));
    }

    /// <summary>A clickable bar item: rounded highlight while pressed or while its menu is open.</summary>
    private Border Button(UIElement content, Action<Border> onClick, double padding = 9)
    {
        if (content is MenuBarIcon icon) icon.Foreground = _barForeground;
        if (content is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border
        {
            Child = content,
            Padding = new Thickness(padding, 0, padding, 0),
            Margin = new Thickness(1, 3, 1, 3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
        };
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            // A second click on the item whose menu is open closes it, like macOS. The menu may already
            // have closed itself on this very press (just before this handler): treat that the same.
            bool wasOpen = _openAnchor == button ||
                           (_lastClosedAnchor == button && DateTime.UtcNow - _lastClosedAt < TimeSpan.FromMilliseconds(300));
            ClosePopup();
            if (wasOpen) return;
            onClick(button);
        };
        return button;
    }

    private void ClosePopup() => _closeOpen?.Invoke();

    /// <summary>Registers the menu/panel just opened under <paramref name="anchor"/> and starts watching for outside clicks.</summary>
    private void TrackOpen(Border anchor, FrameworkElement content, Action close)
    {
        ClosePopup();
        _openAnchor = anchor;
        _openContent = content;
        _closeOpen = close;
        anchor.Background = _barPressed;
        _mouseWasDown = true; // the press that opened it is still down
        _outsideClick.Start();
    }

    /// <summary>Called from the menu/panel's Closed event, whatever closed it.</summary>
    /// <param name="content">The menu/panel that closed. Only it may clear the tracking: when one menu
    /// replaces another under the same item (background app, then its right-click menu), the old one's
    /// late Closed event must not forget the new one (it then could not be closed any more).</param>
    private void OnOpenClosed(Border anchor, FrameworkElement content)
    {
        if (_openContent != content)
        {
            if (_openAnchor != anchor) anchor.Background = Brushes.Transparent;
            return;
        }
        anchor.Background = Brushes.Transparent;
        _lastClosedAnchor = anchor;
        _lastClosedAt = DateTime.UtcNow;
        _openAnchor = null;
        _openContent = null;
        _closeOpen = null;
    }

    /// <summary>
    /// Closes the open panel when the mouse is pressed anywhere outside it. WPF's own "StaysOpen=false"
    /// relies on mouse capture, which a window that never takes focus loses at once (the panel closed
    /// right after opening), so the mouse buttons are watched directly instead, like the Start menu does.
    /// </summary>
    private void WatchOutsideClick()
    {
        if (_openAnchor == null || _closeOpen == null)
        {
            _outsideClick.Stop();
            return;
        }
        bool down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0 ||
                    (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0;
        if (down && !_mouseWasDown && Win32.GetCursorPos(out var pt) &&
            !IsOver(_openContent, pt) && !IsOver(_openAnchor, pt))
            ClosePopup(); // a click on its own bar item is handled by the item (toggle)
        _mouseWasDown = down;
    }

    private static bool IsOver(FrameworkElement? element, Win32.POINT pt)
    {
        if (element == null || PresentationSource.FromVisual(element) == null) return false;
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return pt.X >= topLeft.X && pt.X < bottomRight.X && pt.Y >= topLeft.Y && pt.Y < bottomRight.Y;
    }

    /// <summary>Shows a light macOS menu under <paramref name="anchor"/>.</summary>
    private void ShowMenu(Border anchor, IEnumerable<object> items)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("MacMenu"),
            ItemContainerStyle = (Style)FindResource("MacMenuItem"),
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 3,
            StaysOpen = true, // outside clicks are handled by WatchOutsideClick (the bar never has focus)
        };
        foreach (var item in items) menu.Items.Add(item);
        menu.Closed += (_, _) => OnOpenClosed(anchor, menu);
        Glass.Attach(menu, Glass.Radius(12));
        TrackOpen(anchor, menu, () => menu.IsOpen = false);
        menu.IsOpen = true;
    }

    private Separator Separator() => new() { Style = (Style)FindResource("MacMenuSeparator") };

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>macOS-style row: small monochrome icon, label, and a dimmed keyboard shortcut on the right.</summary>
    private static MenuItem Item(string glyph, string header, Action action, string? shortcut = null, bool enabled = true)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Shortcut" });
        var icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
        var label = new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(icon);
        row.Children.Add(label);
        if (shortcut != null)
        {
            var keys = new TextBlock { Text = shortcut, Opacity = 0.5, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Grid.SetColumn(keys, 2);
            row.Children.Add(keys);
        }
        var item = new MenuItem { Header = row, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---------------- Menus ----------------

    private void OpenLogoMenu(Border anchor) => ShowMenu(anchor, new object[]
    {
        Item("\uE7F4", "About This PC", () => SystemSettingsWindow.Open("about")),
        Separator(),
        Item("\uE713", "System Settings…", () => SystemSettingsWindow.Open()),
        Item("\uE719", "Microsoft Store…", () => Shell.Open("ms-windows-store:")),
        Separator(),
        Item("\uE711", "Force Quit…", () => Shell.Open("taskmgr.exe"), "Ctrl+Shift+Esc"),
        Separator(),
        Item("\uE708", "Sleep", PowerActions.Sleep),
        Item("\uE72C", "Restart…", PowerActions.Restart),
        Item("\uE7E8", "Shut Down…", PowerActions.ShutDown),
        Separator(),
        Item("\uE72E", "Lock Screen", PowerActions.Lock, "Win+L"),
        Item("\uE77B", $"Log Out {UserInfo.UserName}…", PowerActions.SignOut),
    });

    private void OpenAppMenu(Border anchor)
    {
        string name = _appName.Text;
        var windows = AppWindows();
        bool hasApp = _appExe != null && windows.Count > 0;
        ShowMenu(anchor, new object[]
        {
            Item($"Hide {name}", () => { foreach (var w in AppWindows()) WindowActivator.Minimize(w); }, hasApp),
            Item("Hide Others", HideOthers, hasApp),
            Item("Show All", ShowAll),
            Separator(),
            Item($"Quit {name}", () => { foreach (var w in AppWindows()) WindowActivator.Close(w); }, hasApp),
        });
    }

    private void OpenWindowMenu(Border anchor)
    {
        bool hasWindow = _appWindow != IntPtr.Zero && Win32.IsWindow(_appWindow) && !Win32.IsIconic(_appWindow);
        ShowMenu(anchor, new object[]
        {
            Item("Minimize", () => WindowActivator.Minimize(_appWindow), hasWindow),
            Item("Zoom", () => Win32.ShowWindow(_appWindow, Win32.IsZoomed(_appWindow) ? Win32.SW_RESTORE : Win32.SW_MAXIMIZE), hasWindow),
            Separator(),
            Item("Bring All to Front", () =>
            {
                var windows = AppWindows();
                for (int i = windows.Count - 1; i >= 0; i--) WindowActivator.Activate(windows[i]);
            }, _appExe != null),
        });
    }

    private void OpenLanguageMenu(Border anchor)
    {
        var current = SystemStatus.GetCurrentLayout();
        var target = _appWindow;
        var items = new List<object>();
        foreach (var layout in SystemStatus.GetInstalledLayouts())
        {
            bool on = layout.Handle == current.Handle;
            items.Add(Item((on ? "✓  " : "     ") + layout.Name, () =>
            {
                SystemStatus.SwitchLayout(target, layout);
                _language.Text = layout.Code;
            }));
        }
        items.Add(Separator());
        items.Add(Item("Open Keyboard Settings…", () => SystemSettingsWindow.OpenUri("ms-settings:regionlanguage")));
        ShowMenu(anchor, items);
    }

    private void OpenBatteryMenu(Border anchor)
    {
        var b = SystemStatus.GetBattery();
        ShowMenu(anchor, new object[]
        {
            Item($"Battery  {b.Percent}%", () => { }, enabled: false),
            Item(b.Charging ? "Power Source: Power Adapter" : "Power Source: Battery", () => { }, enabled: false),
            Separator(),
            Item("Battery Settings…", () => SystemSettingsWindow.OpenUri("ms-settings:batterysaver")),
        });
    }

    private void OpenControlCenter(Border anchor, bool soundOnly)
    {
        var panel = new ControlCenterPanel(_volume, _networkState, soundOnly, () => OpenNetworkMenu(_networkButton), _menuDark);
        var popup = new Popup
        {
            Child = panel,
            AllowsTransparency = true,
            StaysOpen = true, // closed by WatchOutsideClick (see there why)
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 5,
            HorizontalOffset = anchor.ActualWidth - panel.Width + 6, // right edges aligned, like macOS
            PopupAnimation = PopupAnimation.Fade,
        };
        Glass.Attach(popup, panel, Glass.Radius(24));
        panel.Done += () => popup.IsOpen = false;
        popup.Closed += (_, _) =>
        {
            OnOpenClosed(anchor, panel);
            UpdateVolume();
        };
        TrackOpen(anchor, panel, () => popup.IsOpen = false);
        popup.IsOpen = true;
    }

    /// <summary>Right-click menu of a background app (Windows 11): open, quit, force quit.</summary>
    private void ShowTrayAppMenu(Border anchor, TrayIcon icon)
    {
        string name = icon.Tip.Split('\n')[0].Trim();
        if (name.Length > 40) name = name[..40] + "…";
        ShowMenu(anchor, new object[]
        {
            Item(name, () => { }, enabled: false),
            Separator(),
            Item("Open", () => TrayIcons.Open(icon)),
            Item($"Quit {name}", () => TrayIcons.Quit(icon)),
            Item("Force Quit", () => TrayIcons.ForceQuit(icon)),
        });
    }

    /// <summary>Icons of apps running in the background (the taskbar's "^" hidden-icons area).</summary>
    private void OpenTrayIcons(Border anchor)
    {
        if (!TrayIcons.IsSupported)
        {
            ShowMenu(anchor, new object[]
            {
                Item("Background app icons aren't available on this Windows version", () => { }, enabled: false),
                Separator(),
                Item("Taskbar Settings…", () => SystemSettingsWindow.OpenUri("ms-settings:taskbar")),
            });
            return;
        }

        var icons = TrayIcons.GetIcons();
        var grid = new UniformGrid { Columns = Math.Clamp(icons.Count, 1, 6) };
        Popup? popup = null;
        foreach (var icon in icons)
        {
            var image = new Image
            {
                Source = icon.Image, Width = 18, Height = 18, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var cell = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent,
                ToolTip = string.IsNullOrEmpty(icon.Tip) ? null : icon.Tip,
                Child = image,
            };
            cell.MouseEnter += (_, _) => cell.Background = GlassTheme.Hover;
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            var target = icon;
            cell.MouseLeftButtonUp += (_, _) =>
            {
                popup?.SetCurrentValue(Popup.IsOpenProperty, false);
                if (TrayIcons.ForwardsClicks(target)) TrayIcons.Click(target, right: false);
                else TrayIcons.Open(target);
            };
            cell.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                popup?.SetCurrentValue(Popup.IsOpenProperty, false);
                // Windows 10: the app's own menu (it has Exit/Quit). Windows 11: MacDock's menu.
                if (TrayIcons.ForwardsClicks(target)) TrayIcons.Click(target, right: true);
                else ShowTrayAppMenu(anchor, target);
            };
            grid.Children.Add(cell);
        }
        if (icons.Count == 0)
            grid.Children.Add(new TextBlock { Text = "No background apps", Foreground = GlassTheme.Foreground, Opacity = 0.6, Margin = new Thickness(10, 6, 10, 6), FontSize = 13 });

        double radius = Glass.Radius(12);
        var panel = new Border
        {
            Child = grid,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(radius),
            Background = GlassTheme.Background, // the dock's glass
            BorderBrush = GlassTheme.Rim,
            BorderThickness = new Thickness(1),
        };
        var trayPopup = new Popup
        {
            Child = panel, AllowsTransparency = true, StaysOpen = true, PlacementTarget = anchor,
            Placement = PlacementMode.Bottom, VerticalOffset = 4, PopupAnimation = PopupAnimation.Fade,
        };
        Glass.Attach(trayPopup, panel, radius);
        popup = trayPopup;
        trayPopup.Closed += (_, _) => OnOpenClosed(anchor, panel);
        TrackOpen(anchor, panel, () => trayPopup.IsOpen = false);
        trayPopup.IsOpen = true;
    }

    /// <summary>macOS-style network menu: the connection, its address, nearby Wi-Fi networks, settings.</summary>
    private void OpenNetworkMenu(Border anchor)
    {
        var items = new List<object>();
        var state = _networkState;
        var wifi = SystemStatus.GetWifiNetworks();

        if (state.Kind == NetworkKind.Ethernet)
        {
            items.Add(Item("Ethernet", () => { }, enabled: false));
            items.Add(Item("✓  Connected", () => { }, enabled: false));
            if (SystemStatus.GetIpAddress() is { } ip) items.Add(Item($"IP Address: {ip}", () => { }, enabled: false));
        }
        else if (state.Kind == NetworkKind.None && wifi.Count == 0)
        {
            items.Add(Item("Not Connected", () => { }, enabled: false));
        }

        if (wifi.Count > 0)
        {
            if (items.Count > 0) items.Add(Separator());
            items.Add(Item("Wi-Fi", () => { }, enabled: false));
            foreach (var network in wifi.Take(12))
                items.Add(WifiItem(network));
            items.Add(Item("Other Network…", () => SystemSettingsWindow.OpenUri("ms-settings:network-wifi")));
        }

        items.Add(Separator());
        items.Add(Item("Network Settings…", () => Shell.Open(state.Kind == NetworkKind.Wifi || wifi.Count > 0
            ? "ms-settings:network-wifi" : "ms-settings:network-ethernet")));
        ShowMenu(anchor, items);
    }

    /// <summary>One Wi-Fi network: check mark when connected, name, lock when secured, signal fan.</summary>
    private MenuItem WifiItem(WifiNetwork network)
    {
        var row = new DockPanel { MinWidth = 220, LastChildFill = true };
        var signal = new MenuBarIcon { Glyph = MenuBarGlyph.Wifi, Level = network.Signal / 100.0, Foreground = TextBrush, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(signal, Dock.Right);
        row.Children.Add(signal);
        if (network.Secured)
        {
            var lockGlyph = new TextBlock { Text = "\uE72E", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(lockGlyph, Dock.Right);
            row.Children.Add(lockGlyph);
        }
        row.Children.Add(new TextBlock { Text = (network.Connected ? "✓  " : "     ") + network.Ssid, VerticalAlignment = VerticalAlignment.Center });

        var item = new MenuItem { Header = row };
        item.Click += (_, _) =>
        {
            if (network.Connected) return;
            // Saved networks join directly; new ones need a password: Windows' Wi-Fi settings page.
            if (!SystemStatus.ConnectWifi(network)) SystemSettingsWindow.OpenUri("ms-settings:network-wifi");
            else Dispatcher.BeginInvoke(UpdateNetwork, DispatcherPriority.Background);
        };
        return item;
    }

    /// <summary>Notification Center (Windows' Action Center), opened with its own shortcut.</summary>
    private static void OpenNotifications()
    {
        // Windows 11 has a separate notification panel (Win+N); Windows 10 shows them in Win+A.
        byte key = Environment.OSVersion.Version.Build >= 22000 ? (byte)'N' : (byte)'A';
        var marker = WinKeyInterceptor.Marker; // our own Win key hook leaves these alone
        Win32.keybd_event(Win32.VK_LWIN, 0, Win32.KEYEVENTF_EXTENDEDKEY, marker);
        Win32.keybd_event(key, 0, 0, marker);
        Win32.keybd_event(key, 0, Win32.KEYEVENTF_KEYUP, marker);
        Win32.keybd_event(Win32.VK_LWIN, 0, Win32.KEYEVENTF_EXTENDEDKEY | Win32.KEYEVENTF_KEYUP, marker);
    }

    // ---------------- File, Edit, View, Go, Window, Help ----------------

    /// <summary>The macOS menu titles after the app name: Finder gets its own menus (with Go); other apps
    /// get the standard ones, which send the app its usual keyboard shortcuts.</summary>
    private void BuildMenus(bool finder)
    {
        if (_menusForFinder == finder) return;
        _menusForFinder = finder;
        _menuStrip.Children.Clear();
        _menuStrip.Children.Add(Button(BarText("File"), b => OpenFileMenu(b, finder)));
        _menuStrip.Children.Add(Button(BarText("Edit"), b => OpenEditMenu(b, finder)));
        _menuStrip.Children.Add(Button(BarText("View"), b => OpenViewMenu(b, finder)));
        if (finder) _menuStrip.Children.Add(Button(BarText("Go"), OpenGoMenu));
        _menuStrip.Children.Add(Button(BarText("Window"), OpenWindowMenu));
        _menuStrip.Children.Add(Button(BarText("Help"), OpenHelpMenu));
    }

    private void RunFinder(string command) => MacDock.Finder.FinderWindow.MenuCommand(command, _appWindow);

    private static MenuItem Row(string header, Action action, string? shortcut = null, bool enabled = true, bool check = false) =>
        Item(check ? "" : "", header, action, shortcut, enabled);

    private void OpenFileMenu(Border anchor, bool finder)
    {
        if (finder)
        {
            var st = MacDock.Finder.FinderWindow.MenuState(_appWindow);
            bool win = MacDock.Finder.FinderWindow.IsFinderWindow(_appWindow);
            ShowMenu(anchor, new object[]
            {
                Row("New Finder Window", () => RunFinder("newwindow"), "Ctrl+N"),
                Row("New Folder", () => RunFinder("newfolder"), "Ctrl+Shift+N"),
                Separator(),
                Row("Open", () => RunFinder("open"), "Ctrl+↓", win && st.HasSelection),
                Row("Close Window", () => RunFinder("close"), "Ctrl+W", win),
                Separator(),
                Row("Get Info", () => RunFinder("getinfo"), "Ctrl+I", win),
                Row("Rename", () => RunFinder("rename"), "F2", win && st.HasSelection),
                Row("Compress", () => RunFinder("compress"), null, win && st.HasSelection),
                Row("Duplicate", () => RunFinder("duplicate"), "Ctrl+D", win && st.HasSelection),
                Row("Quick Look", () => RunFinder("quicklook"), "Space", win && st.HasSelection),
                Separator(),
                Row("Move to Trash", () => RunFinder("trash"), "Ctrl+⌫", win && st.HasSelection),
                Separator(),
                Row("Find", () => RunFinder("search"), "Ctrl+F"),
            });
            return;
        }
        bool app = HasAppWindow;
        ShowMenu(anchor, new object[]
        {
            Row("New", () => SendShortcut(VK_CONTROL, 0x4E), "Ctrl+N", app),
            Row("Open…", () => SendShortcut(VK_CONTROL, 0x4F), "Ctrl+O", app),
            Separator(),
            Row("Close Window", () => SendShortcut(VK_CONTROL, 0x57), "Ctrl+W", app),
            Row("Save", () => SendShortcut(VK_CONTROL, 0x53), "Ctrl+S", app),
            Row("Save As…", () => SendShortcut(VK_CONTROL, VK_SHIFT, 0x53), "Ctrl+Shift+S", app),
            Separator(),
            Row("Print…", () => SendShortcut(VK_CONTROL, 0x50), "Ctrl+P", app),
        });
    }

    private void OpenEditMenu(Border anchor, bool finder)
    {
        if (finder)
        {
            bool win = MacDock.Finder.FinderWindow.IsFinderWindow(_appWindow);
            var st = MacDock.Finder.FinderWindow.MenuState(_appWindow);
            ShowMenu(anchor, new object[]
            {
                Row("Cut", () => RunFinder("cut"), "Ctrl+X", win && st.HasSelection),
                Row("Copy", () => RunFinder("copy"), "Ctrl+C", win && st.HasSelection),
                Row("Paste Item", () => RunFinder("paste"), "Ctrl+V", win),
                Row("Select All", () => RunFinder("selectall"), "Ctrl+A", win),
                Separator(),
                Row("Copy as Pathname", () => RunFinder("copypath"), null, win),
            });
            return;
        }
        bool app = HasAppWindow;
        ShowMenu(anchor, new object[]
        {
            Row("Undo", () => SendShortcut(VK_CONTROL, 0x5A), "Ctrl+Z", app),
            Row("Redo", () => SendShortcut(VK_CONTROL, 0x59), "Ctrl+Y", app),
            Separator(),
            Row("Cut", () => SendShortcut(VK_CONTROL, 0x58), "Ctrl+X", app),
            Row("Copy", () => SendShortcut(VK_CONTROL, 0x43), "Ctrl+C", app),
            Row("Paste", () => SendShortcut(VK_CONTROL, 0x56), "Ctrl+V", app),
            Row("Select All", () => SendShortcut(VK_CONTROL, 0x41), "Ctrl+A", app),
            Separator(),
            Row("Find…", () => SendShortcut(VK_CONTROL, 0x46), "Ctrl+F", app),
            Separator(),
            Row("Emoji & Symbols", () => SendShortcut(Win32.VK_LWIN, 0xBE), "Win+.", app),
        });
    }

    private void OpenViewMenu(Border anchor, bool finder)
    {
        if (finder)
        {
            var st = MacDock.Finder.FinderWindow.MenuState(_appWindow);
            bool win = MacDock.Finder.FinderWindow.IsFinderWindow(_appWindow);
            ShowMenu(anchor, new object[]
            {
                Row("as Icons", () => RunFinder("icons"), "Ctrl+1", win, st.View == "Icons"),
                Row("as List", () => RunFinder("list"), "Ctrl+2", win, st.View == "List"),
                Row("as Columns", () => RunFinder("columns"), "Ctrl+3", win, st.View == "Columns"),
                Separator(),
                Row(st.PathBar ? "Hide Path Bar" : "Show Path Bar", () => RunFinder("pathbar"), null, win),
                Row(st.StatusBar ? "Hide Status Bar" : "Show Status Bar", () => RunFinder("statusbar"), null, win),
                Row(st.Hidden ? "Hide Hidden Files" : "Show Hidden Files", () => RunFinder("hidden"), "Ctrl+Shift+.", win),
            });
            return;
        }
        bool app = HasAppWindow;
        ShowMenu(anchor, new object[]
        {
            Row("Zoom In", () => SendShortcut(VK_CONTROL, 0xBB), "Ctrl++", app),
            Row("Zoom Out", () => SendShortcut(VK_CONTROL, 0xBD), "Ctrl+−", app),
            Row("Actual Size", () => SendShortcut(VK_CONTROL, 0x30), "Ctrl+0", app),
            Separator(),
            Row("Enter Full Screen", () => SendShortcut(0x7A), "F11", app),
        });
    }

    private void OpenGoMenu(Border anchor)
    {
        bool win = MacDock.Finder.FinderWindow.IsFinderWindow(_appWindow);
        ShowMenu(anchor, new object[]
        {
            Row("Back", () => RunFinder("back"), "Ctrl+[", win),
            Row("Forward", () => RunFinder("forward"), "Ctrl+]", win),
            Row("Enclosing Folder", () => RunFinder("up"), "Ctrl+↑", win),
            Separator(),
            Row("Recents", () => RunFinder("recents"), "Ctrl+Shift+F"),
            Row("Documents", () => RunFinder("documents"), "Ctrl+Shift+O"),
            Row("Desktop", () => RunFinder("desktop"), "Ctrl+Shift+D"),
            Row("Downloads", () => RunFinder("downloads"), "Ctrl+Shift+L"),
            Row("Home", () => RunFinder("home"), "Ctrl+Shift+H"),
            Row("Computer", () => RunFinder("computer"), "Ctrl+Shift+C"),
            Row("Applications", () => RunFinder("applications"), "Ctrl+Shift+A"),
            Separator(),
            Row("Go to Folder…", () => RunFinder("goto"), "Ctrl+Shift+G"),
        });
    }

    private void OpenHelpMenu(Border anchor)
    {
        string name = _appName.Text;
        bool app = HasAppWindow && _appExe != null;
        ShowMenu(anchor, new object[]
        {
            Row("Search", () => SearchRequested?.Invoke()),
            Separator(),
            Row($"{name} Help", () => SendShortcut(0x70), "F1", app),
            Row("Tips for Windows", () => Shell.Open("ms-get-started:")),
        });
    }

    private const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10;

    private bool HasAppWindow => _appWindow != IntPtr.Zero && Win32.IsWindow(_appWindow);

    /// <summary>Brings the app back to the front and presses the shortcut for it (the menu had the focus).</summary>
    private void SendShortcut(params byte[] keys)
    {
        var target = _appWindow;
        if (target == IntPtr.Zero || !Win32.IsWindow(target)) return;
        WindowActivator.Activate(target);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOTOWNER) != target) WindowActivator.ForceForeground(target);
            var marker = WinKeyInterceptor.Marker; // our own Win key hook leaves these alone
            foreach (var k in keys) Win32.keybd_event(k, 0, k == Win32.VK_LWIN ? Win32.KEYEVENTF_EXTENDEDKEY : 0, marker);
            for (int i = keys.Length - 1; i >= 0; i--)
                Win32.keybd_event(keys[i], 0, (keys[i] == Win32.VK_LWIN ? Win32.KEYEVENTF_EXTENDEDKEY : 0) | Win32.KEYEVENTF_KEYUP, marker);
        };
        timer.Start();
    }

    // ---------------- App in front ----------------

    private void OnForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (Win32.GetWindowThreadProcessId(hwnd, out uint fgPid) != 0 && fgPid != Environment.ProcessId)
            ClosePopup(); // another app came forward: the panel goes away, like macOS
        IntPtr root = Win32.GetAncestor(hwnd, Win32.GA_ROOTOWNER);
        if (root == IntPtr.Zero) root = hwnd;
        Win32.GetWindowThreadProcessId(root, out uint pid);
        if (pid == Environment.ProcessId)
        {
            if (Finder.FinderWindow.IsFinderWindow(root))
            {
                // MacDock's own Finder window is an app of its own, like on macOS.
                _appWindow = root;
                _appExe = null;
                _appName.Text = "Finder";
                BuildMenus(finder: true);
            }
            return; // the dock, Start menu, this bar: keep showing the real app
        }

        var cls = ClassOf(root);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            _appWindow = IntPtr.Zero;
            _appExe = ExePath(root);
            _appName.Text = "Finder"; // the desktop belongs to Finder, like on macOS
            BuildMenus(finder: true);
            return;
        }
        if (cls is "Windows.UI.Core.CoreWindow" or "ForegroundStaging" or "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow")
            return; // shell overlays (Start, Task View, Action Center): not an app

        _appWindow = root;
        _appExe = ExePath(root);
        _appName.Text = AppName(root, _appExe);
        BuildAppMenus(root, _appExe);
    }

    // ---------------- The app's own menus, in the top bar ----------------

    /// <summary>
    /// Like macOS: the app in front shows its own menus next to its name. Classic Windows apps (Notepad...)
    /// show their real menus (taken off their window); VS Code and the editors built on it show theirs
    /// (opened in the editor with its own keys); other apps get the standard File / Edit / View menus.
    /// </summary>
    private void BuildAppMenus(IntPtr root, string? exe)
    {
        try
        {
            if (GlobalMenus.TryGet(root, out var menu)) { BuildWin32Menus(root, menu); return; }
            if (IsCodeEditor(exe)) { BuildEditorMenus(); return; }
        }
        catch (Exception ex)
        {
            CrashLog.Write("MenuBar.BuildAppMenus", ex);
        }
        BuildMenus(finder: false);
    }

    private void BuildWin32Menus(IntPtr hwnd, IntPtr menu)
    {
        _menusForFinder = null; // the standard menus are rebuilt next time they are needed
        _menuStrip.Children.Clear();
        var top = GlobalMenus.Read(menu);
        for (int i = 0; i < top.Count; i++)
        {
            var entry = top[i];
            if (entry.IsSeparator || entry.Text.Length == 0) continue;
            int index = i;
            _menuStrip.Children.Add(Button(BarText(entry.Text), b => OpenWin32Menu(b, hwnd, menu, index, entry)));
        }
    }

    private void OpenWin32Menu(Border anchor, IntPtr hwnd, IntPtr menu, int index, GlobalMenus.Entry entry)
    {
        IntPtr sub = GlobalMenus.SubMenuAt(menu, index);
        if (sub == IntPtr.Zero)
        {
            RunWin32(hwnd, entry.Id); // a command right in the bar
            return;
        }
        GlobalMenus.Prepare(hwnd, sub, index);
        var items = new List<object>();
        AddWin32Items(items, hwnd, sub, indent: "");
        while (items.Count > 0 && items[^1] is System.Windows.Controls.Separator) items.RemoveAt(items.Count - 1);
        if (items.Count > 0) ShowMenu(anchor, items);
    }

    /// <summary>The items of a Windows menu as macOS rows; submenus are shown inline under their title.</summary>
    private void AddWin32Items(List<object> items, IntPtr hwnd, IntPtr menu, string indent)
    {
        var entries = GlobalMenus.Read(menu);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.IsSeparator)
            {
                if (items.Count > 0 && items[^1] is not System.Windows.Controls.Separator) items.Add(Separator());
                continue;
            }
            if (e.Text.Length == 0) continue;
            if (e.SubMenu != IntPtr.Zero)
            {
                if (indent.Length > 0) continue; // two levels deep: rare, left out
                GlobalMenus.Prepare(hwnd, e.SubMenu, i);
                items.Add(Row(e.Text, () => { }, null, enabled: false));
                AddWin32Items(items, hwnd, e.SubMenu, "      ");
                continue;
            }
            uint id = e.Id;
            items.Add(Row(indent + e.Text, () => RunWin32(hwnd, id), e.Shortcut, e.Enabled, e.Checked));
        }
    }

    /// <summary>Brings the app forward and runs the command, exactly as its own menu would.</summary>
    private void RunWin32(IntPtr hwnd, uint id)
    {
        if (!Win32.IsWindow(hwnd)) return;
        WindowActivator.Activate(hwnd);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            GlobalMenus.Invoke(hwnd, id);
        };
        timer.Start();
    }

    // VS Code's menus (and the editors built on it): each opens in the editor with its Alt key.
    private static readonly (string Name, byte Key)[] EditorMenus =
    {
        ("File", (byte)'F'), ("Edit", (byte)'E'), ("Selection", (byte)'S'), ("View", (byte)'V'),
        ("Go", (byte)'G'), ("Run", (byte)'R'), ("Terminal", (byte)'T'), ("Help", (byte)'H'),
    };

    private void BuildEditorMenus()
    {
        _menusForFinder = null;
        _menuStrip.Children.Clear();
        foreach (var (name, key) in EditorMenus)
            _menuStrip.Children.Add(Button(BarText(name), _ => SendShortcut(0x12 /* Alt */, key)));
    }

    private static readonly Dictionary<string, bool> EditorCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>VS Code, Antigravity IDE, Cursor, Windsurf...: a resources\app\product.json next to the exe
    /// (or in its version folder).</summary>
    private static bool IsCodeEditor(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        if (EditorCache.TryGetValue(exe, out bool known)) return known;
        bool found = false;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(exe);
            if (dir != null)
            {
                found = System.IO.File.Exists(System.IO.Path.Combine(dir, "resources", "app", "product.json")) ||
                        System.IO.Directory.EnumerateDirectories(dir).Any(d => System.IO.File.Exists(System.IO.Path.Combine(d, "resources", "app", "product.json")));
            }
        }
        catch { /* not readable: not an editor */ }
        EditorCache[exe] = found;
        return found;
    }

    private string AppName(IntPtr hwnd, string? exe)
    {
        if (exe == null) return WindowTitle(hwnd);
        var file = Path.GetFileName(exe);
        if (file.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return "File Explorer";
        if (file.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return Shorten(WindowTitle(hwnd)); // Store apps
        return _appNames.GetOrAdd(exe, path =>
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description)) return Shorten(description);
            }
            catch
            {
                // Fall back to the file name.
            }
            return Path.GetFileNameWithoutExtension(path);
        });
    }

    /// <summary>Menu bar app names stay short ("Settings - Display" becomes "Settings").</summary>
    private static string Shorten(string name)
    {
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) name = name[..dash];
        return name.Length > 40 ? name[..40] + "…" : name;
    }

    private List<IntPtr> AppWindows()
    {
        if (_appExe == null) return new List<IntPtr>();
        var exe = PathHelper.Normalize(_appExe);
        return WindowEnumerator.GetTaskbarWindows().Where(w => PathHelper.Comparer.Equals(w.ExePath, exe)).Select(w => w.Hwnd).ToList();
    }

    private void HideOthers()
    {
        var exe = _appExe == null ? null : PathHelper.Normalize(_appExe);
        foreach (var (hwnd, path) in WindowEnumerator.GetTaskbarWindows())
            if (exe == null || !PathHelper.Comparer.Equals(path, exe)) WindowActivator.Minimize(hwnd);
    }

    private static void ShowAll()
    {
        foreach (var (hwnd, _) in WindowEnumerator.GetTaskbarWindows())
            if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        int length = Win32.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var sb = new StringBuilder(length + 1);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ExePath(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Win32.QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    // ---------------- Status items ----------------

    /// <summary>"Sun 8 Nov 18:45" in the user's own date/time format; ticks exactly on the minute.</summary>
    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        bool monthFirst = culture.DateTimeFormat.MonthDayPattern.TrimStart().StartsWith('M');
        string date = now.ToString(monthFirst ? "ddd MMM d" : "ddd d MMM", culture);
        _clock.Text = $"{date}   {now.ToString(culture.DateTimeFormat.ShortTimePattern, culture)}";

        _clockTimer.Stop();
        _clockTimer.Interval = TimeSpan.FromMilliseconds(60_000 - now.Second * 1000 - now.Millisecond + 30);
        _clockTimer.Start();
    }

    private void UpdateLanguage()
    {
        var code = SystemStatus.GetCurrentLayout().Code;
        if (_language.Text != code) _language.Text = code;
    }

    private void UpdateVolume()
    {
        var level = _volume.Level;
        _speaker.Visibility = level == null ? Visibility.Collapsed : Visibility.Visible;
        if (level == null) return;
        _speaker.Level = level.Value;
        _speaker.Muted = _volume.Muted;
    }

    private void NudgeVolume(int delta)
    {
        if (_volume.Level is not float level) return;
        _volume.Level = Math.Clamp(level + Math.Sign(delta) * 0.04f, 0f, 1f);
        UpdateVolume();
    }

    private void UpdateBattery()
    {
        var b = SystemStatus.GetBattery();
        _batteryButton.Visibility = b.Present ? Visibility.Visible : Visibility.Collapsed;
        if (!b.Present) return;
        _battery.Level = b.Percent / 100.0;
        _battery.Muted = b.Charging; // the icon shows a bolt
        _batteryText.Text = $"{b.Percent}%";
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateNetwork);

    private void UpdateNetwork()
    {
        if (_networkBusy) return;
        _networkBusy = true;
        Task.Run(SystemStatus.GetNetwork).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            _networkBusy = false;
            if (task.Status != TaskStatus.RanToCompletion) return;
            _networkState = task.Result;
            _network.Glyph = _networkState.Kind switch
            {
                NetworkKind.Wifi => MenuBarGlyph.Wifi,
                NetworkKind.Ethernet => MenuBarGlyph.Ethernet,
                _ => MenuBarGlyph.Offline,
            };
            _network.Level = _networkState.SignalPercent / 100.0;
        }));
    }
}
