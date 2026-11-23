using System.Windows.Media;

namespace MacDock.Models;

/// <summary>An installed app as listed in the Start menu (desktop or Store app).</summary>
public sealed class StartApp
{
    public required string Name { get; init; }

    /// <summary>"shell:AppsFolder\{AppID}": launches exactly like the Windows Start menu does.</summary>
    public required string LaunchPath { get; init; }

    /// <summary>Executable for desktop apps (used to match running windows); null for Store apps.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Grid-size icon, rendered at its exact on-screen pixel size.</summary>
    public ImageSource? Icon { get; init; }

    /// <summary>List-size icon, also pre-rendered at exact size (no runtime scaling = no blur).</summary>
    public ImageSource? SmallIcon { get; init; }

    /// <summary>Letter group for the "All apps" list.</summary>
    public string Group
    {
        get
        {
            char c = Name.Length > 0 ? char.ToUpperInvariant(Name[0]) : '#';
            return char.IsLetter(c) ? c.ToString() : "#";
        }
    }
}
