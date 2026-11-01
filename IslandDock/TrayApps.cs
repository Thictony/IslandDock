using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace IslandDock;

/// <summary>
/// An app that's running with only a tray icon (Steam closed to the tray, say), listed in the tray dropdown.
/// Clicking it starts the exe again, which for these apps brings their window back.
/// </summary>
internal sealed class TrayEntry
{
    public string Path { get; }
    public ImageSource? Icon { get; }
    public string Title { get; }

    public TrayEntry(string path, string title)
    {
        Path = path;
        Title = title;
        Icon = WindowUtil.GetExeIcon(path);
    }
}

/// <summary>
/// Windows keeps every app that has shown a tray icon under HKCU\Control Panel\NotifyIconSettings, with its exe path.
/// An app from that list that's running now but has no taskbar window is living in the tray.
/// </summary>
internal static class TrayApps
{
    static readonly TimeSpan ListAge = TimeSpan.FromSeconds(30);
    static Dictionary<string, string> known = new(StringComparer.OrdinalIgnoreCase); // exe path -> title
    static DateTime knownAt = DateTime.MinValue;

    /// <param name="windowPaths">Exe paths of processes that own a taskbar or docked window.</param>
    public static List<(string Path, string Title)> Running(IEnumerable<string> windowPaths)
    {
        if (DateTime.UtcNow - knownAt > ListAge) { known = ReadKnown(); knownAt = DateTime.UtcNow; }

        // Only look up the paths of processes whose name matches a known tray exe; that's a handful.
        var byName = known.Keys.ToLookup(p => System.IO.Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!byName.Contains(p.ProcessName)) continue;
                if (WindowUtil.GetProcessPath((uint)p.Id) is string path && known.ContainsKey(path)) running.Add(path);
            }
        }

        // An app whose window is already a tab doesn't need a tray tab too. Its window may belong to a helper in a
        // subfolder (Steam's is steamwebhelper.exe under Steam\bin), so match on the tray exe's folder.
        var windows = windowPaths.ToList();
        return running
            .Where(path =>
            {
                string dir = System.IO.Path.GetDirectoryName(path) + System.IO.Path.DirectorySeparatorChar;
                return !windows.Any(w => w.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
            })
            // One tab per app: PowerToys.Awake sits beside PowerToys.exe. Prefer the exe named after its folder.
            .GroupBy(path => System.IO.Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => System.IO.Path.GetFileName(p).StartsWith(System.IO.Path.GetFileName(g.Key) ?? "", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .Select(path => (path, known[path]))
            .OrderBy(a => a.Item2, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // Background helpers that only show a tray icon now and then; starting them again isn't a way to open anything.
    static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvcontainer", "NVDisplay.Container", "jucheck", "jusched",
    };

    static Dictionary<string, string> ReadKnown()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + System.IO.Path.DirectorySeparatorChar;
        string self = Environment.ProcessPath ?? "";
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return result;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("ExecutablePath") is not string raw) continue;
                string path = ExpandKnownFolder(raw);
                // Windows' own tray icons (security, volume, drivers) aren't apps to switch to.
                if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || path.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                if (Helpers.Contains(System.IO.Path.GetFileNameWithoutExtension(path))) continue;
                if (result.ContainsKey(path) || !File.Exists(path)) continue;
                result[path] = TitleFor(path, key.GetValue("InitialTooltip") as string);
            }
        }
        catch { }
        return result;
    }

    /// <summary>Paths are stored as "{known folder GUID}\rest", e.g. {7C5A40EF-…}\Steam\steam.exe for Program Files (x86).</summary>
    static string ExpandKnownFolder(string raw)
    {
        int end = raw.StartsWith('{') ? raw.IndexOf('}') : -1;
        if (end < 0 || !Guid.TryParse(raw[..(end + 1)], out var id)) return raw;
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return raw;
        try { return Marshal.PtrToStringUni(p) + raw[(end + 1)..]; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    static string TitleFor(string path, string? tooltip)
    {
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc)) return desc;
        }
        catch { }
        var first = tooltip?.Split('\n')[0].Trim();
        return string.IsNullOrEmpty(first) ? System.IO.Path.GetFileNameWithoutExtension(path) : first;
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
