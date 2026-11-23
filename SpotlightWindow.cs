using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MacDock.Models;
using MacDock.Native;
using MacDock.Services;

namespace MacDock;

/// <summary>
/// macOS Spotlight: a glass search bar in the middle of the screen. Finds apps (instantly, from the
/// Start menu's catalog), folders and documents (Windows Search index) and offers a web search.
/// Up/Down choose, Enter opens, Esc or clicking elsewhere closes.
/// </summary>
internal sealed class SpotlightWindow : Window
{
    private const double PanelWidth = 680;
    // Text follows what is behind the glass (decided when Spotlight opens): white on dark, black on light.
    private readonly Brush Text;
    private readonly Brush SubText;
    private static readonly Brush Selected = Frozen(0xFF, 0x3D, 0x84, 0xF7);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly AppCatalog _catalog;
    private readonly Border _panel;
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly StackPanel _results = new();
    private readonly Border _resultsHost;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly List<(Border Row, Action Open)> _rows = new();
    private CancellationTokenSource? _search;
    private int _selected = -1;
    private bool _closing;

    public SpotlightWindow(AppCatalog catalog)
    {
        _catalog = catalog;
        // The glass is the dock's; the wallpaper behind Spotlight is its middle band.
        bool dark = GlassTheme.LooksDark(WallpaperTone.Brightness(0.15, 0.55));
        Text = dark ? Brushes.White : Frozen(0xFF, 0x1D, 0x1D, 0x1F);
        SubText = dark ? Frozen(0xA6, 0xFF, 0xFF, 0xFF) : Frozen(0x8C, 0x1D, 0x1D, 0x1F);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth;
        Title = "Spotlight";
        FontFamily = MacFonts.Text;
        UseLayoutRounding = true;
        MacFonts.Smooth(this);

        _box = new TextBox
        {
            FontSize = 22, FontWeight = FontWeights.Light, Foreground = Text, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, CaretBrush = Text,
            FocusVisualStyle = null,
        };
        _placeholder = new TextBlock { Text = "Spotlight Search", FontSize = 22, FontWeight = FontWeights.Light, Foreground = SubText, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        var searchRow = new DockPanel { Height = 58, Margin = new Thickness(18, 0, 18, 0) };
        searchRow.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 20, Foreground = SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var field = new Grid();
        field.Children.Add(_placeholder);
        field.Children.Add(_box);
        searchRow.Children.Add(field);

        _resultsHost = new Border
        {
            BorderBrush = dark ? Frozen(0x33, 0xFF, 0xFF, 0xFF) : Frozen(0x1F, 0, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed,
            Child = new ScrollViewer { MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _results, Padding = new Thickness(8, 6, 8, 8) },
        };

        double radius = Glass.Radius(16);
        _panel = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = GlassTheme.Background, // exactly the dock's glass
            BorderBrush = GlassTheme.Rim, BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { searchRow, _resultsHost } },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.97, 0.97),
            Opacity = 0,
        };
        Content = _panel;

        _box.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop();
            _debounce.Start();
        };
        _box.PreviewKeyDown += OnKey;
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(_box.Text); };
        Deactivated += (_, _) => OnDeactivated();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowEffects.AddExStyle(hwnd, Win32.WS_EX_TOOLWINDOW);
        };
        Loaded += (_, _) =>
        {
            Glass.Apply(_panel, radius);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
            var scale = (ScaleTransform)_panel.RenderTransform;
            var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _box.Focus();
        };
    }

    /// <summary>Centered on the primary screen, a little above the middle (like macOS), focused.</summary>
    public void ShowCentered()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - PanelWidth) / 2;
        Top = area.Top + area.Height * 0.2;
        _shownAt = DateTime.UtcNow;
        Show();
        TakeFocus();

        // The Windows key's own key-up (and the shell reacting to it) can steal the focus back a moment
        // after we open: check a few times and take it again, instead of showing and vanishing.
        var retry = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        int tries = 0;
        retry.Tick += (_, _) =>
        {
            if (_closing || ++tries > 6) { retry.Stop(); return; }
            if (!IsActive) TakeFocus();
        };
        retry.Start();
        _ = _catalog.RefreshIfStaleAsync();
    }

    private DateTime _shownAt;

    /// <summary>True from the moment closing starts (the fade-out) until the window is gone.</summary>
    public bool IsClosing => _closing;

    /// <summary>When the fade-out started.</summary>
    public DateTime ClosingStarted { get; private set; }

    private void TakeFocus()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            WindowActivator.ForceForeground(hwnd);
            Activate();
            _box.Focus();
            Keyboard.Focus(_box);
        }
        catch
        {
            // Focus is best effort; the window stays open either way.
        }
    }

    /// <summary>
    /// Clicking elsewhere closes Spotlight, like macOS. Losing focus in the first moments after opening
    /// is not the user: it is the Windows key being released, so take the focus back instead.
    /// </summary>
    private void OnDeactivated()
    {
        if (_closing) return;
        if (DateTime.UtcNow - _shownAt < TimeSpan.FromMilliseconds(600))
        {
            Dispatcher.BeginInvoke(TakeFocus, DispatcherPriority.Input);
            return;
        }
        CloseAnimated();
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        ClosingStarted = DateTime.UtcNow;
        _search?.Cancel();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => Close();
        _panel.BeginAnimation(OpacityProperty, fade);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_box.Text.Length > 0) _box.Clear(); else CloseAnimated();
                e.Handled = true;
                break;
            case Key.Down:
                Select(Math.Min(_rows.Count - 1, _selected + 1));
                e.Handled = true;
                break;
            case Key.Up:
                Select(Math.Max(0, _selected - 1));
                e.Handled = true;
                break;
            case Key.Enter:
                _debounce.Stop();
                if (_rows.Count == 0) RunSearch(_box.Text);
                if (_selected >= 0 && _selected < _rows.Count) OpenRow(_selected);
                e.Handled = true;
                break;
        }
    }

    // ---------------- Searching ----------------

    private void RunSearch(string query)
    {
        _search?.Cancel();
        query = query.Trim();
        if (query.Length == 0)
        {
            ShowResults(Array.Empty<StartApp>(), Array.Empty<FileHit>(), query);
            return;
        }

        var apps = _catalog.Search(query, 6);
        ShowResults(apps, Array.Empty<FileHit>(), query); // apps appear at once, files a moment later

        var cancel = new CancellationTokenSource();
        _search = cancel;
        Task.Run(() => FileSearch.Search(query, 24, cancel.Token), cancel.Token).ContinueWith(t =>
        {
            if (cancel.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion) return;
            Dispatcher.BeginInvoke(() => { if (!cancel.IsCancellationRequested) ShowResults(apps, t.Result, query); });
        });
    }

    private void ShowResults(IReadOnlyList<StartApp> apps, IReadOnlyList<FileHit> files, string query)
    {
        _results.Children.Clear();
        _rows.Clear();
        _selected = -1;
        if (query.Length == 0)
        {
            _resultsHost.Visibility = Visibility.Collapsed;
            return;
        }

        var folders = files.Where(f => f.IsFolder).Take(5).ToList();
        var documents = files.Where(f => !f.IsFolder).Take(7).ToList();

        // Top hit: the best app, else the best file.
        if (apps.Count > 0)
        {
            Header("Top Hit");
            AppRow(apps[0], big: true);
            if (apps.Count > 1)
            {
                Header("Applications");
                foreach (var app in apps.Skip(1)) AppRow(app, big: false);
            }
        }
        else if (files.Count > 0)
        {
            Header("Top Hit");
            FileRow(files[0], big: true);
            folders.Remove(files[0]);
            documents.Remove(files[0]);
        }
        if (folders.Count > 0)
        {
            Header("Folders");
            foreach (var f in folders) FileRow(f, big: false);
        }
        if (documents.Count > 0)
        {
            Header("Documents");
            foreach (var f in documents) FileRow(f, big: false);
        }
        Header("Web");
        AddRow(Glyph(""), $"Search the web for “{query}”", null, false,
            () => Shell.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(query)));

        _resultsHost.Visibility = Visibility.Visible;
        Select(0);
    }

    private void Header(string text) => _results.Children.Add(new TextBlock
    {
        Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = SubText, Margin = new Thickness(10, 8, 0, 3),
    });

    private void AppRow(StartApp app, bool big)
    {
        var icon = new Image { Source = app.Icon ?? app.SmallIcon, Width = big ? 40 : 26, Height = big ? 40 : 26 };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        AddRow(icon, app.Name, big ? "Application" : null, big, () => Shell.Open(app.LaunchPath));
    }

    private void FileRow(FileHit file, bool big)
    {
        var icon = new Image { Width = big ? 40 : 26, Height = big ? 40 : 26 };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        // Shell icons/thumbnails can take a moment: load them after the list is on screen.
        Dispatcher.BeginInvoke(() => icon.Source = IconService.GetIconForParsingName(file.Path, big ? 64 : 48), DispatcherPriority.Background);
        AddRow(icon, file.Name, ShortPath(file.Path), big, () =>
        {
            // Folders open in MacDock's Finder window; files with their app.
            if (System.IO.Directory.Exists(file.Path)) Finder.FinderWindow.OpenFolder(file.Path);
            else Shell.Open(file.Path);
        });
    }

    private static FrameworkElement Glyph(string glyph) => new TextBlock
    {
        Text = glyph, FontFamily = IconFont, FontSize = 18, Width = 26, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private void AddRow(FrameworkElement icon, string title, string? subtitle, bool big, Action open)
    {
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontSize = big ? 16 : 13.5, FontWeight = big ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle != null)
            texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
        var content = new DockPanel();
        content.Children.Add(icon);
        content.Children.Add(texts);

        var row = new Border
        {
            Child = content, CornerRadius = new CornerRadius(Glass.IsWindows11 ? 5 : 8), Padding = new Thickness(10, big ? 8 : 5, 10, big ? 8 : 5),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
        };
        TextElement.SetForeground(row, Text);
        int index = _rows.Count;
        row.MouseEnter += (_, _) => Select(index);
        row.MouseLeftButtonUp += (_, _) => OpenRow(index);
        _rows.Add((row, open));
        _results.Children.Add(row);
    }

    private void Select(int index)
    {
        if (_selected >= 0 && _selected < _rows.Count)
        {
            _rows[_selected].Row.Background = Brushes.Transparent;
            TextElement.SetForeground(_rows[_selected].Row, Text);
        }
        _selected = index;
        if (index < 0 || index >= _rows.Count) return;
        var row = _rows[index].Row;
        row.Background = Selected;
        TextElement.SetForeground(row, Brushes.White);
        row.BringIntoView();
    }

    private void OpenRow(int index)
    {
        var open = _rows[index].Open;
        CloseAnimated();
        open();
    }

    private static string ShortPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = System.IO.Path.GetDirectoryName(path) ?? path;
        return dir.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + dir[home.Length..] : dir;
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
