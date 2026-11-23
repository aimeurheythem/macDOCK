using System.IO;
using System.Media;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using MacDock.Controls;
using MacDock.Native;
using MacDock.Services;

namespace MacDock.Finder;

/// <summary>
/// A macOS Finder window for browsing files: sidebar (Favorites, Locations), toolbar with back/forward,
/// view switcher (icons, list, columns), sort and action menus and search, a path bar and a status bar.
/// Opened from the File Explorer icon in the dock.
/// </summary>
public partial class FinderWindow : Window
{
    /// <summary>Virtual location: recently opened files.</summary>
    public const string RecentsLocation = "::recents";

    private enum ViewMode { Icons, List, Columns }
    private enum SortKey { Name, Date, Size, Kind }

    // Open Finder windows, most recently active first.
    private static readonly List<FinderWindow> OpenWindows = new();

    /// <summary>A Finder window opened or closed (the dock shows a running dot while any is open).</summary>
    public static event Action? WindowsChanged;

    public static bool AnyOpen => OpenWindows.Count > 0;

    /// <summary>Set by the dock: minimizes a Finder window into its dock icon with the Genie effect (false = not possible).</summary>
    public static Func<IntPtr, Task<bool>>? GenieMinimize { get; set; }

    /// <summary>Set by the dock: brings a minimized Finder window back out of the dock with the Genie effect.</summary>
    public static Func<IntPtr, Task<bool>>? GenieRestore { get; set; }

    /// <summary>Real frosted blur of the desktop behind the see-through sidebar.</summary>
    private BlurBackdrop? _blur;

    private readonly FinderSettings _settings = FinderSettings.Current;
    private readonly IconLoader _icons;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly List<ListBox> _columns = new();
    private readonly HashSet<ListBox> _quietSelection = new();
    private readonly List<(Border Item, string Path)> _sidebarItems = new();
    private string _driveSignature = "";

    private FileSystemWatcher? _watcher;
    private string _location = "";
    private string? _entriesLocation;
    private string? _loadError;
    private List<FileEntry> _entries = new();
    private List<FileEntry> _shown = new();
    private ViewMode _view;
    private SortKey _sort;
    private bool _ascending;
    private int _loadGeneration;
    private CancellationTokenSource? _searchCts;
    private bool _searching;
    private FileEntry? _renaming;
    private bool _zoomed;
    private Rect _restoreBounds;
    private bool _lightsHover;
    private Point _dragStart;
    private bool _dragArmed;
    private readonly string _startLocation;
    private IEnumerable<string>? _startSelect;

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    public FinderWindow(string location)
    {
        InitializeComponent();
        // Opens and closes like a Mac window; the sidebar's blur appears once it is fully open.
        MacMotion.AttachWindow(this, Outer, shown: () => { _settled = true; PlaceBlur(); }, hiding: () => { _settled = false; _blur?.SetVisible(false); });
        ApplyTheme();
        _startLocation = location;
        _icons = new IconLoader(Dispatcher);

        _view = Enum.TryParse<ViewMode>(_settings.View, out var v) ? v : ViewMode.Icons;
        _sort = Enum.TryParse<SortKey>(_settings.Sort, out var s) ? s : SortKey.Name;
        _ascending = _settings.Ascending;
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        SidebarColumn.Width = new GridLength(Math.Clamp(_settings.SidebarWidth, 150, 320));
        try { Icon = FinderIcon.Render(64); } catch { /* window icon is cosmetic */ }

        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            if (_renaming != null) { _reloadTimer.Start(); return; } // don't disturb an edit
            Reload();
        };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(); };

        WireList(IconList);
        WireList(DetailList);
        BuildListHeader();
        BuildSidebar();
        ApplyBars();

        Body.SizeChanged += (_, _) => { ClipBody(); UpdateGlass(); };
        SidebarPanel.SizeChanged += (_, _) => UpdateGlass();
        LocationChanged += (_, _) => PlaceBlur();
        IsVisibleChanged += (_, _) => PlaceBlur();
        SourceInitialized += (_, _) =>
        {
            _blur = new BlurBackdrop(acrylic: false, topmost: false);
            if (_blur.Handle == IntPtr.Zero)
            {
                // No blur available: a solid sidebar instead of a see-through one.
                Resources["SidebarGlass"] = Resources["SidebarBg"];
                _blur = null;
            }
        };
        Activated += (_, _) =>
        {
            OpenWindows.Remove(this);
            OpenWindows.Insert(0, this);
            UpdateLights();
            RefreshDrivesIfChanged();
            PlaceBlur();
        };
        Deactivated += (_, _) => UpdateLights();
        StateChanged += (_, _) =>
        {
            // The menu bar's Window > Zoom (and Aero snap) maximizes; use Finder's own zoom instead.
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                if (!_zoomed) Dispatcher.BeginInvoke(ToggleZoom);
            }
            PlaceBlur();
        };
        PreviewKeyDown += OnKey;
        Closed += OnClosed;
        Closed += (_, _) => Services.MemoryTrim.Soon();
        Loaded += (_, _) =>
        {
            switch (_view)
            {
                case ViewMode.List: ListViewButton.IsChecked = true; break;
                case ViewMode.Columns: ColumnViewButton.IsChecked = true; break;
                default: IconViewButton.IsChecked = true; break;
            }
            ApplyViewVisibility();
            Navigate(_startLocation, addHistory: false, select: _startSelect);
            UpdateGlass();
        };

        OpenWindows.Insert(0, this);
        WindowsChanged?.Invoke();
    }

    // =====================================================================
    // Opening windows (dock, Spotlight)
    // =====================================================================

    /// <summary>Opens a new Finder window showing <paramref name="path"/>.</summary>
    public static FinderWindow OpenFolder(string path) => OpenFolder(path, null);

    /// <summary>Opens a new Finder window showing <paramref name="path"/> with <paramref name="select"/> selected.</summary>
    public static FinderWindow OpenFolder(string path, IEnumerable<string>? select)
    {
        var window = new FinderWindow(path) { _startSelect = select };
        var last = OpenWindows.Skip(1).FirstOrDefault(w => w.IsVisible && w.WindowState != WindowState.Minimized);
        if (last != null && !last._zoomed)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = last.Left + 28;
            window.Top = last.Top + 28;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        window.Show();
        Bring(window);
        return window;
    }

    /// <summary>
    /// The dock's File Explorer icon: no window yet opens one; a window in front is minimized (one
    /// window) or the next one comes forward (several); otherwise the last used window comes back.
    /// </summary>
    public static void ShowFromDock()
    {
        OpenWindows.RemoveAll(w => w._closed);
        if (OpenWindows.Count == 0)
        {
            OpenFolder(FinderAssets.Home);
            return;
        }
        var active = OpenWindows.FirstOrDefault(w => w.IsActive && w.WindowState != WindowState.Minimized);
        if (active != null)
        {
            if (OpenWindows.Count == 1) active.WindowState = WindowState.Minimized;
            else Bring(OpenWindows[^1]);
            return;
        }
        Bring(OpenWindows[0]);
    }

    // =====================================================================
    // Menu bar (File, Edit, View, Go, Window): the commands of the macOS Finder menus
    // =====================================================================

    /// <summary>
    /// Runs a Finder menu command on the Finder window <paramref name="hwnd"/>, or (desktop in front,
    /// no Finder window) opens a new window where the command needs one, like macOS.
    /// </summary>
    public static void MenuCommand(string command, IntPtr hwnd)
    {
        OpenWindows.RemoveAll(w => w._closed);
        var window = OpenWindows.FirstOrDefault(w => w.Handle == hwnd);
        string? place = command switch
        {
            "recents" => RecentsLocation,
            "documents" => FinderAssets.Documents,
            "desktop" => FinderAssets.Desktop,
            "downloads" => FinderAssets.Downloads,
            "home" => FinderAssets.Home,
            "applications" => FinderAssets.Applications,
            "computer" => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            "pictures" => FinderAssets.Pictures,
            "music" => FinderAssets.Music,
            "movies" => FinderAssets.Videos,
            _ => null,
        };
        if (command == "newwindow") { OpenFolder(window?.CurrentFolder ?? FinderAssets.Home); return; }
        if (command == "goto") { GoToFolderPrompt(window); return; }
        if (window == null)
        {
            if (place != null) OpenFolder(place);
            else if (command is "newfolder" or "search") OpenFolder(command == "newfolder" ? FinderAssets.Desktop : FinderAssets.Home).Dispatcher.BeginInvoke(() =>
            {
                var w = OpenWindows.FirstOrDefault();
                if (command == "newfolder") w?.NewFolder(); else w?.OpenSearch();
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
        window.Activate();
        if (place != null) { window.Navigate(place); return; }
        switch (command)
        {
            case "newfolder": window.NewFolder(); break;
            case "open": window.OpenEntries(SelectedEntries(window.ActiveList)); break;
            case "close": window.Close(); break;
            case "getinfo": window.GetInfo(); break;
            case "rename": if (SelectedEntries(window.ActiveList).FirstOrDefault() is { } first) window.StartRename(first); break;
            case "duplicate": window.Duplicate(); break;
            case "compress": window.CompressSelection(); break;
            case "trash": window.TrashSelection(); break;
            case "quicklook": window.QuickLook(); break;
            case "cut": window.CopySelection(true); break;
            case "copy": window.CopySelection(false); break;
            case "paste": window.Paste(); break;
            case "copypath": window.CopyPaths(); break;
            case "selectall": window.ActiveList.SelectAll(); break;
            case "icons": window.SetView(ViewMode.Icons); break;
            case "list": window.SetView(ViewMode.List); break;
            case "columns": window.SetView(ViewMode.Columns); break;
            case "hidden": window.ToggleHidden(); break;
            case "pathbar": window._settings.ShowPathBar = !window._settings.ShowPathBar; window.ApplyBars(); break;
            case "statusbar": window._settings.ShowStatusBar = !window._settings.ShowStatusBar; window.ApplyBars(); break;
            case "back": window.GoBack(); break;
            case "forward": window.GoForward(); break;
            case "up": window.GoUp(); break;
            case "search": window.OpenSearch(); break;
            case "terminal": if (window.CurrentFolder is { } here) FileOperations.OpenTerminal(here); break;
        }
    }

    /// <summary>State for the menu check marks (View menu) of a Finder window; defaults when there is none.</summary>
    public static (string View, bool Hidden, bool PathBar, bool StatusBar, bool HasSelection) MenuState(IntPtr hwnd)
    {
        var window = OpenWindows.FirstOrDefault(w => w.Handle == hwnd && !w._closed);
        if (window == null) return ("", false, true, true, false);
        return (window._view.ToString(), window._settings.ShowHidden, window._settings.ShowPathBar, window._settings.ShowStatusBar,
                SelectedEntries(window.ActiveList).Count > 0);
    }

    /// <summary>Go > Go to Folder… (Ctrl+Shift+G): type a path, Finder opens it.</summary>
    private static void GoToFolderPrompt(FinderWindow? window)
    {
        var box = new TextBox { MinWidth = 380, FontSize = 14, Padding = new Thickness(6, 4, 6, 4), Text = window?.CurrentFolder ?? "" };
        var prompt = new Window
        {
            Title = "Go to Folder",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = window != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = window,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    new TextBlock { Text = "Go to the folder:", Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold },
                    box,
                },
            },
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) prompt.Close();
            if (e.Key != Key.Enter) return;
            var path = Environment.ExpandEnvironmentVariables(box.Text.Trim().Trim('"'));
            if (path.StartsWith("~")) path = FinderAssets.Home + path[1..];
            if (!Directory.Exists(path)) { SystemSounds.Beep.Play(); return; }
            prompt.Close();
            if (window != null && !window._closed) window.Navigate(path); else OpenFolder(path);
        };
        prompt.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        prompt.Show();
        prompt.Activate();
    }

    /// <summary>True when <paramref name="hwnd"/> is one of the Finder windows (the menu bar then says "Finder").</summary>
    public static bool IsFinderWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && OpenWindows.Any(w => w.Handle == hwnd);

    private static async void Bring(FinderWindow window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            try
            {
                if (GenieRestore != null && await GenieRestore(window.Handle)) return;
            }
            catch
            {
                // Fall back to a plain restore.
            }
            window.WindowState = WindowState.Normal;
        }
        window.Show();
        window.Activate();
        WindowActivator.ForceForeground(window.Handle);
    }

    private bool _closed;

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _blur?.Dispose();
        _blur = null;
        _searchCts?.Cancel();
        _watcher?.Dispose();
        _icons.Dispose();
        _reloadTimer.Stop();
        _searchTimer.Stop();
        QuickLookWindow.CloseFor(this);
        if (!_zoomed)
        {
            _settings.Width = Width;
            _settings.Height = Height;
        }
        _settings.SidebarWidth = SidebarColumn.ActualWidth;
        _settings.Save();
        OpenWindows.Remove(this);
        WindowsChanged?.Invoke();
    }

    // =====================================================================
    // Theme and window chrome
    // =====================================================================

    private void ApplyTheme()
    {
        if (!FinderAssets.IsDarkMode()) return;
        void Set(string key, uint argb) => Resources[key] = Frozen(argb);
        Set("ContentBg", 0xFF1E1E1E);
        Set("SidebarBg", 0xFF29282C);
        Set("Text", 0xFFF2F2F7);
        Set("SubText", 0xFF98989D);
        Set("SectionText", 0xFF8A8A8F);
        Set("Accent", 0xFF0A63E0);
        Set("SidebarGlyph", 0xFF3E95FF);
        Set("SelectionInactiveBg", 0xFF46464A);
        Set("IconSelBg", 0xFF3A3A3D);
        Set("SidebarSel", 0xFF414045);
        Set("Stripe", 0xFF262628);
        Set("Separator", 0xFF38383B);
        Set("FrameBorder", 0x80000000);
        Set("ControlBg", 0xFF333336);
        Set("ControlSel", 0xFF5C5C60);
        Set("HoverBg", 0x1FFFFFFF);
        Set("ScrollThumb", 0x66FFFFFF);
        Set("RenameBg", 0xFF2B2B2E);
        Set("LightInactive", 0xFF4A4A4D);
        Set("SidebarGlass", 0xA8242427);
        Set("GlassFill", 0xEB343437);
        Set("GlassSegSel", 0x2AFFFFFF);
        var rim = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x50, 0x00, 0x00, 0x00), 1),
            },
        };
        rim.Freeze();
        Resources["GlassRim"] = rim;
    }

    private static SolidColorBrush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    private const double WindowRadius = 18, SidebarRadius = 16;

    private void ClipBody()
    {
        Body.Clip = new RectangleGeometry(new Rect(0, 0, Body.ActualWidth, Body.ActualHeight), WindowRadius, WindowRadius);
    }

    /// <summary>
    /// The sidebar is real glass: the window is see-through exactly where the sidebar panel is (the
    /// window background is drawn around it, and the shadow layer is cut out there), and a blur window
    /// right behind it frosts the desktop.
    /// </summary>
    private void UpdateGlass()
    {
        if (!IsLoaded || SidebarPanel.ActualWidth <= 0 || Body.ActualHeight <= 0) return;
        try
        {
            var panel = new Rect(0, 0, SidebarPanel.ActualWidth, SidebarPanel.ActualHeight);
            var inBody = SidebarPanel.TransformToAncestor(Body).TransformBounds(panel);
            SidebarRing.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, SidebarColumn.ActualWidth, Body.ActualHeight)),
                new RectangleGeometry(inBody, SidebarRadius, SidebarRadius));

            var inShadow = SidebarPanel.TransformToAncestor(ShadowHost).TransformBounds(panel);
            ShadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-200, -200, ShadowHost.ActualWidth + 400, ShadowHost.ActualHeight + 400)),
                new RectangleGeometry(inShadow, SidebarRadius, SidebarRadius));
        }
        catch (InvalidOperationException)
        {
            // Not in the tree yet.
        }
        PlaceBlur();
    }

    private bool _settled; // fully open (not animating in or out)

    private void PlaceBlur()
    {
        if (_blur == null || _closed) return;
        if (!_settled || !IsVisible || WindowState == WindowState.Minimized || PresentationSource.FromVisual(SidebarPanel) == null || SidebarPanel.ActualWidth <= 0)
        {
            _blur.SetVisible(false);
            return;
        }
        double scale = Dpi;
        var topLeft = SidebarPanel.PointToScreen(new Point(0, 0)); // device pixels
        _blur.Place((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(SidebarPanel.ActualWidth * scale), (int)Math.Round(SidebarPanel.ActualHeight * scale),
            (int)Math.Round(SidebarRadius * scale));
        // Directly behind this window, so other windows cover both together.
        Win32.SetWindowPos(_blur.Handle, Handle, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        _blur.SetVisible(true);
    }

    private void ApplyBars()
    {
        PathBarHost.Visibility = _settings.ShowPathBar ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)StatusText.Parent).Visibility = _settings.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleZoom();
            return;
        }
        if (_zoomed)
        {
            // Dragging a zoomed window: restore its size under the cursor first, like macOS.
            var p = PointToScreen(e.GetPosition(this));
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double ratio = e.GetPosition(this).X / Math.Max(1, ActualWidth);
            ToggleZoom();
            Left = p.X / scale - Width * ratio;
            Top = p.Y / scale - 26;
        }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void ToggleZoom()
    {
        if (!_zoomed)
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);
            var area = WorkArea();
            Outer.Margin = new Thickness(0);
            ShadowLayer.Visibility = Visibility.Collapsed;
            Left = area.Left;
            Top = area.Top;
            Width = area.Width;
            Height = area.Height;
            _zoomed = true;
        }
        else
        {
            Outer.Margin = new Thickness(16);
            ShadowLayer.Visibility = Visibility.Visible;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            _zoomed = false;
        }
    }

    /// <summary>Work area of the monitor the window is on, in DIPs.</summary>
    private Rect WorkArea()
    {
        try
        {
            IntPtr monitor = Win32.MonitorFromWindow(Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
            if (monitor != IntPtr.Zero && Win32.GetMonitorInfo(monitor, ref info))
            {
                double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var r = info.rcWork;
                return new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
            }
        }
        catch
        {
            // Fall back to the primary screen.
        }
        return SystemParameters.WorkArea;
    }

    // ---------------- Traffic lights ----------------

    private void Lights_MouseEnter(object sender, MouseEventArgs e) { _lightsHover = true; UpdateLights(); }
    private void Lights_MouseLeave(object sender, MouseEventArgs e) { _lightsHover = false; UpdateLights(); }
    private void Light_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // not a window drag
    private void CloseLight_Click(object sender, MouseButtonEventArgs e) => Close();
    private async void MinLight_Click(object sender, MouseButtonEventArgs e)
    {
        // Into the dock with the Genie effect, like macOS.
        try
        {
            if (GenieMinimize != null && await GenieMinimize(Handle)) return;
        }
        catch
        {
            // Fall back to a plain minimize.
        }
        WindowState = WindowState.Minimized;
    }
    private void ZoomLight_Click(object sender, MouseButtonEventArgs e) => ToggleZoom();

    private void UpdateLights()
    {
        bool lit = IsActive || _lightsHover;
        var glyph = _lightsHover ? Visibility.Visible : Visibility.Hidden;
        CloseGlyph.Visibility = MinGlyph.Visibility = ZoomGlyph.Visibility = glyph;
        if (lit)
        {
            CloseDot.Fill = Frozen(0xFFFF5F57); CloseDot.Stroke = Frozen(0xFFE14640);
            MinDot.Fill = Frozen(0xFFFEBC2E); MinDot.Stroke = Frozen(0xFFDFA023);
            ZoomDot.Fill = Frozen(0xFF28C840); ZoomDot.Stroke = Frozen(0xFF1DAD2C);
        }
        else
        {
            foreach (var dot in new[] { CloseDot, MinDot, ZoomDot })
            {
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "LightInactive");
                dot.Stroke = Frozen(0x22000000);
            }
        }
    }

    // =====================================================================
    // Sidebar
    // =====================================================================

    private void BuildSidebar()
    {
        SidebarHost.Children.Clear();
        _sidebarItems.Clear();

        AddSection("Favorites");
        AddSidebarItem("", "Recents", RecentsLocation);
        AddSidebarItem("", "Applications", FinderAssets.Applications);
        AddSidebarItem("", "Desktop", FinderAssets.Desktop);
        AddSidebarItem("", "Documents", FinderAssets.Documents);
        AddSidebarItem("", "Downloads", FinderAssets.Downloads);
        AddSidebarItem("", Environment.UserName, FinderAssets.Home);
        AddSidebarItem("", "Pictures", FinderAssets.Pictures);
        AddSidebarItem("", "Music", FinderAssets.Music);
        AddSidebarItem("", "Movies", FinderAssets.Videos);

        AddSection("Locations");
        var drives = Drives();
        _driveSignature = string.Join("|", drives.Select(d => d.Name));
        foreach (var drive in drives)
        {
            string glyph = drive.DriveType switch
            {
                DriveType.Removable => "",
                DriveType.Network => "",
                DriveType.CDRom => "",
                _ => "",
            };
            AddSidebarItem(glyph, FinderAssets.DriveLabel(drive.Name), drive.Name);
        }
        UpdateSidebarSelection();
    }

    private static List<DriveInfo> Drives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.CDRom)
                .Where(d => { try { return d.IsReady; } catch { return false; } })
                .ToList();
        }
        catch
        {
            return new List<DriveInfo>();
        }
    }

    private void RefreshDrivesIfChanged()
    {
        try
        {
            var signature = string.Join("|", Drives().Select(d => d.Name));
            if (signature != _driveSignature) BuildSidebar();
        }
        catch
        {
            // Keep the current list.
        }
    }

    private void AddSection(string title)
    {
        var text = new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(18, SidebarHost.Children.Count == 0 ? 0 : 16, 0, 4),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "SectionText");
        SidebarHost.Children.Add(text);
    }

    private void AddSidebarItem(string glyph, string label, string path)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = FinderAssets.SymbolFont, FontSize = 15, Width = 24, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "SidebarGlyph");
        var name = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 1) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(name);

        var item = new Border
        {
            Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(8, 0, 8, 1), Padding = new Thickness(9, 0, 8, 0),
            Background = Brushes.Transparent, Child = row, ToolTip = path == RecentsLocation ? null : path, AllowDrop = path != RecentsLocation,
        };
        ToolTipService.SetInitialShowDelay(item, 1200);
        item.MouseLeftButtonUp += (_, _) => Navigate(path);
        item.DragOver += (_, e) => { e.Effects = DropEffect(e, path); e.Handled = true; };
        item.Drop += (_, e) => { DropInto(e, path); e.Handled = true; };
        SidebarHost.Children.Add(item);
        _sidebarItems.Add((item, path));
    }

    private void UpdateSidebarSelection()
    {
        foreach (var (item, path) in _sidebarItems)
        {
            bool on = path == RecentsLocation ? _location == RecentsLocation : FinderAssets.Same(path, _location);
            if (on) item.SetResourceReference(Border.BackgroundProperty, "SidebarSel");
            else item.Background = Brushes.Transparent;
        }
    }

    // =====================================================================
    // Navigation
    // =====================================================================

    private void Navigate(string location, bool addHistory = true, IEnumerable<string>? select = null)
    {
        if (location != RecentsLocation && !Directory.Exists(location))
        {
            SystemSounds.Beep.Play();
            return;
        }
        CancelRename();
        if (addHistory && _location.Length > 0 && !FinderAssets.Same(_location, location))
        {
            _back.Push(_location);
            _forward.Clear();
        }
        _location = location;
        CancelSearch(clearBox: true);
        Watch();
        UpdateChrome();
        if (_view == ViewMode.Columns) BuildColumns(select);
        else Load(select);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();

    private void GoBack()
    {
        while (_back.Count > 0)
        {
            var target = _back.Pop();
            if (target != RecentsLocation && !Directory.Exists(target)) continue;
            _forward.Push(_location);
            var from = _location;
            Navigate(target, addHistory: false, select: new[] { from });
            return;
        }
        SystemSounds.Beep.Play();
    }

    private void GoForward()
    {
        while (_forward.Count > 0)
        {
            var target = _forward.Pop();
            if (target != RecentsLocation && !Directory.Exists(target)) continue;
            _back.Push(_location);
            Navigate(target, addHistory: false);
            return;
        }
        SystemSounds.Beep.Play();
    }

    private void GoUp()
    {
        if (_location == RecentsLocation) return;
        var parent = Path.GetDirectoryName(_location.TrimEnd('\\'));
        if (parent == null) { SystemSounds.Beep.Play(); return; }
        if (!parent.EndsWith('\\') && parent.Length == 2) parent += "\\";
        Navigate(parent, select: new[] { _location });
    }

    private void UpdateChrome()
    {
        string title = _searching ? $"Searching “{SearchBox.Text.Trim()}”" : FinderAssets.DisplayName(_location);
        TitleText.Text = title;
        Title = title;
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
        UpdateSidebarSelection();
        UpdatePathBar();
        UpdateStatus();
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_location == RecentsLocation || !Directory.Exists(_location)) return;
        try
        {
            var watcher = new FileSystemWatcher(_location)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { _reloadTimer.Stop(); _reloadTimer.Start(); });
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Changed += changed;
            watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _reloadTimer.Stop(); _reloadTimer.Start(); });
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            // Some locations (network, special) can't be watched: F5 still refreshes.
        }
    }

    // =====================================================================
    // Loading and showing items
    // =====================================================================

    private static (List<FileEntry> Entries, string? Error) Enumerate(string location)
    {
        if (location == RecentsLocation) return (RecentItems(), null);
        var list = new List<FileEntry>();
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            foreach (var info in new DirectoryInfo(location).EnumerateFileSystemInfos("*", options))
                list.Add(FileEntry.FromInfo(info));
            return (list, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (list, $"You don't have permission to see the contents of “{FinderAssets.DisplayName(location)}”.");
        }
        catch (Exception ex)
        {
            return (list, ex.Message);
        }
    }

    /// <summary>Recently opened files (Windows' Recent items), newest first.</summary>
    private static List<FileEntry> RecentItems()
    {
        var result = new List<FileEntry>();
        try
        {
            var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in new DirectoryInfo(recent).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(200))
            {
                var target = ShortcutResolver.GetTarget(link.FullName);
                if (string.IsNullOrEmpty(target) || !File.Exists(target) || !seen.Add(target)) continue;
                result.Add(FileEntry.FromInfo(new FileInfo(target)));
                if (result.Count >= 120) break;
            }
        }
        catch
        {
            // Nothing recent.
        }
        return result;
    }

    private async void Load(IEnumerable<string>? select = null, string? renamePath = null)
    {
        int generation = ++_loadGeneration;
        var location = _location;
        var (entries, error) = await Task.Run(() => Enumerate(location));
        if (generation != _loadGeneration || _closed) return;
        _entries = entries;
        _entriesLocation = location;
        _loadError = error;
        Display(select);
        if (renamePath != null && _shown.FirstOrDefault(x => FinderAssets.Same(x.FullPath, renamePath)) is { } entry)
            StartRename(entry);
    }

    /// <summary>Reloads after a change on disk, keeping the selection.</summary>
    private void Reload(string? select = null)
    {
        if (_searching) return;
        if (_view == ViewMode.Columns)
        {
            foreach (var column in _columns.ToList()) ReloadColumn(column, select);
            return;
        }
        var keep = SelectedEntries(ActiveList).Select(x => x.FullPath).ToList();
        if (select != null) keep = new List<string> { select };
        Load(keep);
    }

    private ViewMode EffectiveView => _searching && _view == ViewMode.Columns ? ViewMode.List : _view;

    private void Display(IEnumerable<string>? select = null)
    {
        var shown = _entries.Where(x => _settings.ShowHidden || !x.IsHidden).ToList();
        shown.Sort(Compare);
        _shown = shown;
        _icons.Reset();

        ApplyViewVisibility();
        var list = EffectiveView == ViewMode.Icons ? IconList : DetailList;
        var other = list == IconList ? DetailList : IconList;
        other.ItemsSource = null;
        list.ItemsSource = _shown;
        RequestIcons(_shown, large: list == IconList);

        if (_loadError != null) ShowEmpty(_loadError);
        else if (_searching && _shown.Count == 0) ShowEmpty(_searchCts?.IsCancellationRequested == false ? "Searching…" : "No Results");
        else ShowEmpty(null);

        if (select != null)
        {
            var wanted = new HashSet<string>(select, StringComparer.OrdinalIgnoreCase);
            var items = _shown.Where(x => wanted.Contains(x.FullPath)).ToList();
            list.SelectedItems.Clear();
            foreach (var item in items) list.SelectedItems.Add(item);
            if (items.Count > 0)
            {
                list.ScrollIntoView(items[0]);
                FocusItem(list, items[0]);
            }
        }
        if (list.SelectedItems.Count == 0 && IsActive && Keyboard.FocusedElement is not TextBox) list.Focus();
        UpdateChrome();
    }

    private void ShowEmpty(string? message)
    {
        EmptyText.Text = message ?? "";
        EmptyText.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void RequestIcons(IEnumerable<FileEntry> items, bool large)
    {
        int px = (int)Math.Ceiling((large ? 64 : 16) * Dpi);
        foreach (var item in items) _icons.Request(item, px, thumbnails: large);
    }

    private int Compare(FileEntry a, FileEntry b)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1; // folders on top
        int c = _sort switch
        {
            SortKey.Date => a.Modified.CompareTo(b.Modified),
            SortKey.Size => a.Size.CompareTo(b.Size),
            SortKey.Kind => string.Compare(a.Kind, b.Kind, StringComparison.CurrentCultureIgnoreCase),
            _ => FinderAssets.NaturalCompare(a.Name, b.Name),
        };
        if (c == 0 && _sort != SortKey.Name) return FinderAssets.NaturalCompare(a.Name, b.Name);
        return _ascending ? c : -c;
    }

    private void SetSort(SortKey key, bool? ascending = null)
    {
        if (ascending.HasValue) _ascending = ascending.Value;
        else if (_sort == key) _ascending = !_ascending;
        else _ascending = key is not (SortKey.Date or SortKey.Size); // newest / largest first, like Finder
        _sort = key;
        _settings.Sort = key.ToString();
        _settings.Ascending = _ascending;
        BuildListHeader();
        if (_view == ViewMode.Columns && !_searching)
        {
            foreach (var column in _columns.ToList()) ReloadColumn(column, null);
            return;
        }
        Display(SelectedEntries(ActiveList).Select(x => x.FullPath).ToList());
    }

    // =====================================================================
    // Views
    // =====================================================================

    private void ViewButton_Checked(object sender, RoutedEventArgs e)
    {
        var mode = sender == ListViewButton ? ViewMode.List : sender == ColumnViewButton ? ViewMode.Columns : ViewMode.Icons;
        if (mode == _view && _location.Length > 0 && (mode != ViewMode.Columns || _columns.Count > 0)) return;
        SetView(mode);
    }

    private void SetView(ViewMode mode)
    {
        var keep = _location.Length > 0 ? SelectedEntries(ActiveList).Select(x => x.FullPath).ToList() : new List<string>();
        _view = mode;
        _settings.View = mode.ToString();
        var button = mode switch { ViewMode.List => ListViewButton, ViewMode.Columns => ColumnViewButton, _ => IconViewButton };
        if (button.IsChecked != true) button.IsChecked = true;
        ApplyViewVisibility();
        if (_location.Length == 0) return;

        if (EffectiveView == ViewMode.Columns)
        {
            IconList.ItemsSource = null;
            DetailList.ItemsSource = null;
            ShowEmpty(null);
            BuildColumns(keep);
            return;
        }
        ColumnsHost.Children.Clear();
        _columns.Clear();
        if (_searching || FinderAssets.Same(_entriesLocation, _location) || _entriesLocation == _location) Display(keep);
        else Load(keep);
    }

    private void ApplyViewVisibility()
    {
        var view = EffectiveView;
        IconList.Visibility = view == ViewMode.Icons ? Visibility.Visible : Visibility.Collapsed;
        ListHost.Visibility = view == ViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        ColumnScroller.Visibility = view == ViewMode.Columns ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The list the user is working in.</summary>
    private ListBox ActiveList
    {
        get
        {
            switch (EffectiveView)
            {
                case ViewMode.Icons: return IconList;
                case ViewMode.List: return DetailList;
            }
            var focused = _columns.FirstOrDefault(c => c.IsKeyboardFocusWithin);
            if (focused != null) return focused;
            var withSelection = _columns.LastOrDefault(c => c.SelectedItems.Count > 0);
            return withSelection ?? _columns.LastOrDefault() ?? DetailList;
        }
    }

    /// <summary>Folder that New Folder and Paste use; null in Recents and search results.</summary>
    private string? CurrentFolder
    {
        get
        {
            if (_searching) return null;
            if (EffectiveView == ViewMode.Columns)
            {
                var list = ActiveList;
                if (list.Tag is string folder)
                {
                    // A selected folder in the last column means "inside it".
                    if (list.SelectedItems.Count == 1 && list.SelectedItem is FileEntry { IsDirectory: true } dir && _columns.LastOrDefault() == list)
                        return dir.FullPath;
                    return folder == RecentsLocation ? null : folder;
                }
            }
            return _location == RecentsLocation ? null : _location;
        }
    }

    private static List<FileEntry> SelectedEntries(ListBox list) => list.SelectedItems.OfType<FileEntry>().ToList();

    // ---------------- List header (list view) ----------------

    private void BuildListHeader()
    {
        ListHeader.Children.Clear();
        AddHeader(0, "Name", SortKey.Name, new Thickness(8, 0, 0, 0), HorizontalAlignment.Left);
        AddHeader(1, "Date Modified", SortKey.Date, new Thickness(0), HorizontalAlignment.Left);
        AddHeader(2, "Size", SortKey.Size, new Thickness(0, 0, 18, 0), HorizontalAlignment.Right);
        AddHeader(3, "Kind", SortKey.Kind, new Thickness(0), HorizontalAlignment.Left);
    }

    private void AddHeader(int column, string label, SortKey key, Thickness margin, HorizontalAlignment align)
    {
        bool active = _sort == key;
        var text = new TextBlock { Text = label, FontSize = 12, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text" : "SubText");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, Margin = margin };
        panel.Children.Add(text);
        if (active)
        {
            var chevron = new TextBlock { Text = _ascending ? "" : "", FontFamily = FinderAssets.SymbolFont, FontSize = 8, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            panel.Children.Add(chevron);
        }
        var cell = new Border { Background = Brushes.Transparent, Child = panel, Cursor = Cursors.Hand };
        cell.MouseLeftButtonUp += (_, _) => SetSort(key);
        Grid.SetColumn(cell, column);
        ListHeader.Children.Add(cell);
    }

    // ---------------- Column view ----------------

    private void BuildColumns(IEnumerable<string>? select = null)
    {
        ColumnsHost.Children.Clear();
        _columns.Clear();
        if (_location == RecentsLocation)
        {
            AddColumn(RecentsLocation, select?.FirstOrDefault());
            return;
        }

        // The chain of folders from a natural root (the home folder, or the drive) down to the current one.
        var chain = new List<string>();
        string? path = _location.TrimEnd('\\');
        string home = FinderAssets.Home.TrimEnd('\\');
        bool underHome = (path + "\\").StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase);
        while (path != null)
        {
            chain.Insert(0, path.Length == 2 ? path + "\\" : path);
            if (underHome && FinderAssets.Same(path, home)) break;
            path = Path.GetDirectoryName(path)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(path)) break;
        }
        for (int i = 0; i < chain.Count; i++)
        {
            string? child = i + 1 < chain.Count ? chain[i + 1] : select?.FirstOrDefault();
            AddColumn(chain[i], child);
        }
        ScrollColumnsToEnd();
    }

    private ListBox AddColumn(string folder, string? selectPath)
    {
        var list = new ListBox
        {
            Style = (Style)FindResource("FinderList"),
            ItemContainerStyle = (Style)FindResource("ColumnItem"),
            ItemTemplate = (DataTemplate)FindResource("ColumnTemplate"),
            Tag = folder,
            Padding = new Thickness(0, 6, 0, 6),
            SelectionMode = SelectionMode.Extended,
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        var host = new Border { Width = 240, BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        host.SetResourceReference(Border.BorderBrushProperty, "Separator");
        ColumnsHost.Children.Add(host);
        _columns.Add(list);
        WireList(list);
        list.SelectionChanged += (_, _) => OnColumnSelection(list);
        list.PreviewKeyDown += (_, e) => OnColumnKey(list, e);
        LoadColumn(list, selectPath);
        return list;
    }

    private async void LoadColumn(ListBox list, string? selectPath)
    {
        var folder = (string)list.Tag;
        var (entries, error) = await Task.Run(() => Enumerate(folder));
        if (_closed || !_columns.Contains(list)) return;
        var shown = entries.Where(x => _settings.ShowHidden || !x.IsHidden).ToList();
        shown.Sort(Compare);
        _quietSelection.Add(list);
        list.ItemsSource = shown;
        RequestIcons(shown, large: false);
        if (selectPath != null && shown.FirstOrDefault(x => FinderAssets.Same(x.FullPath, selectPath)) is { } item)
        {
            list.SelectedItem = item;
            list.ScrollIntoView(item);
            if (_columns.LastOrDefault() == list) FocusItem(list, item);
        }
        _quietSelection.Remove(list);
        if (error != null && shown.Count == 0) list.ToolTip = error;
        // The last column decides what a selected file shows.
        if (_columns.LastOrDefault() == list && list.SelectedItem is FileEntry { IsDirectory: false } file) AddPreview(file);
        UpdateChrome();
    }

    private void ReloadColumn(ListBox list, string? select)
    {
        var keep = select ?? (list.SelectedItem as FileEntry)?.FullPath;
        int index = _columns.IndexOf(list);
        if (index < 0) return;
        bool last = index == _columns.Count - 1;
        LoadColumnKeepingNext(list, keep, last);
    }

    private async void LoadColumnKeepingNext(ListBox list, string? keep, bool last)
    {
        var folder = (string)list.Tag;
        var (entries, _) = await Task.Run(() => Enumerate(folder));
        if (_closed || !_columns.Contains(list)) return;
        var shown = entries.Where(x => _settings.ShowHidden || !x.IsHidden).ToList();
        shown.Sort(Compare);
        _quietSelection.Add(list);
        list.ItemsSource = shown;
        RequestIcons(shown, large: false);
        if (keep != null && shown.FirstOrDefault(x => FinderAssets.Same(x.FullPath, keep)) is { } item)
            list.SelectedItem = item;
        _quietSelection.Remove(list);
        if (last) UpdateChrome();
    }

    private void OnColumnSelection(ListBox list)
    {
        if (_quietSelection.Contains(list)) return;
        int index = _columns.IndexOf(list);
        if (index < 0) return;

        // Everything to the right of this column goes.
        while (ColumnsHost.Children.Count > index + 1) ColumnsHost.Children.RemoveAt(ColumnsHost.Children.Count - 1);
        while (_columns.Count > index + 1) _columns.RemoveAt(_columns.Count - 1);

        var folder = (string)list.Tag;
        if (list.SelectedItems.Count == 1 && list.SelectedItem is FileEntry entry)
        {
            if (entry.IsDirectory)
            {
                AddColumn(entry.FullPath, null);
                SetLocationQuietly(entry.FullPath);
            }
            else
            {
                AddPreview(entry);
                SetLocationQuietly(folder);
            }
        }
        else
        {
            SetLocationQuietly(folder);
        }
        ScrollColumnsToEnd();
        UpdateChrome();
        QuickLookWindow.UpdateFor(this, list.SelectedItem as FileEntry);
    }

    /// <summary>Column view moves the current folder without adding history steps.</summary>
    private void SetLocationQuietly(string folder)
    {
        if (FinderAssets.Same(folder, _location)) return;
        _location = folder;
        Watch();
    }

    private void OnColumnKey(ListBox list, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        int index = _columns.IndexOf(list);
        if (e.Key == Key.Right)
        {
            if (index + 1 < _columns.Count)
            {
                var next = _columns[index + 1];
                if (next.Items.Count > 0)
                {
                    next.SelectedItem ??= next.Items[0];
                    FocusItem(next, next.SelectedItem!);
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            if (index > 0)
            {
                var previous = _columns[index - 1];
                // Selecting the parent again closes this column, like Finder.
                if (previous.SelectedItem is { } selected)
                {
                    OnColumnSelection(previous);
                    FocusItem(previous, selected);
                }
            }
            e.Handled = true;
        }
    }

    private void ScrollColumnsToEnd() =>
        Dispatcher.BeginInvoke(() => ColumnScroller.ScrollToRightEnd(), DispatcherPriority.Loaded);

    /// <summary>Column view's last column for a selected file: a large preview and its details.</summary>
    private void AddPreview(FileEntry entry)
    {
        var image = new Image { Width = 150, Height = 150, Source = entry.Icon, Margin = new Thickness(0, 24, 0, 14), Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _ = LoadLargeImageAsync(entry.FullPath, (int)Math.Ceiling(256 * Dpi)).ContinueWith(t =>
        {
            if (t.Result != null) image.Source = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());

        var name = new TextBlock { Text = entry.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 0, 16, 2) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var sub = new TextBlock { Text = $"{entry.Kind} - {entry.SizeText}", FontSize = 11.5, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 0, 16, 16) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SubText");

        var info = new Grid { Margin = new Thickness(18, 0, 18, 0) };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        DateTime created = DateTime.MinValue;
        try { created = File.GetCreationTime(entry.FullPath); } catch { }
        AddInfoRow(info, "Created", FileEntry.FormatDate(created));
        AddInfoRow(info, "Modified", entry.DateText);

        var header = new TextBlock { Text = "Information", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 0, 18, 6) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        var panel = new StackPanel { Children = { image, name, sub, header, info } };
        var host = new Border { Width = 300, Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false } };
        ColumnsHost.Children.Add(host);
    }

    private static void AddInfoRow(Grid grid, string label, string value)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 11.5, Margin = new Thickness(0, 2, 12, 2) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        var v = new TextBlock { Text = value, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 2), TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        v.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
    }

    /// <summary>A large thumbnail or icon, made on a short-lived STA thread (shell image APIs are COM).</summary>
    internal static Task<ImageSource?> LoadLargeImageAsync(string path, int px)
    {
        var done = new TaskCompletionSource<ImageSource?>();
        var thread = new Thread(() =>
        {
            try
            {
                ImageSource? image = Directory.Exists(path) ? FinderAssets.FolderIcon(path) : FinderAssets.LoadFileIcon(path, px, thumbnails: true);
                if (image is { CanFreeze: true, IsFrozen: false }) image.Freeze();
                done.SetResult(image);
            }
            catch
            {
                done.SetResult(null);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // =====================================================================
    // Path bar and status bar
    // =====================================================================

    private void UpdatePathBar()
    {
        PathBar.Children.Clear();
        if (_location.Length == 0) return;
        var list = ActiveList;
        var selected = list.SelectedItems.Count == 1 ? list.SelectedItem as FileEntry : null;

        if (_location == RecentsLocation && selected == null)
        {
            AddPathSegment(null, "", "Recents", null);
            return;
        }

        string target = selected?.FullPath ?? _location;
        var chain = new List<string>();
        string? path = target.TrimEnd('\\');
        while (!string.IsNullOrEmpty(path))
        {
            chain.Insert(0, path.Length == 2 ? path + "\\" : path);
            path = Path.GetDirectoryName(path)?.TrimEnd('\\');
        }
        for (int i = 0; i < chain.Count; i++)
        {
            bool isLast = i == chain.Count - 1;
            string p = chain[i];
            bool isRoot = i == 0;
            bool isFile = isLast && selected is { IsDirectory: false };
            if (i > 0)
            {
                var sep = new TextBlock { Text = "", FontFamily = FinderAssets.SymbolFont, FontSize = 7, Margin = new Thickness(6, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                sep.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
                PathBar.Children.Add(sep);
            }
            ImageSource? icon = isFile ? selected!.Icon : isRoot ? null : FinderAssets.FolderIcon(p);
            AddPathSegment(isFile ? null : p, isRoot ? "" : null, FinderAssets.DisplayName(p), icon);
        }
    }

    private void AddPathSegment(string? navigateTo, string? glyph, string label, ImageSource? icon)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = navigateTo != null ? Cursors.Hand : Cursors.Arrow };
        if (icon != null)
        {
            var image = new Image { Source = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            panel.Children.Add(image);
        }
        else if (glyph != null)
        {
            var g = new TextBlock { Text = glyph, FontFamily = FinderAssets.SymbolFont, FontSize = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            panel.Children.Add(g);
        }
        var text = new TextBlock { Text = label, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        panel.Children.Add(text);
        if (navigateTo != null)
        {
            panel.MouseLeftButtonUp += (_, _) => Navigate(navigateTo);
            panel.MouseEnter += (_, _) => text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            panel.MouseLeave += (_, _) => text.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        }
        PathBar.Children.Add(panel);
    }

    private void UpdateStatus()
    {
        var list = ActiveList;
        int count = list.Items.Count;
        int selected = list.SelectedItems.Count;
        string text = selected > 0 ? $"{selected} of {count} selected" : count == 1 ? "1 item" : $"{count} items";
        string? folder = _location == RecentsLocation ? null : _location;
        if (folder != null)
        {
            try
            {
                var root = Path.GetPathRoot(folder);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady) text += $", {FileEntry.FormatSize(drive.AvailableFreeSpace)} available";
                }
            }
            catch
            {
                // UNC or unavailable drive: no free space shown.
            }
        }
        StatusText.Text = text;
    }

    // =====================================================================
    // Lists: mouse, selection, drag and drop
    // =====================================================================

    private void WireList(ListBox list)
    {
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (EntryAt(e.OriginalSource) is { } entry)
            {
                OpenEntries(new List<FileEntry> { entry });
                e.Handled = true;
            }
        };
        // Reaches the list only for clicks on empty space (items handle their own clicks).
        list.MouseLeftButtonDown += (_, _) =>
        {
            CommitRename();
            list.UnselectAll();
            list.Focus();
        };
        list.PreviewMouseRightButtonDown += (_, e) =>
        {
            var entry = EntryAt(e.OriginalSource);
            if (entry == null) list.UnselectAll();
            else if (!list.SelectedItems.Contains(entry)) list.SelectedItem = entry;
            if (entry != null) FocusItem(list, entry); else list.Focus();
        };
        list.MouseRightButtonUp += (_, e) =>
        {
            ShowContextMenu(list, EntryAt(e.OriginalSource) != null);
            e.Handled = true;
        };
        list.SelectionChanged += (_, _) =>
        {
            UpdateStatus();
            UpdatePathBar();
            if (list == ActiveList) QuickLookWindow.UpdateFor(this, list.SelectedItems.Count == 1 ? list.SelectedItem as FileEntry : null);
        };

        // Drag files out (to other folders, the desktop, other apps).
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragArmed = e.OriginalSource is not TextBox && EntryAt(e.OriginalSource) != null && e.ClickCount == 1;
            _dragStart = e.GetPosition(list);
        };
        list.PreviewMouseLeftButtonUp += (_, _) => _dragArmed = false;
        list.PreviewMouseMove += (_, e) =>
        {
            if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(list);
            if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
                Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
            _dragArmed = false;
            var paths = SelectedEntries(list).Select(x => x.FullPath).ToArray();
            if (paths.Length == 0) return;
            try { DragDrop.DoDragDrop(list, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            catch { /* drop target failed */ }
        };

        // Drop files in: onto a folder item, or onto the folder being shown.
        list.DragOver += (_, e) =>
        {
            var target = DropTargetFolder(list, e.OriginalSource);
            e.Effects = target == null ? DragDropEffects.None : DropEffect(e, target);
            e.Handled = true;
        };
        list.Drop += (_, e) =>
        {
            var target = DropTargetFolder(list, e.OriginalSource);
            if (target != null) DropInto(e, target);
            e.Handled = true;
        };
    }

    private string? DropTargetFolder(ListBox list, object source)
    {
        if (EntryAt(source) is { IsDirectory: true } folder) return folder.FullPath;
        if (list.Tag is string column) return column == RecentsLocation ? null : column;
        return CurrentFolder;
    }

    private static DragDropEffects DropEffect(DragEventArgs e, string target)
    {
        if (target == RecentsLocation || !e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths == null || paths.Length == 0) return DragDropEffects.None;
        if (paths.Any(p => FinderAssets.Same(p, target))) return DragDropEffects.None;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;
        // Same drive: move; another drive: copy (like Finder and Explorer).
        bool sameDrive = string.Equals(Path.GetPathRoot(paths[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        if (sameDrive && paths.All(p => FinderAssets.Same(Path.GetDirectoryName(p), target))) return DragDropEffects.None;
        return sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private void DropInto(DragEventArgs e, string target)
    {
        var effect = DropEffect(e, target);
        if (effect == DragDropEffects.None) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        FileOperations.CopyOrMove(Handle, paths, target, move: effect == DragDropEffects.Move);
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private static FileEntry? EntryAt(object source)
    {
        var item = FindAncestor<ListBoxItem>(source as DependencyObject);
        return item?.DataContext as FileEntry;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void FocusItem(ListBox list, object item)
    {
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
        else list.Focus();
    }

    // =====================================================================
    // Actions
    // =====================================================================

    private void OpenEntries(IReadOnlyList<FileEntry> entries)
    {
        if (entries.Count == 0) return;
        bool navigated = false;
        foreach (var entry in entries)
        {
            string? folder = entry.IsDirectory ? entry.FullPath : null;
            if (folder == null && ShortcutResolver.IsShortcut(entry.FullPath) && ShortcutResolver.GetTarget(entry.FullPath) is { } target && Directory.Exists(target))
                folder = target;

            if (folder != null)
            {
                // The first folder opens here; any others get their own window.
                if (!navigated) { Navigate(folder); navigated = true; }
                else OpenFolder(folder);
            }
            else if (Archive.IsZip(entry.FullPath))
            {
                ExtractHere(entry.FullPath); // like Archive Utility: unzip next to it
            }
            else
            {
                FileOperations.Open(entry.FullPath);
            }
        }
    }

    // =====================================================================
    // Zip (Archive Utility)
    // =====================================================================

    private async void ExtractHere(string zipPath)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.AppStarting;
            var result = await Archive.ExtractAsync(zipPath);
            if (_closed) return;
            if (FinderAssets.Same(Path.GetDirectoryName(zipPath) ?? "", _location)) Navigate(_location, addHistory: false, select: new[] { result });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"“{Path.GetFileName(zipPath)}” could not be expanded.\n\n{ex.Message}", "Archive Utility", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async void CompressSelection()
    {
        var paths = SelectedEntries(ActiveList).Select(x => x.FullPath).ToList();
        if (paths.Count == 0) return;
        try
        {
            Mouse.OverrideCursor = Cursors.AppStarting;
            var zip = await Archive.CompressAsync(paths);
            if (_closed) return;
            if (FinderAssets.Same(Path.GetDirectoryName(zip) ?? "", _location)) Navigate(_location, addHistory: false, select: new[] { zip });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The items could not be compressed.\n\n{ex.Message}", "Archive Utility", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>A .zip opened outside Finder (desktop double-click): extracted next to itself, like macOS.</summary>
    public static async void ExtractFromDesktop(string zipPath)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.AppStarting;
            await Archive.ExtractAsync(zipPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"“{Path.GetFileName(zipPath)}” could not be expanded.\n\n{ex.Message}", "Archive Utility", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void TrashSelection()
    {
        var paths = SelectedEntries(ActiveList).Select(x => x.FullPath).ToList();
        if (paths.Count == 0) return;
        FileOperations.MoveToTrash(Handle, paths);
        ScheduleReload();
    }

    private void CopySelection(bool cut)
    {
        var selected = SelectedEntries(ActiveList);
        if (selected.Count == 0) return;
        foreach (var e in _shown) e.IsCut = false;
        FileOperations.SetClipboard(selected.Select(x => x.FullPath).ToList(), cut);
        if (cut) foreach (var e in selected) e.IsCut = true;
    }

    private void Paste()
    {
        var folder = CurrentFolder;
        if (folder == null) { SystemSounds.Beep.Play(); return; }
        var (paths, cut) = FileOperations.GetClipboard();
        if (paths.Count == 0) return;
        FileOperations.CopyOrMove(Handle, paths, folder, cut);
        if (cut)
        {
            try { Clipboard.Clear(); } catch { }
        }
        ScheduleReload();
    }

    private void Duplicate()
    {
        var selected = SelectedEntries(ActiveList);
        if (selected.Count == 0) return;
        foreach (var group in selected.GroupBy(x => Path.GetDirectoryName(x.FullPath)))
            if (group.Key != null) FileOperations.CopyOrMove(Handle, group.Select(x => x.FullPath).ToList(), group.Key, move: false);
        ScheduleReload();
    }

    private void NewFolder()
    {
        var folder = CurrentFolder;
        if (folder == null) { SystemSounds.Beep.Play(); return; }
        var path = FileOperations.NewFolder(folder);
        if (path == null) { SystemSounds.Beep.Play(); return; }

        if (EffectiveView == ViewMode.Columns)
        {
            var column = _columns.FirstOrDefault(c => FinderAssets.Same(c.Tag as string, folder));
            if (column != null) RenameAfterColumnLoad(column, path);
            return;
        }
        Load(new[] { path }, renamePath: path);
    }

    private async void RenameAfterColumnLoad(ListBox column, string path)
    {
        var folder = (string)column.Tag;
        var (entries, _) = await Task.Run(() => Enumerate(folder));
        if (_closed || !_columns.Contains(column)) return;
        var shown = entries.Where(x => _settings.ShowHidden || !x.IsHidden).ToList();
        shown.Sort(Compare);
        _quietSelection.Add(column);
        column.ItemsSource = shown;
        RequestIcons(shown, large: false);
        _quietSelection.Remove(column);
        if (shown.FirstOrDefault(x => FinderAssets.Same(x.FullPath, path)) is { } entry)
        {
            column.SelectedItem = entry;
            column.ScrollIntoView(entry);
            StartRename(entry);
        }
    }

    private void GetInfo()
    {
        var selected = SelectedEntries(ActiveList);
        if (selected.Count > 0)
        {
            foreach (var e in selected.Take(5)) FileOperations.ShowProperties(Handle, e.FullPath);
        }
        else if (CurrentFolder is { } folder)
        {
            FileOperations.ShowProperties(Handle, folder);
        }
    }

    private void CopyPaths()
    {
        var selected = SelectedEntries(ActiveList).Select(x => x.FullPath).ToList();
        if (selected.Count == 0 && CurrentFolder is { } folder) selected.Add(folder);
        if (selected.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, selected)); } catch { }
    }

    private void ToggleHidden()
    {
        _settings.ShowHidden = !_settings.ShowHidden;
        if (_view == ViewMode.Columns && !_searching)
        {
            foreach (var column in _columns.ToList()) ReloadColumn(column, null);
            return;
        }
        Display(SelectedEntries(ActiveList).Select(x => x.FullPath).ToList());
    }

    private void QuickLook()
    {
        var list = ActiveList;
        var entry = list.SelectedItems.Count >= 1 ? list.SelectedItem as FileEntry : null;
        if (entry == null) { QuickLookWindow.CloseFor(this); return; }
        QuickLookWindow.Toggle(this, entry);
    }

    // ---------------- Rename in place ----------------

    private void StartRename(FileEntry? entry)
    {
        if (entry == null) return;
        CommitRename();
        var list = ActiveList;
        list.SelectedItems.Clear();
        list.SelectedItem = entry;
        list.ScrollIntoView(entry);
        _renaming = entry;
        entry.EditName = entry.Name;
        entry.IsRenaming = true;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible || box.DataContext is not FileEntry entry) return;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            int dot = entry.IsDirectory ? -1 : box.Text.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : box.Text.Length); // the name, not the extension
        }, DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRename();

    private void CommitRename()
    {
        var entry = _renaming;
        if (entry == null) return;
        _renaming = null;
        entry.IsRenaming = false;
        string newName = entry.EditName.Trim();
        if (newName == entry.Name || newName.Length == 0)
        {
            RefocusList(entry);
            return;
        }
        var error = FileOperations.Rename(entry.FullPath, newName);
        if (error != null)
        {
            MessageBox.Show(this, error, "Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefocusList(entry);
            return;
        }
        var newPath = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, newName);
        if (_searching) { RunSearch(); return; }
        Reload(newPath);
    }

    private void CancelRename()
    {
        var entry = _renaming;
        if (entry == null) return;
        _renaming = null;
        entry.IsRenaming = false;
        RefocusList(entry);
    }

    private void RefocusList(FileEntry entry) =>
        Dispatcher.BeginInvoke(() => FocusItem(ActiveList, entry), DispatcherPriority.Input);

    // =====================================================================
    // Search
    // =====================================================================

    private void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch() =>
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); Keyboard.Focus(SearchBox); SearchBox.SelectAll(); }, DispatcherPriority.Input);

    /// <summary>The search capsule stays in the toolbar (Liquid Glass); nothing to hide.</summary>
    private void CloseSearchField()
    {
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = SearchBox.Text.Length == 0;
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CancelSearch(clearBox: true);
                ReturnFromSearch();
                ActiveList.Focus();
                e.Handled = true;
                break;
            case Key.Enter:
                _searchTimer.Stop();
                RunSearch();
                e.Handled = true;
                break;
            case Key.Down:
                var list = ActiveList;
                if (list.Items.Count > 0)
                {
                    list.SelectedItem ??= list.Items[0];
                    FocusItem(list, list.SelectedItem!);
                }
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) CloseSearchField();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        CancelSearch(clearBox: true);
        ReturnFromSearch();
    }

    /// <summary>Back to the folder itself after a search ends.</summary>
    private void ReturnFromSearch()
    {
        CloseSearchField();
        ApplyViewVisibility();
        if (_view == ViewMode.Columns) { IconList.ItemsSource = null; DetailList.ItemsSource = null; BuildColumns(); }
        else Load();
        UpdateChrome();
    }

    private void CancelSearch(bool clearBox)
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searching = false;
        _searchTimer.Stop();
        if (clearBox && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            _searchTimer.Stop(); // TextChanged restarted it
        }
    }

    /// <summary>Finds items whose name contains the text, in this folder and every folder inside it.</summary>
    private async void RunSearch()
    {
        string query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            if (_searching)
            {
                CancelSearch(clearBox: false);
                ReturnFromSearch();
            }
            return;
        }

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        bool wasSearching = _searching;
        _searching = true;
        if (!wasSearching && _view == ViewMode.Columns)
        {
            ColumnsHost.Children.Clear();
            _columns.Clear();
        }
        string root = _location == RecentsLocation ? FinderAssets.Home : _location;
        bool hidden = _settings.ShowHidden;
        _entries = new List<FileEntry>();
        _loadError = null;
        Display();

        var found = new List<FileEntry>();
        var token = cts.Token;
        try
        {
            await Task.Run(() =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                    AttributesToSkip = hidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
                };
                var batch = new List<FileEntry>();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*" + query + "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    batch.Add(FileEntry.FromInfo(info));
                    if (found.Count + batch.Count >= 2000) break;
                    if (clock.ElapsedMilliseconds > 300 && batch.Count > 0)
                    {
                        var chunk = batch.ToList();
                        batch.Clear();
                        clock.Restart();
                        Dispatcher.Invoke(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            found.AddRange(chunk);
                            _entries = found.ToList();
                            Display(SelectedEntries(ActiveList).Select(x => x.FullPath).ToList());
                        });
                    }
                }
                Dispatcher.Invoke(() => { if (!token.IsCancellationRequested) found.AddRange(batch); });
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _loadError = ex.Message;
        }
        if (token.IsCancellationRequested || _closed) return;
        _entries = found;
        cts.Cancel(); // finished: "No Results" instead of "Searching…"
        Display(SelectedEntries(ActiveList).Select(x => x.FullPath).ToList());
    }

    // =====================================================================
    // Menus
    // =====================================================================

    private static MenuItem Item(string label, Action action, bool enabled = true, string? shortcut = null)
    {
        var header = new DockPanel { LastChildFill = true, MinWidth = 170 };
        if (shortcut != null)
        {
            var keys = new TextBlock { Text = shortcut, Opacity = 0.55, Margin = new Thickness(24, 0, 0, 0) };
            DockPanel.SetDock(keys, Dock.Right);
            header.Children.Add(keys);
        }
        header.Children.Add(new TextBlock { Text = label });
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static string Check(bool on) => on ? "✓  " : "     ";

    private void ShowContextMenu(ListBox list, bool onItem)
    {
        var selected = SelectedEntries(list);
        var menu = new ContextMenu();
        if (onItem && selected.Count > 0)
        {
            bool single = selected.Count == 1;
            var first = selected[0];
            menu.Items.Add(Item("Open", () => OpenEntries(selected), true, "Enter"));
            if (single && !first.IsDirectory) menu.Items.Add(Item("Open With…", () => FileOperations.OpenWith(first.FullPath)));
            if (single && first.IsDirectory) menu.Items.Add(Item("Open in New Window", () => OpenFolder(first.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Move to Trash", TrashSelection, true, "Delete"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Get Info", GetInfo, true, "Ctrl+I"));
            menu.Items.Add(Item("Rename", () => StartRename(first), single, "F2"));
            menu.Items.Add(Item("Duplicate", Duplicate, true, "Ctrl+D"));
            menu.Items.Add(Item("Quick Look", QuickLook, true, "Space"));
            menu.Items.Add(new Separator());
            if (single && Archive.IsZip(first.FullPath)) menu.Items.Add(Item("Extract Here", () => ExtractHere(first.FullPath)));
            menu.Items.Add(Item(single ? $"Compress “{first.Name}”" : $"Compress {selected.Count} Items", CompressSelection));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Copy", () => CopySelection(false), true, "Ctrl+C"));
            menu.Items.Add(Item("Cut", () => CopySelection(true), true, "Ctrl+X"));
            menu.Items.Add(Item("Copy as Pathname", CopyPaths));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Show in File Explorer", () => FileOperations.ShowInExplorer(first.FullPath)));
            if (single && first.IsDirectory) menu.Items.Add(Item("Open in Terminal", () => FileOperations.OpenTerminal(first.FullPath)));
        }
        else
        {
            AddFolderMenuItems(menu, includeWindowItems: false);
        }
        menu.PlacementTarget = list;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void AddFolderMenuItems(ContextMenu menu, bool includeWindowItems)
    {
        var folder = CurrentFolder;
        if (includeWindowItems)
        {
            menu.Items.Add(Item("New Finder Window", () => OpenFolder(folder ?? FinderAssets.Home), true, "Ctrl+N"));
        }
        menu.Items.Add(Item("New Folder", NewFolder, folder != null, "Ctrl+Shift+N"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Get Info", GetInfo, folder != null || SelectedEntries(ActiveList).Count > 0, "Ctrl+I"));
        menu.Items.Add(Item("Paste Item", Paste, folder != null && FileOperations.ClipboardHasFiles(), "Ctrl+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Check(_view == ViewMode.Icons) + "as Icons", () => SetView(ViewMode.Icons), true, "Ctrl+1"));
        menu.Items.Add(Item(Check(_view == ViewMode.List) + "as List", () => SetView(ViewMode.List), true, "Ctrl+2"));
        menu.Items.Add(Item(Check(_view == ViewMode.Columns) + "as Columns", () => SetView(ViewMode.Columns), true, "Ctrl+3"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(_settings.ShowHidden ? "Hide Hidden Files" : "Show Hidden Files", ToggleHidden, true, "Ctrl+Shift+."));
        if (includeWindowItems)
        {
            menu.Items.Add(Item(_settings.ShowPathBar ? "Hide Path Bar" : "Show Path Bar", () => { _settings.ShowPathBar = !_settings.ShowPathBar; ApplyBars(); }));
            menu.Items.Add(Item(_settings.ShowStatusBar ? "Hide Status Bar" : "Show Status Bar", () => { _settings.ShowStatusBar = !_settings.ShowStatusBar; ApplyBars(); }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy as Pathname", CopyPaths, folder != null));
        menu.Items.Add(Item("Open in Terminal", () => FileOperations.OpenTerminal(folder!), folder != null));
        menu.Items.Add(Item("Show in File Explorer", () => FileOperations.ShowInExplorer(folder!), folder != null));
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var selected = SelectedEntries(ActiveList);
        if (selected.Count > 0)
        {
            menu.Items.Add(Item("Open", () => OpenEntries(selected), true, "Enter"));
            menu.Items.Add(Item("Rename", () => StartRename(selected[0]), selected.Count == 1, "F2"));
            menu.Items.Add(Item("Duplicate", Duplicate, true, "Ctrl+D"));
            menu.Items.Add(Item("Move to Trash", TrashSelection, true, "Delete"));
            menu.Items.Add(Item("Copy", () => CopySelection(false), true, "Ctrl+C"));
            menu.Items.Add(new Separator());
        }
        AddFolderMenuItems(menu, includeWindowItems: true);
        menu.PlacementTarget = ActionButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item(Check(_sort == SortKey.Name) + "Name", () => SetSort(SortKey.Name, true)));
        menu.Items.Add(Item(Check(_sort == SortKey.Kind) + "Kind", () => SetSort(SortKey.Kind, true)));
        menu.Items.Add(Item(Check(_sort == SortKey.Date) + "Date Modified", () => SetSort(SortKey.Date, false)));
        menu.Items.Add(Item(Check(_sort == SortKey.Size) + "Size", () => SetSort(SortKey.Size, false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Check(_ascending) + "Ascending", () => SetSort(_sort, true)));
        menu.Items.Add(Item(Check(!_ascending) + "Descending", () => SetSort(_sort, false)));
        menu.PlacementTarget = SortButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // =====================================================================
    // Keyboard
    // =====================================================================

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Window-wide shortcuts, also while typing.
        if (ctrl && !shift && key == Key.W) { Close(); e.Handled = true; return; }
        if (ctrl && !shift && key == Key.N) { OpenFolder(CurrentFolder ?? FinderAssets.Home); e.Handled = true; return; }
        if (ctrl && !shift && key == Key.F) { OpenSearch(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;

        bool handled = true;
        if (ctrl && shift)
        {
            switch (key)
            {
                case Key.N: NewFolder(); break;
                case Key.OemPeriod: ToggleHidden(); break;
                case Key.D: Navigate(FinderAssets.Desktop); break;
                case Key.O: Navigate(FinderAssets.Documents); break;
                case Key.H: Navigate(FinderAssets.Home); break;
                case Key.L: Navigate(FinderAssets.Downloads); break;
                case Key.A: Navigate(FinderAssets.Applications); break;
                case Key.F: Navigate(RecentsLocation); break;
                case Key.G: GoToFolderPrompt(this); break;
                case Key.C: Navigate(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"); break;
                default: handled = false; break;
            }
        }
        else if (ctrl)
        {
            switch (key)
            {
                case Key.C: CopySelection(false); break;
                case Key.X: CopySelection(true); break;
                case Key.V: Paste(); break;
                case Key.D: Duplicate(); break;
                case Key.I: GetInfo(); break;
                case Key.D1: SetView(ViewMode.Icons); break;
                case Key.D2: SetView(ViewMode.List); break;
                case Key.D3: SetView(ViewMode.Columns); break;
                case Key.Up: GoUp(); break;
                case Key.Down: OpenEntries(SelectedEntries(ActiveList)); break;
                case Key.OemOpenBrackets: GoBack(); break;
                case Key.OemCloseBrackets: GoForward(); break;
                case Key.Back: TrashSelection(); break;
                case Key.Y: QuickLook(); break;
                default: handled = false; break;
            }
        }
        else if (alt)
        {
            switch (key)
            {
                case Key.Left: GoBack(); break;
                case Key.Right: GoForward(); break;
                case Key.Up: GoUp(); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (key)
            {
                case Key.Enter: OpenEntries(SelectedEntries(ActiveList)); break;
                case Key.Space: QuickLook(); break;
                case Key.F2: StartRename(SelectedEntries(ActiveList).FirstOrDefault()); break;
                case Key.Delete: TrashSelection(); break;
                case Key.Back: GoBack(); break;
                case Key.BrowserBack: GoBack(); break;
                case Key.BrowserForward: GoForward(); break;
                case Key.F5: Reload(); break;
                case Key.Escape:
                    if (QuickLookWindow.IsOpenFor(this)) QuickLookWindow.CloseFor(this);
                    else if (_searching) { CancelSearch(clearBox: true); ReturnFromSearch(); }
                    else handled = false;
                    break;
                default: handled = false; break;
            }
        }
        if (handled) e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        // Mouse back / forward buttons.
        if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
        base.OnMouseUp(e);
    }
}
