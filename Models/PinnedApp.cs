namespace MacDock.Models;

/// <summary>One pinned entry as stored in config.json (dock or Start menu).</summary>
public sealed class PinnedApp
{
    /// <summary>
    /// Path to an .exe or .lnk (environment variables allowed), or a shell path such as
    /// "shell:AppsFolder\{AppID}" for apps pinned from the Start menu.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>Display name for shell paths (files use their own name).</summary>
    public string? Name { get; set; }

    /// <summary>Executable behind a shell path, used to show the running dot.</summary>
    public string? Target { get; set; }

    /// <summary>Path with environment variables expanded.</summary>
    public string ExpandedPath() => Environment.ExpandEnvironmentVariables(Path);
}
