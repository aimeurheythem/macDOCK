using System.IO;

namespace MacDock.Services;

/// <summary>A file or folder found by the Windows search index.</summary>
internal sealed record FileHit(string Path, string Name, bool IsFolder);

/// <summary>
/// Files and folders by name, from the Windows Search index (the same index File Explorer uses), so
/// results come back in milliseconds without scanning the disk. Queried through ADO, which ships with
/// Windows. Call off the UI thread.
/// </summary>
internal static class FileSearch
{
    private const string Connection = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public static List<FileHit> Search(string query, int max, CancellationToken cancel)
    {
        var hits = new List<FileHit>();
        query = query.Trim();
        if (query.Length < 2) return hits;

        // Words of the query must each start a word of the name ("proj rep" finds "Project report.docx").
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", "").Replace("'", "''"))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}*\"");
        string contains = string.Join(" AND ", terms);
        // System.ItemUrl is the real path ("file:C:/Users/..."); ItemPathDisplay is translated
        // ("C:\Utilisateurs\..." on a French PC) and can't be opened.
        string sql = $"SELECT TOP {max * 2} System.ItemUrl, System.ItemNameDisplay FROM SYSTEMINDEX " +
                     $"WHERE scope='file:' AND CONTAINS(System.ItemNameDisplay, '{contains}') " +
                     "ORDER BY System.Search.Rank DESC";

        dynamic? connection = null;
        try
        {
            var type = Type.GetTypeFromProgID("ADODB.Connection");
            if (type == null) return hits;
            connection = Activator.CreateInstance(type)!;
            connection.Open(Connection);
            dynamic records = connection.Execute(sql);
            while (!(bool)records.EOF && !cancel.IsCancellationRequested)
            {
                string? url = records.Fields.Item(0).Value as string;
                string? name = records.Fields.Item(1).Value as string;
                string? path = url != null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    ? Uri.UnescapeDataString(url[5..]).Replace('/', '\\') : null;
                if (hits.Count < max && !string.IsNullOrEmpty(path) && !IsNoise(path))
                    hits.Add(new FileHit(path, string.IsNullOrEmpty(name) ? System.IO.Path.GetFileName(path) : name, Directory.Exists(path)));
                records.MoveNext();
            }
            records.Close();
        }
        catch
        {
            // Windows Search service off or the query was rejected: no file results.
        }
        finally
        {
            try { connection?.Close(); } catch { }
        }
        return hits;
    }

    /// <summary>Skip app internals nobody searches for (caches, app data, temp files).</summary>
    private static bool IsNoise(string path) =>
        path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\.", StringComparison.Ordinal) ||               // hidden tool folders (.cache, .vscode...)
        path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) || // build output
        path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the search connection once in the background so the first real search is fast.</summary>
    public static void WarmUp() => Task.Run(() => Search("warmup", 1, CancellationToken.None));
}
