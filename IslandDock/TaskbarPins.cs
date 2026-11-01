using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.Win32;

namespace IslandDock;

/// <summary>
/// A taskbar pin whose app isn't open, shown as a bare icon in the tab strip. It has the tab template's bindable
/// shape; clicking it starts the app, and the window it opens is docked as a tab in the pin's place.
/// </summary>
internal sealed class PinEntry : System.ComponentModel.INotifyPropertyChanged
{
    bool loading;

    /// <summary>Set while the app is starting, until its window is docked; the pin pulses meanwhile.</summary>
    public bool IsLoading
    {
        get => loading;
        set { if (loading != value) { loading = value; PropertyChanged?.Invoke(this, new(nameof(IsLoading))); } }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What to start: the pin's .lnk, or shell:AppsFolder\{AUMID} for a Store app.</summary>
    public string Launch { get; }
    public string? Arguments { get; init; }
    /// <summary>
    /// For something opened from search, whose window can't be told apart by app (a file opens in whatever app owns
    /// its type): the first new window to appear while it's starting is taken as its own.
    /// </summary>
    public bool AnyNewWindow { get; init; }
    public string? AppId { get; }
    public string? Target { get; }
    public string Title { get; }
    public ImageSource? Icon { get; }
    public bool IsPin => true;
    public bool IsDocked => false;
    public bool IsActive => false;
    public double Level => 0;
    public bool Playing => false;

    public PinEntry(string launch, string? appId, string? target, string title)
    {
        Launch = launch;
        AppId = appId;
        Target = target;
        Title = title;
        Icon = ShellIcon.ForItem(launch);
        // A Store AUMID is {Name}_{PublisherId}!{App}.
        if (launch.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) && appId?.Split('!')[0] is string family
            && family.LastIndexOf('_') is int cut and > 0)
            StorePackage = (family[..cut], family[(cut + 1)..]);
    }

    (string Name, string Publisher)? StorePackage { get; }

    /// <summary>
    /// Whether a window belongs to this pin, the way the taskbar groups them: by AppUserModelID when both have one,
    /// otherwise by exe. The exe may be a helper beside or below the pinned one (Steam's window is steamwebhelper in
    /// Steam\bin), or a newer copy elsewhere (Roblox updates into a new version folder, Epic has Win32 and Win64).
    /// </summary>
    /// <remarks>
    /// The taskbar keeps a window whose AUMID differs from the pin's apart from it; with <paramref name="loose"/> it's
    /// matched by exe anyway, so an app's stray windows merge into its pin. Callers prefer an exact owner first.
    /// </remarks>
    public bool Owns(string? windowAppId, string? windowPath, bool loose = true)
    {
        // A Store app's exe sits in its package folder, WindowsApps\{Name}_{Version}_{Arch}__{PublisherId}; its windows
        // may carry their own AUMID (Electron apps like Claude do), so the folder is the surer match.
        if (StorePackage is var (name, publisher) && windowPath != null)
        {
            int at = windowPath.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                string folder = windowPath[(at + @"\WindowsApps\".Length)..].Split('\\')[0];
                if (folder.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)
                    && folder.EndsWith("__" + publisher, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        // Windows of a Store app can carry a different app of the same package (Xbox's window is ...!Microsoft.Xbox.AppL,
        // hosted by ApplicationFrameHost, while its pin is ...!Microsoft.Xbox.App). The taskbar splits those; we group by package.
        if (AppId?.Split('!') is [var pinFamily, _] && windowAppId?.Split('!') is [var windowFamily, _]
            && pinFamily.Equals(windowFamily, StringComparison.OrdinalIgnoreCase)) return true;
        if (AppId != null && windowAppId != null)
        {
            if (AppId.Equals(windowAppId, StringComparison.OrdinalIgnoreCase)) return true;
            // A Windows host exe (ApplicationFrameHost, explorer) runs many apps, so it says nothing about which.
            if (!loose || windowPath == null
                || windowPath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (Target == null || windowPath == null) return false;
        if (windowPath.Equals(Target, StringComparison.OrdinalIgnoreCase)) return true;
        if (Path.GetFileName(windowPath).Equals(Path.GetFileName(Target), StringComparison.OrdinalIgnoreCase)) return true;
        string dir = Path.GetDirectoryName(Target) ?? "";
        // Not for an exe straight in a broad folder like Program Files, where everything would match.
        return dir.Count(c => c == Path.DirectorySeparatorChar) >= 2
            && windowPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The taskbar's pins in order. Shortcuts live in User Pinned\TaskBar (or ImplicitAppShortcuts, for apps like Steam
/// that pin themselves); Store apps have no file at all. The order of both is only in the Taskband "Favorites" blob,
/// which names each pin's .lnk file or AUMID as UTF-16 text, so reading those names in order gives the pin order.
/// </summary>
internal static class TaskbarPins
{
    static readonly string PinnedRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned");
    static readonly Regex Names = new(@"[\x20-\x7E]{4,}");
    static readonly Regex StoreAppId = new(@"^[\w.\-]+_[a-z0-9]{13}![\w.\-]+$");

    static List<PinEntry> pins = new();
    static DateTime readAt = DateTime.MinValue;

    public static IReadOnlyList<PinEntry> Current()
    {
        if (DateTime.UtcNow - readAt < TimeSpan.FromSeconds(10)) return pins;
        readAt = DateTime.UtcNow;
        try
        {
            var old = pins.ToDictionary(p => p.Launch, StringComparer.OrdinalIgnoreCase);
            // Keep the entries already shown so their tabs don't flicker or reload icons.
            pins = Read().Select(launch => old.TryGetValue(launch, out var same) ? same : Create(launch)).OfType<PinEntry>().ToList();
        }
        catch { }
        return pins;
    }

    /// <summary>
    /// Pins an app in the island. Windows lets no app pin to its own taskbar, so the island keeps its pins on top of
    /// the taskbar's: a taskbar pin unpinned here comes back, anything else goes after the taskbar's pins.
    /// </summary>
    public static void Pin(string launch)
    {
        var s = Islands.Settings;
        if (s.HiddenPins.RemoveAll(p => p.Equals(launch, StringComparison.OrdinalIgnoreCase)) == 0
            && !s.ExtraPins.Contains(launch, StringComparer.OrdinalIgnoreCase))
            s.ExtraPins.Add(launch);
        s.Save();
        readAt = DateTime.MinValue;
    }

    public static void Unpin(PinEntry pin)
    {
        var s = Islands.Settings;
        if (s.ExtraPins.RemoveAll(p => p.Equals(pin.Launch, StringComparison.OrdinalIgnoreCase)) == 0
            && !s.HiddenPins.Contains(pin.Launch, StringComparer.OrdinalIgnoreCase))
            s.HiddenPins.Add(pin.Launch);
        s.Save();
        readAt = DateTime.MinValue;
    }

    /// <summary>
    /// Pins a window's app: back to its taskbar pin if that was unpinned here, otherwise its Store app when it has
    /// one, or its exe.
    /// </summary>
    public static void PinWindow(string? appId, string? path)
    {
        foreach (var hidden in Islands.Settings.HiddenPins.ToList())
            if (Create(hidden) is PinEntry p && p.Owns(appId, path, loose: false)) { Pin(hidden); return; }
        if ((appId != null && StoreAppId.IsMatch(appId) ? @"shell:AppsFolder\" + appId : path) is string launch) Pin(launch);
    }

    static PinEntry? Create(string launch)
    {
        if (launch.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(launch)) return null;
            string? name = null;
            try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(launch).FileDescription; } catch { }
            return new PinEntry(launch, null, launch, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(launch) : name.Trim());
        }
        if (launch.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
        {
            string id = launch[@"shell:AppsFolder\".Length..];
            return new PinEntry(launch, id, null, ShellIcon.DisplayName(launch) ?? id);
        }
        var (appId, target) = ShellIcon.ForShortcut(launch);
        // File Explorer's pin points at a shell item, not an exe, and its windows may not carry its AUMID.
        if (target == null && "Microsoft.Windows.Explorer".Equals(appId, StringComparison.OrdinalIgnoreCase))
            (appId, target) = (null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        return new PinEntry(launch, appId, target, Path.GetFileNameWithoutExtension(launch));
    }

    /// <summary>Launch strings in taskbar order: .lnk paths and shell:AppsFolder\ links.</summary>
    static List<string> Read()
    {
        string taskbar = Path.Combine(PinnedRoot, "TaskBar"), implicitDir = Path.Combine(PinnedRoot, "ImplicitAppShortcuts");
        var shortcuts = Directory.Exists(taskbar) ? Directory.GetFiles(taskbar, "*.lnk").ToList() : new List<string>();
        var implicitOnes = Directory.Exists(implicitDir) ? Directory.GetFiles(implicitDir, "*.lnk", SearchOption.AllDirectories) : Array.Empty<string>();

        var result = new List<string>();
        void Add(string s) { if (!result.Contains(s, StringComparer.OrdinalIgnoreCase)) result.Add(s); }

        if (Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband")?.GetValue("Favorites") is byte[] blob)
        {
            // Strings may start on odd bytes, so read both alignments and put the hits back in byte order.
            var hits = new List<(int At, string Text)>();
            foreach (int align in new[] { 0, 1 })
            {
                int len = (blob.Length - align) & ~1;
                string s = Encoding.Unicode.GetString(blob, align, len);
                foreach (Match m in Names.Matches(s)) hits.Add((align + 2 * m.Index, m.Value));
            }
            foreach (var (_, text) in hits.OrderBy(h => h.At))
            {
                if (StoreAppId.IsMatch(text)) Add(@"shell:AppsFolder\" + text);
                else if (text.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && FindShortcut(text) is string lnk) Add(lnk);
            }
        }
        // A pin the blob didn't name (it's undocumented, so be forgiving) still shows, at the end.
        foreach (var lnk in shortcuts) Add(lnk);
        // The island's own pins and unpins.
        var settings = Islands.Settings;
        result.RemoveAll(p => settings.HiddenPins.Contains(p, StringComparer.OrdinalIgnoreCase));
        foreach (var extra in settings.ExtraPins) Add(extra);
        return result;

        string? FindShortcut(string name)
        {
            // A length byte before the name can read as a stray character ("!Steam.lnk"); try without it too.
            foreach (var n in new[] { name, name[1..] })
            {
                if (shortcuts.FirstOrDefault(f => Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase)) is string a) return a;
                if (implicitOnes.FirstOrDefault(f => Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase)) is string b) return b;
            }
            return null;
        }
    }
}
