using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MacDock.Config;
using MacDock.Models;
using MacDock.Native;
using MacDock.Services;

namespace MacDock;

/// <summary>What the Start menu needs from the dock.</summary>
public interface IStartMenuHost
{
    bool IsPinnedToDock(StartApp app);
    void PinToDock(StartApp app);
    void UnpinFromDock(StartApp app);
    void SaveConfig();
}

/// <summary>
/// Clean, solid light Start menu (macOS-style): Spotlight search with a Top Hit, pinned apps on
/// uniform rounded tiles, an All Apps list, account and power. No ads, no "recommended", no web results.
/// </summary>
public partial class StartMenuWindow : Window
{
    /// <summary>Pinned tile icon size (DIPs). The catalog renders icons at exactly this size in device pixels.</summary>
    public const double TileIconSize = 48;

    /// <summary>List row icon size (DIPs), also pre-rendered at exact size.</summary>
    public const double RowIconSize = 26;

    private const double PanelWidth = 640, PanelHeight = 580;
    private const int Columns = 6;
    private const int MaxPins = 42;
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    /// <summary>Picked on first run, in this order, when installed.</summary>
    private static readonly string[] DefaultPinNames =
    {
        "File Explorer", "Settings", "Microsoft Edge", "Google Chrome", "Firefox", "Visual Studio Code",
        "Claude", "Notepad", "Calculator", "Microsoft Store", "Photos", "Paint", "Snipping Tool",
        "Task Manager", "Windows PowerShell", "Terminal", "Command Prompt", "Control Panel",
    };

    private enum View { Home, AllApps, Search }

    private readonly DockConfig _config;
    private readonly AppCatalog _catalog;
    private readonly IStartMenuHost _host;
    private readonly DispatcherTimer _clickWatcher;
    private readonly BlurBackdrop _backdrop = new(); // standard blur: respects the window region exactly
    private const double CornerRadius = 16;

    private View _view = View.Home;
    private bool _open;
    private int _generation;
    private DateTime _lastAutoHide = DateTime.MinValue;
    private bool _popupOpen;
    private DateTime _popupClosedAt = DateTime.MinValue;
    private bool _mouseWasDown;

    internal StartMenuWindow(DockConfig config, AppCatalog catalog, IStartMenuHost host)
    {
        _config = config;
        _catalog = catalog;
        _host = host;
        InitializeComponent();

        Width = PanelWidth + Frame.Margin.Left + Frame.Margin.Right;

        // Light blur behind the translucent panel (owned windows stay above their owner).
        new WindowInteropHelper(this).Owner = _backdrop.Handle;
        _backdrop.SetVisible(false);
        Closed += (_, _) => _backdrop.Dispose();
        Frame.SizeChanged += (_, _) => ClipShadowToOutside();

        UserNameText.Text = UserInfo.UserName;
        UserInitial.Text = UserInfo.UserName.Length > 0 ? char.ToUpperInvariant(UserInfo.UserName[0]).ToString() : "?";
        if (UserInfo.LoadAccountPicture() is { } picture)
        {
            UserPicture.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
            UserPicture.Visibility = Visibility.Visible;
            UserInitial.Visibility = Visibility.Collapsed;
        }

        SourceInitialized += (_, _) =>
            WindowEffects.AddExStyle(new WindowInteropHelper(this).Handle, Win32.WS_EX_TOOLWINDOW);
        Deactivated += (_, _) => { if (!_popupOpen) HideMenu(byOutsideClick: true); };
        PreviewKeyDown += OnPreviewKeyDown;
        SearchBox.TextChanged += (_, _) => UpdateSearch();

        foreach (var list in new[] { PinnedGrid, AppList })
        {
            list.PreviewMouseLeftButtonUp += (_, e) => { if (ItemUnderMouse(e) is { } app) Launch(app); };
            list.PreviewMouseRightButtonUp += (_, e) =>
            {
                if (ItemUnderMouse(e) is not { } app) return;
                e.Handled = true;
                ShowAppMenu(app);
            };
        }
        _catalog.Loaded += Rebuild;

        _clickWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _clickWatcher.Tick += (_, _) => WatchOutsideClicks();
    }

    public bool IsOpen => _open;

    private ListBox ActiveList => _view == View.Home ? PinnedGrid : AppList;

    // ---------------- Show / hide ----------------

    /// <summary>Opens centered on <paramref name="bottomCenter"/> (screen DIPs), growing upward.</summary>
    public void Toggle(Point bottomCenter)
    {
        if (_open)
        {
            HideMenu(byOutsideClick: false);
            return;
        }
        // The click that just closed us (outside click on the Start icon) must not re-open.
        if (DateTime.UtcNow - _lastAutoHide < ReopenGuard) return;
        ShowMenu(bottomCenter);
    }

    /// <summary>
    /// Creates the window and lays out its content once, off screen and without taking focus,
    /// so the first real open has no stutter.
    /// </summary>
    public void Prewarm()
    {
        if (_prewarmed || _open) return;
        _prewarmed = true;
        ShowActivated = false;
        Opacity = 0;
        Left = -20000;
        Top = -20000;
        SwitchView(View.Home);
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = true;
    }

    private bool _prewarmed;

    public void ShowMenu(Point bottomCenter)
    {
        _open = true;
        int generation = ++_generation;
        _prewarmed = true;

        double panelHeight = Math.Min(PanelHeight, bottomCenter.Y - 16);
        Height = panelHeight + Frame.Margin.Top + Frame.Margin.Bottom;
        Left = Math.Round(bottomCenter.X - Width / 2);
        Top = Math.Round(bottomCenter.Y - panelHeight - Frame.Margin.Top);

        SearchBox.Text = "";
        SwitchView(View.Home);

        // Start fully transparent: the window must never draw a frame at full opacity before the fade.
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        UpdateLayout();
        PlaceBackdrop();
        WindowActivator.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
        Keyboard.Focus(SearchBox);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        fadeIn.Completed += (_, _) =>
        {
            if (generation != _generation) return;
            Opacity = 1;
            BeginAnimation(OpacityProperty, null);
            // The blur comes in only once the panel covers it, so it never shows on its own.
            _backdrop.SetVisible(true);
        };
        BeginAnimation(OpacityProperty, fadeIn);

        _mouseWasDown = AnyMouseButtonDown();
        _clickWatcher.Start();
        _ = _catalog.RefreshIfStaleAsync();
    }

    public void HideMenu() => HideMenu(byOutsideClick: false);

    private void HideMenu(bool byOutsideClick)
    {
        if (!_open) return;
        _open = false;
        if (byOutsideClick) _lastAutoHide = DateTime.UtcNow;
        _clickWatcher.Stop();
        int generation = ++_generation;
        _backdrop.SetVisible(false); // the 94% panel hides this change; the blur never lingers alone

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(100)) { FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            if (generation != _generation) return; // re-opened meanwhile
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // ---------------- Outside clicks ----------------

    /// <summary>
    /// Puts the blur window behind the panel, inset by 2 px on every side (with a matching smaller
    /// radius) so its hard-edged region always stays inside the smooth rounded corners: nothing can
    /// show outside them.
    /// </summary>
    private void PlaceBackdrop()
    {
        if (PresentationSource.FromVisual(Frame) == null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int inset = (int)Math.Ceiling(2 * dpi.DpiScaleX);
        var topLeft = Frame.PointToScreen(new Point(0, 0));
        int w = (int)Math.Round(Frame.ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(Frame.ActualHeight * dpi.DpiScaleY);
        int radius = (int)Math.Round(CornerRadius * dpi.DpiScaleX);
        _backdrop.Place((int)Math.Round(topLeft.X) + inset, (int)Math.Round(topLeft.Y) + inset,
            w - 2 * inset, h - 2 * inset, Math.Max(0, radius - inset));
    }

    /// <summary>The shadow layer is opaque; clip it to outside the panel so it never shows through the translucency.</summary>
    private void ClipShadowToOutside()
    {
        double w = Frame.ActualWidth, h = Frame.ActualHeight;
        if (w <= 0 || h <= 0) return;
        ShadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(-100, -100, w + 200, h + 200)),
            new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - 1), CornerRadius, CornerRadius));
    }

    private static bool AnyMouseButtonDown() =>
        (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0 ||
        (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0 ||
        (Win32.GetAsyncKeyState(Win32.VK_MBUTTON) & 0x8000) != 0;

    private void WatchOutsideClicks()
    {
        bool down = AnyMouseButtonDown();
        bool pressed = down && !_mouseWasDown;
        _mouseWasDown = down;
        if (!pressed || !_open) return;
        if (_popupOpen || DateTime.UtcNow - _popupClosedAt < TimeSpan.FromMilliseconds(300)) return;

        Win32.GetCursorPos(out var cursor);
        if (!IsInsidePanel(cursor)) HideMenu(byOutsideClick: true);
    }

    private bool IsInsidePanel(Win32.POINT cursor)
    {
        if (!IsVisible || PresentationSource.FromVisual(Frame) == null) return false;
        var topLeft = Frame.PointToScreen(new Point(0, 0));
        var bottomRight = Frame.PointToScreen(new Point(Frame.ActualWidth, Frame.ActualHeight));
        return cursor.X >= topLeft.X && cursor.X <= bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
    }

    // ---------------- Content ----------------

    private void SwitchView(View view)
    {
        _view = view;
        HomeView.Visibility = view == View.Home ? Visibility.Visible : Visibility.Collapsed;
        AppsView.Visibility = view == View.Home ? Visibility.Collapsed : Visibility.Visible;
        ListTitle.Text = view == View.Search ? "Top Hits" : "All Apps";
        BackLink.Visibility = view == View.AllApps ? Visibility.Visible : Visibility.Collapsed;
        AppList.Tag = view == View.Search ? "search" : null;
        Rebuild();
    }

    private void Rebuild()
    {
        if (!_catalog.IsLoaded)
        {
            PinnedGrid.ItemsSource = null;
            AppList.ItemsSource = null;
            ShowEmpty("Loading apps…");
            return;
        }

        EnsureDefaultPins();
        int count;
        switch (_view)
        {
            case View.Home:
                var pins = (_config.StartPins ?? new())
                    .Select(p => _catalog.Find(p.Path))
                    .Where(a => a != null)
                    .Select(a => a!)
                    .ToList();
                PinnedGrid.ItemsSource = pins;
                PinnedGrid.SelectedIndex = -1;
                count = pins.Count;
                SetEmpty(count == 0, "Nothing pinned yet.\nOpen All Apps, right-click an app and choose Pin to Start.");
                break;

            case View.AllApps:
                var grouped = new ListCollectionView(_catalog.Apps.ToList());
                grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StartApp.Group)));
                AppList.ItemsSource = grouped;
                AppList.SelectedIndex = -1;
                if (AppList.Items.Count > 0) AppList.ScrollIntoView(AppList.Items[0]);
                SetEmpty(AppList.Items.Count == 0, "No apps found");
                break;

            case View.Search:
                var results = _catalog.Search(SearchBox.Text, 30);
                AppList.ItemsSource = results;
                AppList.SelectedIndex = results.Count > 0 ? 0 : -1; // Top Hit selected: Enter opens it
                if (results.Count > 0) AppList.ScrollIntoView(results[0]);
                SetEmpty(results.Count == 0, "No Results");
                break;
        }
    }

    private void SetEmpty(bool empty, string text)
    {
        if (empty) ShowEmpty(text);
        else EmptyText.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
    }

    private void UpdateSearch()
    {
        bool searching = SearchBox.Text.Trim().Length > 0;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (searching) SwitchView(View.Search);
        else if (_view == View.Search) SwitchView(View.Home);
    }

    private void EnsureDefaultPins()
    {
        if (_config.StartPins != null) return;
        _config.StartPins = DefaultPinNames
            .Select(_catalog.FindByName)
            .Where(a => a != null)
            .Select(a => ToPin(a!))
            .ToList();
        _host.SaveConfig();
    }

    private static PinnedApp ToPin(StartApp app) =>
        new() { Path = app.LaunchPath, Name = app.Name, Target = app.TargetPath };

    private bool IsPinnedToStart(StartApp app) =>
        _config.StartPins?.Any(p => string.Equals(p.Path, app.LaunchPath, StringComparison.OrdinalIgnoreCase)) == true;

    private static StartApp? ItemUnderMouse(MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return (element as ListBoxItem)?.DataContext as StartApp;
    }

    // ---------------- Keyboard ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        int columns = _view == View.Home ? Columns : 1;
        bool typing = SearchBox.Text.Length > 0;
        switch (e.Key)
        {
            case Key.Escape:
                if (typing) SearchBox.Text = "";
                else if (_view == View.AllApps) SwitchView(View.Home);
                else HideMenu();
                e.Handled = true;
                break;
            // Left/Right move the caret while typing; they navigate the grid otherwise.
            case Key.Right when !typing && columns > 1: MoveSelection(+1); e.Handled = true; break;
            case Key.Left when !typing && columns > 1: MoveSelection(-1); e.Handled = true; break;
            case Key.Down: MoveSelection(+columns); e.Handled = true; break;
            case Key.Up: MoveSelection(-columns); e.Handled = true; break;
            case Key.Enter:
                if (ActiveList.SelectedItem is StartApp app) Launch(app);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var list = ActiveList;
        int count = list.Items.Count;
        if (count == 0) return;
        int index = list.SelectedIndex < 0 ? 0 : Math.Clamp(list.SelectedIndex + delta, 0, count - 1);
        list.SelectedIndex = index;
        list.ScrollIntoView(list.Items[index]);
    }

    private void AllApps_Click(object sender, RoutedEventArgs e) => SwitchView(View.AllApps);

    private void Back_Click(object sender, RoutedEventArgs e) => SwitchView(View.Home);

    // ---------------- Menus ----------------

    private void ShowAppMenu(StartApp app)
    {
        var menu = NewMenu();
        menu.Items.Add(NewItem("Open", () => Launch(app)));
        menu.Items.Add(NewSeparator());
        menu.Items.Add(_host.IsPinnedToDock(app)
            ? NewItem("Remove from Dock", () => _host.UnpinFromDock(app))
            : NewItem("Keep in Dock", () => _host.PinToDock(app)));
        menu.Items.Add(IsPinnedToStart(app)
            ? NewItem("Unpin from Start", () => UnpinFromStart(app))
            : NewItem("Pin to Start", () => PinToStart(app)));
        menu.Items.Add(NewSeparator());
        bool hasFile = app.TargetPath != null && File.Exists(app.TargetPath);
        menu.Items.Add(NewItem("Show in Explorer", () => ShowInExplorer(app.TargetPath!), hasFile));

        menu.Placement = PlacementMode.MousePoint;
        menu.PlacementTarget = ActiveList;
        menu.IsOpen = true;
    }

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();
        menu.Items.Add(NewItem("Sleep", () => Run(PowerActions.Sleep)));
        menu.Items.Add(NewItem("Restart", () => Run(PowerActions.Restart)));
        menu.Items.Add(NewItem("Shut Down", () => Run(PowerActions.ShutDown)));
        menu.Items.Add(NewSeparator());
        menu.Items.Add(NewItem("Lock Screen", () => Run(PowerActions.Lock)));
        menu.Items.Add(NewItem($"Log Out {UserInfo.UserName}", () => Run(PowerActions.SignOut)));
        menu.PlacementTarget = PowerButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) { SystemSettingsWindow.Open(); Close(); }

    private ContextMenu NewMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("MacMenu"),
            ItemContainerStyle = (Style)FindResource("MacMenuItem"),
        };
        // Over the light Start menu these stay light and solid (the dock-glass menus are for the menu bar).
        menu.Resources["GlassMenuBackground"] = new SolidColorBrush(Color.FromArgb(0xFA, 0xFB, 0xFB, 0xFD));
        menu.Resources["GlassMenuForeground"] = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
        menu.Resources["GlassMenuRim"] = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xE0));
        menu.Resources["GlassMenuSeparator"] = new SolidColorBrush(Color.FromArgb(0x1F, 0, 0, 0));
        menu.Opened += (_, _) => _popupOpen = true;
        menu.Closed += (_, _) =>
        {
            _popupOpen = false;
            _popupClosedAt = DateTime.UtcNow;
        };
        return menu;
    }

    private Separator NewSeparator() => new() { Style = (Style)FindResource("MacMenuSeparator") };

    private static MenuItem NewItem(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------------- Actions ----------------

    private void Launch(StartApp app) => OpenAndClose(app.LaunchPath);

    private void OpenAndClose(string target)
    {
        try
        {
            if (SystemSettingsWindow.IsSettingsExe(target)) SystemSettingsWindow.OpenFromShortcut();
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Start menu launch failed for {target}: {ex.Message}");
        }
        HideMenu();
    }

    private void Run(Action action)
    {
        HideMenu();
        action();
    }

    private void PinToStart(StartApp app)
    {
        _config.StartPins ??= new();
        if (IsPinnedToStart(app) || _config.StartPins.Count >= MaxPins) return;
        _config.StartPins.Add(ToPin(app));
        _host.SaveConfig();
        Rebuild();
    }

    private void UnpinFromStart(StartApp app)
    {
        _config.StartPins?.RemoveAll(p => string.Equals(p.Path, app.LaunchPath, StringComparison.OrdinalIgnoreCase));
        _host.SaveConfig();
        Rebuild();
    }

    private void ShowInExplorer(string path)
    {
        HideMenu();
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Show in Explorer failed: {ex.Message}"); }
    }
}
