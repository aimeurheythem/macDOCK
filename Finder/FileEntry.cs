using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MacDock.Finder;

/// <summary>One file or folder shown in a Finder window.</summary>
internal sealed class FileEntry : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isRenaming;
    private string _editName = "";
    private bool _isCut;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsHidden { get; init; }
    public DateTime Modified { get; init; }

    /// <summary>Bytes; -1 for folders.</summary>
    public long Size { get; init; } = -1;

    public string Kind { get; init; } = "";

    public string Extension => IsDirectory ? "" : Path.GetExtension(FullPath);

    public string DateText => FormatDate(Modified);
    public string SizeText => IsDirectory ? "--" : FormatSize(Size);

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnChanged(); }
    }

    /// <summary>The name is being edited in place.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set { _isRenaming = value; OnChanged(); }
    }

    public string EditName
    {
        get => _editName;
        set { _editName = value; OnChanged(); }
    }

    /// <summary>Cut to the clipboard: drawn faded until pasted.</summary>
    public bool IsCut
    {
        get => _isCut;
        set { _isCut = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static FileEntry FromInfo(FileSystemInfo info)
    {
        bool dir = info is DirectoryInfo;
        long size = -1;
        if (info is FileInfo f)
        {
            try { size = f.Length; } catch { size = 0; }
        }
        FileAttributes attributes = 0;
        try { attributes = info.Attributes; } catch { }
        DateTime modified = DateTime.MinValue;
        try { modified = info.LastWriteTime; } catch { }

        return new FileEntry
        {
            Name = info.Name,
            FullPath = info.FullName,
            IsDirectory = dir,
            IsHidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || info.Name.StartsWith('.'),
            Modified = modified,
            Size = size,
            Kind = dir ? "Folder" : FinderAssets.TypeName(info.Name),
        };
    }

    /// <summary>"Today at 9:41 AM", "Yesterday at 8:02 PM", or "Nov 3, 2026 at 10:15 AM", like macOS.</summary>
    public static string FormatDate(DateTime time)
    {
        if (time == DateTime.MinValue) return "--";
        var today = DateTime.Today;
        string clock = time.ToString("h:mm tt");
        if (time.Date == today) return $"Today at {clock}";
        if (time.Date == today.AddDays(-1)) return $"Yesterday at {clock}";
        return $"{time:MMM d, yyyy} at {clock}";
    }

    /// <summary>Decimal units, like macOS: "12 KB", "3.4 MB", "1.21 GB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "--";
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = -1;
        do
        {
            value /= 1000;
            unit++;
        } while (value >= 1000 && unit < units.Length - 1);
        return unit == 0 ? $"{Math.Round(value)} KB" : $"{value:0.##} {units[unit]}";
    }
}
