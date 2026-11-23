using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MacDock.Services;

/// <summary>
/// VS Code and the editors built on it (Antigravity, Cursor, Windsurf...) can use Windows' own title bar
/// instead of drawing their own ("window.titleBarStyle": "native"). With it they get the full macOS
/// title bar like every classic app (lights on the left, centered title). This switches that setting on
/// in each editor's settings.json and remembers the previous value, so turning MacDock's title bars off
/// (or uninstalling) puts it back. The editors pick the change up on their next start.
/// </summary>
internal static class EditorTitleBars
{
    /// <summary>Windows' own title bar (so they get the full macOS one), and their menu row hidden (the menus are in the top bar).</summary>
    private static readonly (string Key, string Value)[] Settings = { ("window.titleBarStyle", "native"), ("window.menuBarVisibility", "toggle") };
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "editor-title-bars.json");

    // "settings.json path|key" -> previous value of the key (null = it was not set).
    private sealed class State : Dictionary<string, string?> { }

    /// <summary>Editors of the VS Code family: a User\settings.json next to a User\globalStorage folder.</summary>
    private static IEnumerable<string> SettingsFiles()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(roaming).ToList(); }
        catch { yield break; }
        foreach (var dir in dirs)
        {
            string user = Path.Combine(dir, "User");
            string file = Path.Combine(user, "settings.json");
            if (File.Exists(file) && Directory.Exists(Path.Combine(user, "globalStorage"))) yield return file;
        }
    }

    public static void Apply()
    {
        try
        {
            var state = Load();
            foreach (var file in SettingsFiles())
            {
                string text = File.ReadAllText(file);
                bool changed = false;
                foreach (var (key, value) in Settings)
                {
                    var match = Find(text, key);
                    if (match is { } m && m.Groups["value"].Value == value) continue; // already set
                    string id = file + "|" + key;
                    if (!state.ContainsKey(id)) state[id] = match?.Groups["value"].Value;
                    text = Set(text, match, key, value);
                    changed = true;
                }
                if (changed) File.WriteAllText(file, text);
            }
            Save(state);
        }
        catch (Exception ex)
        {
            CrashLog.Write("EditorTitleBars.Apply", ex);
        }
    }

    public static void Restore()
    {
        try
        {
            var state = Load();
            foreach (var (id, previous) in state)
            {
                int bar = id.LastIndexOf('|');
                string file = bar > 0 ? id[..bar] : id;
                string key = bar > 0 ? id[(bar + 1)..] : "window.titleBarStyle"; // older state files: the title bar only
                string ours = Settings.FirstOrDefault(s => s.Key == key).Value ?? "native";
                if (!File.Exists(file)) continue;
                string text = File.ReadAllText(file);
                var match = Find(text, key);
                if (match == null || match.Groups["value"].Value != ours) continue; // changed by the user since
                File.WriteAllText(file, previous == null ? Remove(text, match) : Set(text, match, key, previous));
            }
            if (File.Exists(StateFile)) File.Delete(StateFile);
        }
        catch (Exception ex)
        {
            CrashLog.Write("EditorTitleBars.Restore", ex);
        }
    }

    // ---------------- settings.json text (JSON with comments: edited as text, never reformatted) ----------------



    private static Match? Find(string text, string key)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<value>[^\"]*)\"");
        return m.Success ? m : null;
    }

    private static string Set(string text, Match? match, string key, string value)
    {
        if (match != null)
        {
            var group = match.Groups["value"];
            return text[..group.Index] + value + text[(group.Index + group.Length)..];
        }
        int brace = text.IndexOf('{');
        if (brace < 0) return "{\n    \"" + key + "\": \"" + value + "\"\n}\n";
        // First entry of the object: keeps the rest of the file exactly as it was.
        bool empty = text[(brace + 1)..].Trim().StartsWith('}');
        string entry = "\n    \"" + key + "\": \"" + value + "\"" + (empty ? "\n" : ",");
        return text[..(brace + 1)] + entry + text[(brace + 1)..];
    }

    private static string Remove(string text, Match match)
    {
        int start = match.Index, end = match.Index + match.Length;
        // Take the trailing comma (or a leading one when it was the last entry) and the line break with it.
        int after = end;
        while (after < text.Length && text[after] is ' ' or '\t') after++;
        if (after < text.Length && text[after] == ',') end = after + 1;
        else
        {
            int before = start - 1;
            while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
            if (before >= 0 && text[before] == ',') start = before;
        }
        int lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t') lineStart--;
        if (lineStart > 0 && text[lineStart - 1] == '\n') start = lineStart - 1;
        return text[..start] + text[end..];
    }

    private static State Load()
    {
        try
        {
            if (File.Exists(StateFile)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new State();
        }
        catch { /* start over */ }
        return new State();
    }

    private static void Save(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        if (state.Count == 0) { if (File.Exists(StateFile)) File.Delete(StateFile); return; }
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
