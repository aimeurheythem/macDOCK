using System.IO;
using System.Text.Json;
using MacDock.Config;

namespace MacDock.Finder;

/// <summary>Finder window preferences (view, sorting, sizes), kept in %APPDATA%\MacDock\finder.json.</summary>
internal sealed class FinderSettings
{
    public string View { get; set; } = "Icons";
    public string Sort { get; set; } = "Name";
    public bool Ascending { get; set; } = true;
    public bool ShowHidden { get; set; }
    public bool ShowPathBar { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public double Width { get; set; } = 980;
    public double Height { get; set; } = 600;
    public double SidebarWidth { get; set; } = 200;

    private static string FilePath => Path.Combine(ConfigService.ConfigDirectory, "finder.json");

    public static FinderSettings Current { get; } = Load();

    private static FinderSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<FinderSettings>(File.ReadAllText(FilePath)) ?? new FinderSettings();
        }
        catch
        {
            // Corrupt file: start with defaults.
        }
        return new FinderSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigService.ConfigDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Preferences are a convenience; never fail because of them.
        }
    }
}
