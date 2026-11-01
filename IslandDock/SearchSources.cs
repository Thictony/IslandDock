using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace IslandDock;

/// <summary>
/// One row of the search results: a section header ("Best match", "Apps"…) or something to open. Files can also be
/// shown in their folder; for the rest, Launch is handed to the shell (an app link, an ms-settings: page, a URL).
/// </summary>
internal sealed class SearchHit
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Launch { get; init; } = "";
    public ImageSource? Icon { get; init; }
    /// <summary>Segoe Fluent glyph shown when there's no icon (web search).</summary>
    public string Glyph { get; init; } = "";
    public string? FilePath { get; init; }
    public bool IsHeader { get; init; }
    public bool IsItem => !IsHeader;
    public bool CanReveal => FilePath != null;
    public bool HasGlyph => Icon == null && Glyph.Length > 0;

    public static SearchHit Header(string text) => new() { Name = text, IsHeader = true };
}

/// <summary>
/// The sources Windows search looks through besides files: installed apps (everything in Start's All apps list) and
/// Settings pages. Matching works like Start: every typed word must begin a word of the name, or the name contains it.
/// </summary>
internal static class SearchSources
{
    static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    static List<(string Name, string Id)> apps = new();
    static DateTime appsAt = DateTime.MinValue;
    static Task? appsLoading;

    const string SettingsAppId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    static readonly (string Name, string Keywords, string Uri)[] SettingsPages =
    {
        ("Display settings", "screen monitor resolution brightness scale night light hdr", "ms-settings:display"),
        ("Sound settings", "audio volume speakers microphone output input", "ms-settings:sound"),
        ("Volume mixer", "audio app volume sound", "ms-settings:apps-volume"),
        ("Bluetooth & devices", "bluetooth devices pair mouse keyboard headphones", "ms-settings:bluetooth"),
        ("Wi-Fi settings", "wifi wireless network internet", "ms-settings:network-wifi"),
        ("Network & internet", "network internet ethernet vpn proxy", "ms-settings:network"),
        ("Notifications", "notifications focus do not disturb", "ms-settings:notifications"),
        ("Power & battery", "power battery sleep energy saver", "ms-settings:powersleep"),
        ("Storage", "storage disk space cleanup", "ms-settings:storagesense"),
        ("Personalization", "personalize theme colors background wallpaper", "ms-settings:personalization"),
        ("Background", "wallpaper desktop background image", "ms-settings:personalization-background"),
        ("Colors", "colors accent dark mode light mode theme", "ms-settings:colors"),
        ("Taskbar settings", "taskbar pins tray", "ms-settings:taskbar"),
        ("Start settings", "start menu", "ms-settings:personalization-start"),
        ("Installed apps", "apps uninstall programs add remove", "ms-settings:appsfeatures"),
        ("Default apps", "default apps browser file types", "ms-settings:defaultapps"),
        ("Startup apps", "startup apps boot launch", "ms-settings:startupapps"),
        ("Accounts", "account user profile sign-in", "ms-settings:yourinfo"),
        ("Sign-in options", "password pin windows hello sign-in", "ms-settings:signinoptions"),
        ("Date & time", "date time clock timezone", "ms-settings:dateandtime"),
        ("Language & region", "language region keyboard input", "ms-settings:regionlanguage"),
        ("Gaming", "game mode game bar xbox captures", "ms-settings:gaming-gamebar"),
        ("Game Mode", "game mode performance", "ms-settings:gaming-gamemode"),
        ("Accessibility", "accessibility ease of access narrator magnifier", "ms-settings:easeofaccess"),
        ("Mouse settings", "mouse pointer cursor scroll", "ms-settings:mousetouchpad"),
        ("Keyboard settings", "keyboard typing", "ms-settings:keyboard"),
        ("Printers & scanners", "printer scanner print", "ms-settings:printers"),
        ("Privacy & security", "privacy security permissions camera microphone location", "ms-settings:privacy"),
        ("Windows Security", "antivirus defender virus firewall", "windowsdefender:"),
        ("Windows Update", "update upgrade patch", "ms-settings:windowsupdate"),
        ("About", "about pc system info specs name", "ms-settings:about"),
        ("Graphics settings", "graphics gpu performance", "ms-settings:display-advancedgraphics"),
    };

    /// <summary>Starts reading the app list early, so the first search doesn't wait for it.</summary>
    public static void Warm() => EnsureApps();

    public static List<SearchHit> Apps(string text, int max)
    {
        EnsureApps();
        return apps
            .Select(a => (a.Name, a.Id, Score: Score(a.Name, "", text)))
            .Where(a => a.Score < int.MaxValue)
            .OrderBy(a => a.Score).ThenBy(a => a.Name.Length)
            .Take(max)
            .Select(a => new SearchHit
            {
                Name = a.Name, Detail = "App", Launch = @"shell:AppsFolder\" + a.Id,
                Icon = Icons.GetOrAdd(a.Id, id => ShellIcon.ForAppId(id, 32)),
            })
            .ToList();
    }

    public static List<SearchHit> Settings(string text, int max)
    {
        var icon = Icons.GetOrAdd(SettingsAppId, id => ShellIcon.ForAppId(id, 32));
        return SettingsPages
            .Select(s => (s, Score: Score(s.Name, s.Keywords, text)))
            .Where(x => x.Score < int.MaxValue)
            .OrderBy(x => x.Score)
            .Take(max)
            .Select(x => new SearchHit { Name = x.s.Name, Detail = "System settings", Launch = x.s.Uri, Icon = icon })
            .ToList();
    }

    /// <summary>
    /// What Windows search does with a typed location: a path (environment variables allowed, like %temp%) opens that
    /// folder or file, and a partial one suggests what's in its folder; a web address opens in the browser.
    /// Exact is set when the first hit is the typed location itself rather than a suggestion.
    /// </summary>
    public static (bool Exact, List<SearchHit> Hits) Locations(string text, int max)
    {
        var hits = new List<SearchHit>();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && !text.Contains(' '))
        {
            string url = text.Contains("://") ? text : "https://" + text;
            hits.Add(new SearchHit { Name = text, Detail = "Open website", Launch = url, Glyph = "" });
            return (true, hits);
        }

        string path = Environment.ExpandEnvironmentVariables(text.Trim('"'));
        if (path.StartsWith("~")) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return (false, hits);
            if (System.IO.Directory.Exists(path) || System.IO.File.Exists(path))
            {
                string full = System.IO.Path.GetFullPath(path);
                bool dir = System.IO.Directory.Exists(full);
                hits.Add(new SearchHit
                {
                    Name = System.IO.Path.GetFileName(full.TrimEnd('\\')) is { Length: > 0 } n ? n : full,
                    Detail = (dir ? "File folder · " : "File · ") + full, Launch = full, FilePath = full,
                    Icon = ShellIcon.ForItem(full, 32),
                });
                // Inside a folder, list what's there, like the suggestions under Windows search's best match.
                if (dir) hits.AddRange(System.IO.Directory.EnumerateFileSystemEntries(full).Take(max).Select(Entry));
                return (true, hits);
            }
            // A partial path: suggest the entries in its folder that start with the last part.
            string? parent = System.IO.Path.GetDirectoryName(path);
            string start = System.IO.Path.GetFileName(path);
            if (parent != null && System.IO.Directory.Exists(parent))
                hits.AddRange(System.IO.Directory.EnumerateFileSystemEntries(parent, start + "*").Take(max).Select(Entry));
        }
        catch { }
        return (false, hits);

        static SearchHit Entry(string p) => new()
        {
            Name = System.IO.Path.GetFileName(p), Detail = System.IO.Path.GetDirectoryName(p) ?? "", Launch = p, FilePath = p,
            Icon = ShellIcon.ForItem(p, 32),
        };
    }

    /// <summary>Lower is better; int.MaxValue for no match.</summary>
    public static int Score(string name, string keywords, string text)
    {
        if (name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 0;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nameWords = name.Split(new[] { ' ', '-', '.', '_', '&', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => nameWords.Any(n => n.StartsWith(w, StringComparison.OrdinalIgnoreCase)))) return 1;
        if (name.Contains(text, StringComparison.OrdinalIgnoreCase)) return 2;
        var keyWords = keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (keyWords.Length > 0 && words.All(w => keyWords.Concat(nameWords).Any(k => k.StartsWith(w, StringComparison.OrdinalIgnoreCase)))) return 3;
        return int.MaxValue;
    }

    /// <summary>
    /// Reads shell:AppsFolder (Start's All apps: desktop and Store apps alike) on an STA thread, as the shell's
    /// automation objects want; refreshed every few minutes so newly installed apps turn up.
    /// </summary>
    static void EnsureApps()
    {
        if (appsLoading is { IsCompleted: false } || DateTime.UtcNow - appsAt < TimeSpan.FromMinutes(5)) return;
        appsAt = DateTime.UtcNow;
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                var list = new List<(string, string)>();
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type != null)
                {
                    dynamic shell = Activator.CreateInstance(type)!;
                    dynamic items = shell.NameSpace("shell:AppsFolder").Items();
                    int n = items.Count;
                    for (int i = 0; i < n; i++)
                    {
                        dynamic item = items.Item(i);
                        if (item.Name is string name && item.Path is string id && name.Length > 0 && id.Length > 0)
                            list.Add((name, id));
                    }
                }
                if (list.Count > 0) apps = list;
            }
            catch { }
            done.SetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        appsLoading = done.Task;
    }

    /// <summary>Waits (briefly) for the first read of the app list.</summary>
    public static Task AppsReady(CancellationToken ct) => appsLoading is Task t ? t.WaitAsync(TimeSpan.FromSeconds(3), ct) : Task.CompletedTask;
}
