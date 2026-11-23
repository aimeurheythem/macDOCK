using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacDock.Config;
using MacDock.Services;

namespace MacDock;

/// <summary>
/// Settings editor. Changes apply live through <see cref="SettingsChanged"/>;
/// the owner saves config.json when the window closes.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] PresetColors = { "#FFFFFF", "#D6D6D6", "#7A7A7A", "#1E1E1E", "#000000", "#2F6FDB" };

    private readonly DockConfig _config;
    private bool _loading = true;

    /// <summary>Raised whenever a setting changes, so the dock can apply it immediately.</summary>
    public event Action? SettingsChanged;

    public SettingsWindow(DockConfig config)
    {
        _config = config;
        InitializeComponent();

        foreach (var hex in PresetColors)
        {
            var swatch = new Button
            {
                Style = (Style)Resources["Swatch"],
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                ToolTip = hex,
            };
            swatch.Click += (_, _) => ColorBox.Text = hex;
            SwatchPanel.Children.Add(swatch);
        }

        IconSizeSlider.Minimum = DockConfig.MinIconSize;
        IconSizeSlider.Maximum = DockConfig.MaxIconSize;
        MagnifySlider.Maximum = DockConfig.MaxMagnifiedSize;

        IconSizeSlider.Value = config.IconSize;
        MagnifySlider.Minimum = config.IconSize;
        MagnifySlider.Value = config.MagnifiedSize;
        OpacitySlider.Value = Math.Round(config.DockOpacity * 100);
        ColorBox.Text = config.DockColor;
        AutoHideCheck.IsChecked = config.AutoHide;
        GenieCheck.IsChecked = config.GenieEffect;
        if (GenieEffect.IsWindhawkGenieActive()) WindhawkWarning.Visibility = Visibility.Visible;
        HideTaskbarCheck.IsChecked = config.HideTaskbar;
        MenuBarCheck.IsChecked = config.ShowMenuBar;
        StartButtonCheck.IsChecked = config.ShowStartButton;
        WinKeyCheck.IsChecked = config.WinKeyOpensStart;
        TrashCheck.IsChecked = config.ShowTrash;
        WindowButtonsCheck.IsChecked = config.MacWindowButtons;
        FolderIconsCheck.IsChecked = config.BlueDesktopFolders || Finder.WindowsFolderIcons.IsApplied();
        FinderFoldersCheck.IsChecked = config.FinderOpensFolders;
        CursorsCheck.IsChecked = config.MacCursors;
        StartupCheck.IsChecked = StartupService.IsEnabled;
        UpdateLabels();

        IconSizeSlider.ValueChanged += (_, _) => OnIconSizeChanged();
        MagnifySlider.ValueChanged += (_, _) => Apply(() => _config.MagnifiedSize = MagnifySlider.Value);
        OpacitySlider.ValueChanged += (_, _) => Apply(() => _config.DockOpacity = OpacitySlider.Value / 100);
        ColorBox.TextChanged += (_, _) => OnColorChanged();
        AutoHideCheck.Click += (_, _) => Apply(() => _config.AutoHide = AutoHideCheck.IsChecked == true);
        GenieCheck.Click += (_, _) => Apply(() => _config.GenieEffect = GenieCheck.IsChecked == true);
        HideTaskbarCheck.Click += (_, _) => Apply(() => _config.HideTaskbar = HideTaskbarCheck.IsChecked == true);
        MenuBarCheck.Click += (_, _) => Apply(() => _config.ShowMenuBar = MenuBarCheck.IsChecked == true);
        StartButtonCheck.Click += (_, _) => Apply(() => _config.ShowStartButton = StartButtonCheck.IsChecked == true);
        WinKeyCheck.Click += (_, _) => Apply(() => _config.WinKeyOpensStart = WinKeyCheck.IsChecked == true);
        TrashCheck.Click += (_, _) => Apply(() => _config.ShowTrash = TrashCheck.IsChecked == true);
        WindowButtonsCheck.Click += (_, _) => Apply(() => _config.MacWindowButtons = WindowButtonsCheck.IsChecked == true);
        FolderIconsCheck.Click += async (_, _) =>
        {
            bool on = FolderIconsCheck.IsChecked == true;
            FolderIconsCheck.IsEnabled = false;
            Apply(() => _config.BlueDesktopFolders = on); // desktop folders: no permission needed
            if (on != Finder.WindowsFolderIcons.IsApplied()) await Finder.WindowsFolderIcons.SetAsync(on); // the rest of Windows
            FolderIconsCheck.IsChecked = _config.BlueDesktopFolders || Finder.WindowsFolderIcons.IsApplied();
            FolderIconsCheck.IsEnabled = true;
        };
        FinderFoldersCheck.Click += (_, _) => Apply(() => _config.FinderOpensFolders = FinderFoldersCheck.IsChecked == true);
        CursorsCheck.Click += (_, _) => Apply(() => _config.MacCursors = CursorsCheck.IsChecked == true);
        StartupCheck.Click += (_, _) => StartupService.SetEnabled(StartupCheck.IsChecked == true);
        OpenConfigButton.Click += (_, _) => OpenConfigFile();
        OpenIconsButton.Click += (_, _) =>
        {
            CustomIcons.EnsureFolder();
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CustomIcons.Folder}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"Open icons folder failed: {ex.Message}"); }
        };
        CloseButton.Click += (_, _) => Close();

        _loading = false;
    }

    private void OnIconSizeChanged()
    {
        // Magnified size can never be smaller than the icon size.
        MagnifySlider.Minimum = IconSizeSlider.Value;
        if (MagnifySlider.Value < IconSizeSlider.Value) MagnifySlider.Value = IconSizeSlider.Value;
        Apply(() =>
        {
            _config.IconSize = IconSizeSlider.Value;
            _config.MagnifiedSize = MagnifySlider.Value;
        });
    }

    private void OnColorChanged()
    {
        if (!TryParseColor(ColorBox.Text, out _))
        {
            ColorBox.BorderBrush = Brushes.IndianRed;
            return;
        }
        ColorBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        Apply(() => _config.DockColor = ColorBox.Text.Trim());
    }

    private void Apply(Action change)
    {
        if (_loading) return;
        change();
        _config.Normalize();
        UpdateLabels();
        SettingsChanged?.Invoke();
    }

    private void UpdateLabels()
    {
        IconSizeText.Text = $"{IconSizeSlider.Value:0}px";
        MagnifyText.Text = MagnifySlider.Value <= IconSizeSlider.Value ? "Off" : $"{MagnifySlider.Value:0}px";
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        color = Colors.White;
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            color = (Color)ColorConverter.ConvertFromString(text.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ConfigService.ConfigPath) { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{ConfigService.ConfigPath}\"")); }
            catch (Exception ex) { Debug.WriteLine($"Open config failed: {ex.Message}"); }
        }
    }
}
