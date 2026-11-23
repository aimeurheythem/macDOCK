using System.IO;
using System.IO.Compression;

namespace MacDock.Finder;

/// <summary>
/// Zip files the macOS way (Archive Utility): opening a .zip extracts it next to itself, and
/// "Compress" makes "Name.zip" (one item) or "Archive.zip" (several).
/// </summary>
internal static class Archive
{
    public static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>
    /// Extracts next to the archive. One top-level folder or file comes out as itself; several items
    /// go into a folder named after the archive. Returns the new item (to select it).
    /// </summary>
    public static Task<string> ExtractAsync(string zipPath) => Task.Run(() =>
    {
        var parent = Path.GetDirectoryName(zipPath)!;
        using var zip = ZipFile.OpenRead(zipPath);
        var tops = zip.Entries
            .Select(e => e.FullName.Replace('\\', '/').TrimStart('/'))
            .Where(n => n.Length > 0 && !n.StartsWith("__MACOSX/", StringComparison.Ordinal))
            .Select(n => n.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string target, stripPrefix;
        if (tops.Count == 1)
        {
            // "Project.zip" holding "Project/...": just "Project", not "Project/Project".
            bool isFolder = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/').TrimStart('/'))
                .Any(n => !n.StartsWith("__MACOSX/", StringComparison.Ordinal) && n.Contains('/'));
            target = Unique(Path.Combine(parent, tops[0]), isFolder);
            stripPrefix = tops[0] + "/";
            if (!isFolder)
            {
                var entry = zip.Entries.First(e => e.Name.Length > 0 && !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal));
                entry.ExtractToFile(target);
                File.SetLastWriteTime(target, entry.LastWriteTime.DateTime);
                return target;
            }
        }
        else
        {
            target = Unique(Path.Combine(parent, Path.GetFileNameWithoutExtension(zipPath)), true);
            stripPrefix = "";
        }

        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || name.EndsWith("/.DS_Store", StringComparison.Ordinal) || name == ".DS_Store") continue;
            if (stripPrefix.Length > 0)
            {
                if (!name.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                name = name[stripPrefix.Length..];
            }
            if (name.Length == 0) continue;
            var dest = Path.GetFullPath(Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // "../" tricks stay out
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            try { File.SetLastWriteTime(dest, entry.LastWriteTime.DateTime); } catch { }
        }
        return target;
    });

    /// <summary>Compresses the items into one zip in their folder. Returns the new archive.</summary>
    public static Task<string> CompressAsync(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        var parent = Path.GetDirectoryName(paths[0].TrimEnd('\\'))!;
        string baseName = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : "Archive";
        string zipPath = Path.Combine(parent, baseName + ".zip");
        for (int i = 2; File.Exists(zipPath) || Directory.Exists(zipPath); i++)
            zipPath = Path.Combine(parent, $"{baseName} {i}.zip");

        using var stream = new FileStream(zipPath, FileMode.CreateNew);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var name = Path.GetFileName(path.TrimEnd('\\'));
                var files = Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories);
                if (files.Length == 0) zip.CreateEntry(name + "/");
                foreach (var item in files)
                {
                    var relative = name + "/" + Path.GetRelativePath(path, item).Replace('\\', '/');
                    if (Directory.Exists(item))
                    {
                        if (Directory.GetFileSystemEntries(item).Length == 0) zip.CreateEntry(relative + "/");
                    }
                    else
                    {
                        zip.CreateEntryFromFile(item, relative, CompressionLevel.Optimal);
                    }
                }
            }
            else if (File.Exists(path))
            {
                zip.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
            }
        }
        return zipPath;
    });

    private static string Unique(string path, bool folder)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = folder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = folder ? "" : Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} {i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
