using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using MacDock.Models;
using MacDock.Native;

namespace MacDock.Services;

/// <summary>
/// All installed apps (desktop + Store), read from shell:AppsFolder, the same source the Windows
/// "All apps" list uses. Uninstallers, readme/help links and web links are filtered out.
/// Loading runs on a background STA thread; icons are frozen so the UI thread can use them.
/// </summary>
internal sealed class AppCatalog
{
    /// <summary>Request the biggest icon the app has (sharp), then store a normalized 96px copy (light on memory).</summary>
    private const int SourcePixels = 256;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private static readonly string[] JunkWords =
    {
        "uninstall", "readme", "read me", "help", "release notes", "website", "documentation",
        "license", "manual", "what's new", "changelog", "faq", "support", "online",
    };

    private static readonly string[] DocumentExtensions =
        { ".url", ".htm", ".html", ".pdf", ".txt", ".chm", ".rtf", ".md", ".doc", ".docx", ".xml" };

    private Task? _loading;

    public IReadOnlyList<StartApp> Apps { get; private set; } = Array.Empty<StartApp>();

    /// <summary>
    /// Icon size in device pixels. Set to the exact on-screen size (tile DIPs x DPI scale) so icons
    /// are drawn 1:1 with no runtime scaling: perfectly sharp.
    /// </summary>
    public int IconPixels { get; set; } = 44;

    /// <summary>Exact pixel size of the small list icons.</summary>
    public int SmallIconPixels { get; set; } = 24;
    public DateTime LoadedAt { get; private set; } = DateTime.MinValue;
    public bool IsLoaded => LoadedAt != DateTime.MinValue;

    /// <summary>Raised on the calling (UI) thread after each successful load.</summary>
    public event Action? Loaded;

    public Task RefreshIfStaleAsync() =>
        DateTime.UtcNow - LoadedAt > StaleAfter ? RefreshAsync() : Task.CompletedTask;

    public Task RefreshAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            int px = IconPixels, smallPx = SmallIconPixels;
            var apps = await RunOnStaThread(() => Enumerate(px, smallPx));
            Apps = apps;
            LoadedAt = DateTime.UtcNow;
            Loaded?.Invoke();
            // Building ~100 icons leaves many temporary render bitmaps in unmanaged memory that the GC
            // doesn't feel; collect once now instead of carrying hundreds of MB.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch (Exception ex)
        {
            CrashLog.Write("AppCatalog", ex);
        }
        finally
        {
            _loading = null;
        }
    }

    public StartApp? Find(string launchPath) =>
        Apps.FirstOrDefault(a => string.Equals(a.LaunchPath, launchPath, StringComparison.OrdinalIgnoreCase));

    public StartApp? FindByName(string name) =>
        Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Name starts with query first, then a word starts with it, then contains it.</summary>
    public List<StartApp> Search(string query, int max = 40)
    {
        var q = query.Trim();
        if (q.Length == 0) return new List<StartApp>();

        return Apps
            .Select(a => (App: a, Score: Score(a.Name, q)))
            .Where(x => x.Score >= 0)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.App)
            .ToList();
    }

    private static int Score(string name, string q)
    {
        if (name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 0;
        if (name.Split(' ', '-', '.', '_').Any(w => w.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))) return 1;
        if (name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return 2;
        return -1;
    }

    // ---------------- Enumeration (STA thread) ----------------

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "MacDock app catalog", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static List<StartApp> Enumerate(int iconPx, int smallPx)
    {
        var result = new List<StartApp>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Guid folderId = ShellInterop.FOLDERID_AppsFolder, iidItem = ShellInterop.IID_IShellItem;
        if (ShellInterop.SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidItem, out var folder) != 0 || folder == null)
            return result;

        ShellInterop.IEnumShellItems? enumerator = null;
        try
        {
            Guid bhid = ShellInterop.BHID_EnumItems, iidEnum = ShellInterop.IID_IEnumShellItems;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var pEnum) != 0 || pEnum == IntPtr.Zero)
                return result;
            enumerator = (ShellInterop.IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);

            while (enumerator.Next(1, out var item, out uint fetched) == 0 && fetched == 1 && item != null)
            {
                try
                {
                    if (TryCreate(item, iconPx, smallPx) is { } app && seenNames.Add(app.Name))
                        result.Add(app);
                }
                catch
                {
                    // Skip entries the shell can't describe.
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            Marshal.ReleaseComObject(folder);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private static StartApp? TryCreate(ShellInterop.IShellItem item, int iconPx, int smallPx)
    {
        item.GetDisplayName(ShellInterop.SIGDN.NormalDisplay, out var pName);
        item.GetDisplayName(ShellInterop.SIGDN.ParentRelativeParsing, out var pId);
        var name = ShellInterop.TakeString(pName)?.Trim();
        var id = ShellInterop.TakeString(pId);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || IsJunkName(name)) return null;

        string? target = null;
        if (item is ShellInterop.IShellItem2 item2)
        {
            var key = ShellInterop.PKEY_Link_TargetParsingPath;
            if (item2.GetString(ref key, out var pTarget) == 0)
                target = ShellInterop.TakeString(pTarget);
        }

        // Web links and documents are not apps.
        if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        if (target != null)
        {
            target = Environment.ExpandEnvironmentVariables(target);
            var ext = Path.GetExtension(target);
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                DocumentExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return null;
            if (!string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
                target = null;
        }

        // Best source first: package logo (Store/MSIX apps), then the exe's full-size icon (same as
        // the dock), then the shell's rendering as a last resort.
        // A 128px master, then both display sizes rendered from it at their exact pixel size.
        // A 128px master whose content fills the whole square, then the grid tile and the list
        // icon are each rendered from it at their exact on-screen pixel size.
        const int masterPx = 128;
        BitmapSource? raw = null;
        if (PackagedAppIcon.IsPackagedAppId(id))
            raw = PackagedAppIcon.TryLoad(id, masterPx);
        if (raw == null && target != null && File.Exists(target) && IconService.GetIcon(target) is BitmapSource exeIcon)
            raw = exeIcon;
        if (raw == null && item is Win32.IShellItemImageFactory factory && IconService.FromFactory(factory, SourcePixels) is { } big)
            raw = big;
        var master = raw == null ? null : IconService.NormalizeSquare(raw, masterPx, 1.0);

        var icon = master == null ? null : IconStyler.MakeTile(master, iconPx);
        var smallIcon = master == null ? null : IconStyler.Centered(master, smallPx);

        // Built-in apps with dated icons get clean vector icons, drawn at the exact pixel size.
        if (SystemIcons.TryRender(name, target, id, iconPx) is { } systemIcon)
        {
            icon = systemIcon;
            smallIcon = SystemIcons.TryRender(name, target, id, smallPx);
        }

        // The user's own icon (Icons folder) wins over everything, shown exactly as designed.
        if (CustomIcons.TryLoad(name, target) is { } custom)
        {
            var customMaster = IconService.NormalizeSquare(custom, 256, 1.0);
            icon = IconStyler.Centered(customMaster, iconPx, 1.0);
            smallIcon = IconStyler.Centered(customMaster, smallPx, 1.0);
        }

        return new StartApp
        {
            Name = name,
            LaunchPath = @"shell:AppsFolder\" + id,
            TargetPath = target,
            Icon = icon,
            SmallIcon = smallIcon,
        };
    }

    private static bool IsJunkName(string name)
    {
        var lower = name.ToLowerInvariant();
        return JunkWords.Any(w => lower.Contains(w));
    }
}
