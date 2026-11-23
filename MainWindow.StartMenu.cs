using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MacDock.Config;
using MacDock.Controls;
using MacDock.Models;
using MacDock.Services;

namespace MacDock;

/// <summary>Start button in the dock and the Start menu window.</summary>
public partial class MainWindow : IStartMenuHost
{
    private const double StartMenuGap = 12; // DIPs between the dock and the Start menu

    private readonly AppCatalog _catalog = new();
    private DockIconView? _startView;
    private StartMenuWindow? _startMenu;

    private bool IsStartMenuOpen => _startMenu?.IsOpen == true;

    private WinKeyInterceptor? _winKeys;

    /// <summary>Starts or stops the Windows-key takeover to match the setting.</summary>
    private void ApplyWinKeySetting()
    {
        if (_config.WinKeyOpensStart && _winKeys == null)
        {
            _winKeys = new WinKeyInterceptor();
            _winKeys.Tapped += () => Dispatcher.BeginInvoke(ToggleSpotlight); // like Cmd+Space on a Mac
            _winKeys.TakeSettingsKey = () => _config.MacSystemSettings;
            var keys = _winKeys;
            GameMode.Changed += on => keys.SetPaused(on); // in a game the Win key is the game's (and no key passes through MacDock)
            _winKeys.SettingsKey += () => Dispatcher.BeginInvoke(() => SystemSettingsWindow.Open());
        }
        else if (!_config.WinKeyOpensStart && _winKeys != null)
        {
            _winKeys.Dispose();
            _winKeys = null;
        }
    }

    private DockIconView GetStartView()
    {
        return _startView ??= CreateView(new DockItem
        {
            Name = "Finder",
            Path = "",
            Kind = DockItemKind.StartButton,
            Icon = FinderIcon.Create(),
        });
    }

    private SpotlightWindow? _spotlight;
    private DateTime _spotlightOpenedAt;

    /// <summary>Finder (dock) and the menu bar's magnifier: the Spotlight search bar; a second click closes it.</summary>
    private void ToggleSpotlight()
    {
        try
        {
            var open = _spotlight;
            if (open != null)
            {
                // Already fading out (e.g. clicking the dock just took its focus): that click meant "close".
                if (open.IsClosing && DateTime.UtcNow - open.ClosingStarted < TimeSpan.FromSeconds(1)) return;
                if (open.IsClosing)
                {
                    open.Close(); // stuck fading out: finish it and open a fresh one below
                }
                else
                {
                    // A second press right after opening is a bounce (key repeat, a double event): keep it open.
                    if (DateTime.UtcNow - _spotlightOpenedAt < TimeSpan.FromMilliseconds(350)) return;
                    open.CloseAnimated();
                    return;
                }
            }

            var spotlight = new SpotlightWindow(_catalog);
            spotlight.Closed += (_, _) => { if (ReferenceEquals(_spotlight, spotlight)) _spotlight = null; };
            _spotlight = spotlight;
            _spotlightOpenedAt = DateTime.UtcNow;
            spotlight.ShowCentered();
        }
        catch (Exception ex)
        {
            CrashLog.Write("ToggleSpotlight", ex);
            _spotlight = null;
        }
    }

    /// <summary>
    /// Loads the app list in the background a few seconds after startup (not during it: icon extraction
    /// keeps Explorer busy and would compete with the taskbar switch), so the first open is instant.
    /// </summary>
    private void PreloadStartMenu()
    {
        FileSearch.WarmUp(); // Spotlight's first file search is then instant
        // The app list Spotlight searches; loaded a few seconds after startup (icon extraction keeps
        // Explorer busy and would compete with the taskbar switch). The old Start menu is no longer used.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _catalog.IconPixels = (int)Math.Round(40 * scale);      // Spotlight's top hit
            _catalog.SmallIconPixels = (int)Math.Round(26 * scale); // Spotlight's rows
            _ = _catalog.RefreshAsync();
        };
        delay.Start();
    }

    private async Task PreloadAsync()
    {
        await _catalog.RefreshAsync();
        _startMenu ??= new StartMenuWindow(_config, _catalog, this);
        _startMenu.Prewarm();
    }

    private void ToggleStartMenu()
    {
        _startMenu ??= new StartMenuWindow(_config, _catalog, this);

        // Centered on the screen like Windows 11, just above the dock background.
        var bg = Dock.BackgroundBounds;
        double screenCenter = Left + ActualWidth / 2;
        // Ignore the auto-hide slide: the menu sits where the dock is when shown.
        double dockTop = Top + (bg.IsEmpty ? ActualHeight - Dock.DockHeight : bg.Top);
        _startMenu.Toggle(new Point(screenCenter, dockTop - StartMenuGap));
    }

    // ---------------- IStartMenuHost ----------------

    public bool IsPinnedToDock(StartApp app) =>
        _config.PinnedApps.Any(p => string.Equals(p.Path, app.LaunchPath, StringComparison.OrdinalIgnoreCase)) ||
        (app.TargetPath != null && _pinnedViews.Any(v => PathHelper.AreSame(v.Item.TargetPath, app.TargetPath)));

    public void PinToDock(StartApp app)
    {
        if (IsPinnedToDock(app)) return;
        _config.PinnedApps.Add(new PinnedApp { Path = app.LaunchPath, Name = app.Name, Target = app.TargetPath });
        SaveAndRebuild();
    }

    public void UnpinFromDock(StartApp app)
    {
        var targets = _pinnedViews
            .Where(v => app.TargetPath != null && PathHelper.AreSame(v.Item.TargetPath, app.TargetPath))
            .Select(v => v.Item.Pinned)
            .ToHashSet();
        _config.PinnedApps.RemoveAll(p =>
            string.Equals(p.Path, app.LaunchPath, StringComparison.OrdinalIgnoreCase) || targets.Contains(p));
        SaveAndRebuild();
    }

    public void SaveConfig() => ConfigService.Save(_config);
}
