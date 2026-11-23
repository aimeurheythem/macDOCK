using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MacDock.Config;

/// <summary>Loads and saves <see cref="DockConfig"/> as JSON in %APPDATA%\MacDock.</summary>
public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock");

    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, "config.json");

    /// <summary>Reads the config; creates it with defaults if missing. A corrupt file is backed up, then replaced.</summary>
    public static DockConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<DockConfig>(json, JsonOptions);
                if (config != null)
                {
                    config.PinnedApps ??= new();
                    config.PinnedApps.RemoveAll(p => string.IsNullOrWhiteSpace(p?.Path));
                    config.Normalize();
                    return config;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Invalid config, backing up: {ex.Message}");
                TryBackupCorruptFile();
            }
        }

        var defaults = DockConfig.CreateDefault();
        Save(defaults);
        return defaults;
    }

    /// <summary>Writes the config atomically (temp file + move) so a crash never leaves a half-written file.</summary>
    public static void Save(DockConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not save config: {ex.Message}");
        }
    }

    private static void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not back up config: {ex.Message}");
        }
    }
}
