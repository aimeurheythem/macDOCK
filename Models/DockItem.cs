using System.Windows.Media;

namespace MacDock.Models;

public enum DockItemKind
{
    App,
    StartButton,
    Trash,
}

/// <summary>One icon in the dock.</summary>
public sealed class DockItem
{
    public required string Name { get; init; }

    /// <summary>Path that is launched: an .exe, a .lnk, or a "shell:" path.</summary>
    public required string Path { get; init; }

    /// <summary>Real executable behind <see cref="Path"/> (same as Path for .exe). Used to match running windows.</summary>
    public string? TargetPath { get; init; }

    public ImageSource? Icon { get; init; }

    /// <summary>Config entry when pinned, null for unpinned running apps.</summary>
    public PinnedApp? Pinned { get; init; }

    public DockItemKind Kind { get; init; } = DockItemKind.App;

    public bool IsPinned => Pinned != null;
}
