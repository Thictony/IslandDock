using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace IslandDock;

/// <summary>
/// Moves a newly opened Explorer window into a new tab of an Explorer window that's already open, the way a browser
/// opens links in a tab. Explorer has no API for tabs, so this does what Ctrl+T does (an undocumented command sent
/// to its tab window), then points the new tab at the folder through the shell's window list.
/// </summary>
internal static class ExplorerTabs
{
    const uint WM_COMMAND = 0x111, WM_CLOSE = 0x10;
    const int NewTabCommand = 0xA21B;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    // Typed as object so calls taking it stay statically bound.
    static object NewShell() => Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;

    /// <summary>Each tab of window h, with its COM identity so a new tab can be told from the ones before it.</summary>
    static List<(dynamic Tab, IntPtr Id)> TabsOf(object shell, IntPtr h)
    {
        var list = new List<(dynamic, IntPtr)>();
        dynamic windows = ((dynamic)shell).Windows();
        int n = windows.Count;
        for (int i = 0; i < n; i++)
        {
            try
            {
                dynamic? w = windows.Item(i);
                if (w == null || (IntPtr)Convert.ToInt64(w.HWND) != h) continue;
                IntPtr id = Marshal.GetIUnknownForObject((object)w);
                Marshal.Release(id);
                list.Add((w, id));
            }
            catch { }
        }
        return list;
    }

    static string? PathOf(dynamic tab)
    {
        try { return (string)tab.Document.Folder.Self.Path; } catch { return null; }
    }

    /// <summary>
    /// Opens what window <paramref name="from"/> shows as a new tab of <paramref name="into"/>, keeping its selection
    /// (e.g. "Show in folder"), then closes <paramref name="from"/>. False if it couldn't, with <paramref name="from"/> left open.
    /// </summary>
    public static async Task<bool> MoveToTab(IntPtr from, IntPtr into)
    {
        try
        {
            var shell = NewShell();
            // The new window registers with the shell and picks its folder a moment after it shows.
            string? path = null;
            var selected = new List<string>();
            for (int i = 0; i < 60 && path == null; i++)
            {
                foreach (var (tab, _) in TabsOf(shell, from))
                {
                    path = PathOf(tab);
                    if (path == null) continue;
                    try
                    {
                        dynamic items = tab.Document.SelectedItems();
                        int n = items.Count;
                        for (int j = 0; j < n; j++) selected.Add(System.IO.Path.GetFileName((string)items.Item(j).Path));
                    }
                    catch { }
                }
                if (path == null) await Task.Delay(50);
            }
            if (path == null) return false;

            var tabWindow = FindWindowEx(into, IntPtr.Zero, "ShellTabWindowClass", null);
            if (tabWindow == IntPtr.Zero) return false;
            var before = new HashSet<IntPtr>();
            foreach (var (_, id) in TabsOf(shell, into)) before.Add(id);
            PostMessage(tabWindow, WM_COMMAND, (IntPtr)NewTabCommand, IntPtr.Zero);

            dynamic? fresh = null;
            for (int i = 0; i < 60 && fresh == null; i++)
            {
                await Task.Delay(50);
                foreach (var (tab, id) in TabsOf(shell, into))
                    if (!before.Contains(id)) fresh = tab;
            }
            if (fresh == null) return false;

            // Virtual folders (Home, This PC) come back as "::{GUID}", which navigating needs as a shell: path.
            fresh.Navigate2(path.StartsWith("::") ? "shell:" + path : path);
            PostMessage(from, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            if (selected.Count > 0)
            {
                for (int i = 0; i < 60 && !string.Equals(PathOf(fresh), path, StringComparison.OrdinalIgnoreCase); i++)
                    await Task.Delay(50);
                bool first = true;
                foreach (var name in selected)
                {
                    try
                    {
                        dynamic item = fresh.Document.Folder.ParseName(name);
                        // Select, and for the first one also clear others, scroll to it and focus it.
                        if (item != null) fresh.Document.SelectItem(item, first ? 1 | 4 | 8 | 16 : 1);
                        first = false;
                    }
                    catch { }
                }
            }
            return true;
        }
        catch { return false; }
    }
}
