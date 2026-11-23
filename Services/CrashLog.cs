using System.IO;
using MacDock.Config;

namespace MacDock.Services;

/// <summary>Appends unhandled exceptions to %APPDATA%\MacDock\crash.log.</summary>
internal static class CrashLog
{
    public static string LogPath => Path.Combine(ConfigService.ConfigDirectory, "crash.log");

    public static void Write(string source, object? exception)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.ConfigDirectory);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash handler.
        }
    }
}
