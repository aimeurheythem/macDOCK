using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MacDock.Config;
using MacDock.Controls;
using MacDock.Models;
using MacDock.Services;

namespace MacDock;

/// <summary>Right-click menus for dock icons and for the empty dock area.</summary>
public partial class MainWindow
{
    private void InitMenus()
    {
        // Icons handle their own right-click; anything else on the dock gets the dock menu.
        Dock.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            ShowMenu(BuildDockMenu(), Dock, centerOn: null);
            e.Handled = true;
        };
    }

    private void OnIconRightClick(DockIconView view, MouseButtonEventArgs e)
    {
        var menu = view.Item.Kind switch
        {
            DockItemKind.StartButton => BuildDockMenu(),
            DockItemKind.Trash => BuildTrashMenu(),
            _ => BuildIconMenu(view),
        };
        ShowMenu(menu, view, centerOn: view);
        e.Handled = true;
    }

    // ---------------- Menu contents ----------------

    private ContextMenu BuildIconMenu(DockIconView view)
    {
        var item = view.Item;
        var windows = _running.GetWindows(item.TargetPath);
        bool running = windows.Count > 0;
        var menu = new ContextMenu();

        menu.Items.Add(MenuItem("Open", () => OpenItem(view)));
        menu.Items.Add(new Separator());

        if (item.IsPinned)
            menu.Items.Add(MenuItem("Remove from Dock", () => Unpin(item)));
        else
            menu.Items.Add(MenuItem("Keep in Dock", () => Pin(item)));

        var explorerTarget = item.TargetPath ?? item.Path;
        menu.Items.Add(MenuItem("Show in Explorer", () => ShowInExplorer(explorerTarget),
            enabled: File.Exists(explorerTarget)));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(windows.Count > 1 ? $"Quit ({windows.Count} windows)" : "Quit",
            () => Quit(item), enabled: running));
        return menu;
    }

    private ContextMenu BuildDockMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Settings…", OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Exit MacDock", App.QuitSmoothly));
        return menu;
    }

    private static MenuItem MenuItem(string header, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Opens the menu above <paramref name="target"/>; centered on an icon, or at the cursor.</summary>
    private void ShowMenu(ContextMenu menu, UIElement target, FrameworkElement? centerOn)
    {
        menu.PlacementTarget = target;
        if (centerOn != null)
        {
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                // Sizes here are in device pixels; center horizontally over the icon image.
                double iconWidth = targetSize.Width; // the view is as wide as its icon
                return new[]
                {
                    new CustomPopupPlacement(
                        new Point((iconWidth - popupSize.Width) / 2, -popupSize.Height - 6),
                        PopupPrimaryAxis.Horizontal),
                };
            };
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
        }

        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            Dock.LabelSuppressed = true;
        };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            Dock.LabelSuppressed = false;
            Dock.ResetHover();
        };
        menu.IsOpen = true;
    }

    // ---------------- Actions ----------------

    /// <summary>Launches the app if it isn't running, otherwise brings it to the front.</summary>
    private void OpenItem(DockIconView view)
    {
        var windows = _running.GetWindows(view.Item.TargetPath);
        if (windows.Count > 0)
        {
            ActivateWithGenie(view, WindowEnumerator.TopMost(windows));
            return;
        }
        if (!_launches.IsLaunching(view) && Launch(view.Item))
            _launches.Begin(view);
    }

    private void Pin(DockItem item)
    {
        if (IsAlreadyPinned(item.Path)) return;
        _config.PinnedApps.Add(new PinnedApp { Path = item.Path });
        SaveAndRebuild();
    }

    private void Unpin(DockItem item)
    {
        if (item.Pinned == null) return;
        _config.PinnedApps.Remove(item.Pinned);
        SaveAndRebuild(); // if it's running, it reappears after the separator
    }

    private void SaveAndRebuild()
    {
        ConfigService.Save(_config);
        BuildPinnedViews();
        RefreshPanel();
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Show in Explorer failed: {ex.Message}");
        }
    }

    /// <summary>Sends WM_CLOSE to every window of the app (apps may still ask to save).</summary>
    private void Quit(DockItem item)
    {
        foreach (var hwnd in _running.GetWindows(item.TargetPath).ToList())
            WindowActivator.Close(hwnd);
    }
}
