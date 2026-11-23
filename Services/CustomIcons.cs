using System.IO;
using System.Windows.Media.Imaging;
using MacDock.Config;

namespace MacDock.Services;

/// <summary>
/// User-supplied icons: %APPDATA%\MacDock\Icons\&lt;name&gt;.png (or .ico/.jpg).
/// The file name can be the app's display name ("Google Chrome") or its exe name ("chrome").
/// Big images (e.g. 1024x1024) are scaled down with high quality to the exact display size.
/// </summary>
internal static class CustomIcons
{
    private static readonly string[] Extensions = { ".png", ".ico", ".jpg", ".jpeg" };

    public static string Folder { get; } = Path.Combine(ConfigService.ConfigDirectory, "Icons");

    /// <summary>Creates the folder with a short how-to the first time.</summary>
    public static void EnsureFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var readme = Path.Combine(Folder, "README.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme,
                    "MacDock custom icons\r\n" +
                    "====================\r\n\r\n" +
                    "Put an image here to replace an app's icon in the dock and the Start menu.\r\n\r\n" +
                    "Name the file after the app, either:\r\n" +
                    "  - its name as shown in the Start menu:  Google Chrome.png\r\n" +
                    "  - or its program file name:             chrome.png\r\n\r\n" +
                    "Formats: .png (best, with transparency), .ico, .jpg\r\n" +
                    "Size: square, 512x512 or 1024x1024 for the sharpest result.\r\n\r\n" +
                    "Changes show the next time the Start menu reloads its app list (within 2 minutes)\r\n" +
                    "or when MacDock restarts.\r\n");
        }
        catch
        {
            // Folder is optional.
        }
    }

    /// <summary>Loads the custom icon for an app, or null if the user has none for it.</summary>
    public static BitmapSource? TryLoad(string? name, string? exePath)
    {
        foreach (var key in Keys(name, exePath))
        {
            foreach (var ext in Extensions)
            {
                var file = Path.Combine(Folder, key + ext);
                if (!File.Exists(file)) continue;
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(file);
                    image.DecodePixelWidth = 512; // plenty for any display size, light on memory
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch
                {
                    // Unreadable image: try the next candidate.
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> Keys(string? name, string? exePath)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var clean = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
            if (clean.Length > 0) yield return clean;
        }
        if (!string.IsNullOrWhiteSpace(exePath))
            yield return Path.GetFileNameWithoutExtension(exePath);
    }
}
