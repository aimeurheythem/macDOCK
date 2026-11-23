using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MacDock.Controls;
using MacDock.Models;
using MacDock.Services;

namespace MacDock;

/// <summary>Trash at the end of the dock: open, empty, drop files to recycle them.</summary>
public partial class MainWindow
{
    private static readonly TimeSpan TrashPollInterval = TimeSpan.FromSeconds(8);

    private DockIconView? _trashView;
    private readonly Border _trashSeparator = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
        IsHitTestVisible = false,
    };
    private DispatcherTimer? _trashTimer;
    private bool _trashFull;
    private bool _trashChecking;

    private DockIconView GetTrashView()
    {
        return _trashView ??= CreateView(new DockItem
        {
            Name = "Trash",
            Path = "",
            Kind = DockItemKind.Trash,
            Icon = TrashIcon.Empty,
        });
    }

    /// <summary>Polls the Recycle Bin (off the UI thread) so the icon shows empty/full.</summary>
    private void InitTrash()
    {
        _trashTimer = new DispatcherTimer { Interval = TrashPollInterval };
        _trashTimer.Tick += (_, _) => { if (!GameMode.Active) UpdateTrashIcon(); };
        _trashTimer.Start();
        UpdateTrashIcon();
    }

    private async void UpdateTrashIcon()
    {
        if (_trashChecking || !_config.ShowTrash) return;
        _trashChecking = true;
        try
        {
            bool full = await Task.Run(() => RecycleBin.ItemCount() > 0);
            if (full == _trashFull && _trashView != null) return;
            _trashFull = full;
            GetTrashView().SetIcon(full ? TrashIcon.Full : TrashIcon.Empty);
        }
        finally
        {
            _trashChecking = false;
        }
    }

    private ContextMenu BuildTrashMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open", RecycleBin.Open));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Empty Trash…", () =>
        {
            RecycleBin.Empty(_hwnd); // Windows asks for confirmation
            UpdateTrashIcon();
        }, enabled: _trashFull));
        return menu;
    }

    // ---------------- Drag & drop onto the trash ----------------

    private static string[] GetDroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray()
            : Array.Empty<string>();

    private bool IsOverTrash(DragEventArgs e)
    {
        if (_trashView == null || !_config.ShowTrash || !Dock.Children.Contains(_trashView)) return false;
        var p = e.GetPosition(_trashView);
        return p.X >= -6 && p.X <= _trashView.ActualWidth + 6 && p.Y >= -24 && p.Y <= _trashView.ActualHeight + 8;
    }

    /// <summary>Returns true when the drop was handled by the trash.</summary>
    private bool TryDropOnTrash(DragEventArgs e)
    {
        if (!IsOverTrash(e)) return false;
        var files = GetDroppedFiles(e);
        if (files.Length > 0)
        {
            RecycleBin.MoveToBin(_hwnd, files);
            UpdateTrashIcon();
        }
        return true;
    }
}
