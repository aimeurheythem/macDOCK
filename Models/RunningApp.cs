namespace MacDock.Models;

/// <summary>A running application: its executable and its taskbar-eligible top-level windows.</summary>
public sealed class RunningApp
{
    public required string ExePath { get; init; }

    /// <summary>Windows in stable order (first seen first), used for cycling.</summary>
    public required IReadOnlyList<IntPtr> Windows { get; init; }
}
