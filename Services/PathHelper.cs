using System.IO;

namespace MacDock.Services;

internal static class PathHelper
{
    /// <summary>Case-insensitive comparer for file system paths.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Full, normalized path; returns the input unchanged if it is not a valid path.</summary>
    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch { return path; }
    }

    public static bool AreSame(string? a, string? b) =>
        a != null && b != null && Comparer.Equals(Normalize(a), Normalize(b));
}
