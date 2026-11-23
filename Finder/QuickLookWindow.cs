using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MacDock.Finder;

/// <summary>
/// Quick Look: Space in a Finder window previews the selected item (photos, text and code, or a large
/// icon with details). It follows the selection while open; Space or Esc closes it.
/// </summary>
internal sealed class QuickLookWindow : Window
{
    private static readonly Dictionary<Window, QuickLookWindow> Open = new();

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".ini", ".cfg", ".conf", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml", ".toml",
        ".cs", ".xaml", ".csproj", ".sln", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".c", ".h", ".cpp", ".hpp",
        ".go", ".rs", ".rb", ".php", ".html", ".htm", ".css", ".scss", ".sql", ".sh", ".bat", ".cmd", ".ps1", ".lua",
        ".kt", ".swift", ".dart", ".vue", ".gitignore", ".editorconfig", ".env", ".reg", ".srt",
    };

    private readonly bool _dark = FinderAssets.IsDarkMode();
    private readonly TextBlock _title;
    private readonly Border _body;
    private FileEntry? _entry;
    private int _version;

    private QuickLookWindow(Window owner)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Width = 760;
        Height = 540;
        MinWidth = 320;
        MinHeight = 240;
        FontFamily = MacFonts.Text;
        Title = "Quick Look";

        var text = _dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
        _title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(90, 0, 150, 0) };

        var close = HeaderButton("", "Close (Space)");
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.Margin = new Thickness(10, 0, 0, 0);
        close.MouseLeftButtonUp += (_, _) => Close();

        var openButton = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 3, 10, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0), Background = new SolidColorBrush(_dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)), Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "Open", FontSize = 12, Foreground = text },
        };
        openButton.MouseLeftButtonUp += (_, _) =>
        {
            if (_entry != null) FileOperations.Open(_entry.FullPath);
            Close();
        };

        var header = new Grid { Height = 42, Background = Brushes.Transparent };
        header.Children.Add(_title);
        header.Children.Add(close);
        header.Children.Add(openButton);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

        _body = new Border { Margin = new Thickness(0, 0, 0, 0) };
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_body);

        Content = new Border
        {
            Margin = new Thickness(18),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(_dark ? Color.FromArgb(0xF5, 0x24, 0x24, 0x26) : Color.FromArgb(0xF8, 0xF5, 0xF5, 0xF7)),
            BorderBrush = new SolidColorBrush(_dark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.35 },
            Child = layout,
        };

        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Escape) { Close(); e.Handled = true; }
        };
        Closed += (_, _) =>
        {
            if (Owner != null && Open.TryGetValue(Owner, out var w) && w == this) Open.Remove(Owner);
        };
    }

    private TextBlock HeaderButton(string glyph, string tip) => new()
    {
        Text = glyph, FontFamily = FinderAssets.SymbolFont, FontSize = 11, Width = 28, Height = 28, TextAlignment = TextAlignment.Center,
        Padding = new Thickness(0, 8, 0, 0), Foreground = _dark ? Brushes.White : Brushes.Black, Opacity = 0.65, Cursor = Cursors.Hand,
        ToolTip = tip, VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------------- Static API used by the Finder window ----------------

    public static bool IsOpenFor(Window owner) => Open.ContainsKey(owner);

    public static void Toggle(Window owner, FileEntry entry)
    {
        if (Open.TryGetValue(owner, out var existing))
        {
            existing.Close();
            return;
        }
        var window = new QuickLookWindow(owner);
        Open[owner] = window;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Show(entry);
    }

    public static void UpdateFor(Window owner, FileEntry? entry)
    {
        if (entry != null && Open.TryGetValue(owner, out var window) && window._entry != entry)
            window.Show(entry);
    }

    public static void CloseFor(Window owner)
    {
        if (Open.TryGetValue(owner, out var window)) window.Close();
    }

    // ---------------- Content ----------------

    private void Show(FileEntry entry)
    {
        _entry = entry;
        _title.Text = entry.Name;
        int version = ++_version;
        string ext = Path.GetExtension(entry.FullPath);
        if (!IsVisible) Show();

        if (!entry.IsDirectory && ImageTypes.Contains(ext)) ShowImage(entry, version);
        else if (!entry.IsDirectory && (TextTypes.Contains(ext) || IsSmallPlainText(entry))) ShowText(entry, version);
        else ShowInfo(entry, version);
    }

    private static bool IsSmallPlainText(FileEntry entry) => entry.Extension.Length == 0 && entry.Size is > 0 and < 512 * 1024;

    private async void ShowImage(FileEntry entry, int version)
    {
        _body.Child = Placeholder();
        BitmapSource? bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(entry.FullPath);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1600;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return (BitmapSource)image;
            }
            catch
            {
                return null;
            }
        });
        if (version != _version) return;
        if (bitmap == null) { ShowInfo(entry, version); return; }
        var view = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(12, 0, 12, 12) };
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
        _body.Child = view;
    }

    private async void ShowText(FileEntry entry, int version)
    {
        _body.Child = Placeholder();
        string? text = await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(entry.FullPath);
                var buffer = new byte[Math.Min(stream.Length, 256 * 1024)];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (buffer.Take(read).Count(b => b == 0) > read / 50 + 1) return null; // binary
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch
            {
                return null;
            }
        });
        if (version != _version) return;
        if (text == null) { ShowInfo(entry, version); return; }
        _body.Child = new TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5,
            Background = _dark ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)) : Brushes.White,
            Foreground = _dark ? Brushes.White : Brushes.Black, BorderThickness = new Thickness(0), Padding = new Thickness(14, 10, 14, 10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(1, 0, 1, 12), Focusable = false,
        };
    }

    private async void ShowInfo(FileEntry entry, int version)
    {
        var image = new Image { Width = 220, Height = 220, Source = entry.Icon, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var text = _dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
        var sub = _dark ? new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        var name = new TextBlock { Text = entry.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var kind = new TextBlock { Text = entry.IsDirectory ? "Folder" : $"{entry.Kind} - {entry.SizeText}", FontSize = 13, Foreground = sub, Margin = new Thickness(0, 0, 0, 4) };
        var modified = new TextBlock { Text = "Modified: " + entry.DateText, FontSize = 13, Foreground = sub };
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 28, 0), MaxWidth = 340, Children = { name, kind, modified } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 30) };
        row.Children.Add(image);
        row.Children.Add(details);
        _body.Child = row;

        var big = await FinderWindow.LoadLargeImageAsync(entry.FullPath, 512);
        if (version == _version && big != null) image.Source = big;
    }

    private UIElement Placeholder() => new TextBlock
    {
        Text = "Loading…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Foreground = _dark ? Brushes.Gray : Brushes.DimGray,
    };
}
