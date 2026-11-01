using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace IslandDock;

/// <summary>
/// An island on every display the user hasn't turned it off on, each with its own tabs. They share the settings and
/// the Discord reader; things that should only happen once (merging new Explorer windows, putting the taskbar back)
/// are decided here.
/// </summary>
internal static class Islands
{
    public static readonly Settings Settings = Settings.Load();
    public static readonly DiscordStatus Discord = new();
    /// <summary>The main display's island first.</summary>
    public static readonly List<IslandWindow> All = new();

    static bool exiting;
    static bool pinnedArg = Environment.GetCommandLineArgs().Contains("--pinned");
    static Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

    public static void Start()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        Discord.Start();
        Discord.StartCall();
        Sync();
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;

        KeyboardHook.AltTab = back =>
        {
            if (!Settings.AltTabSwitchesTabs || KeyTarget(needTabs: true) is not IslandWindow island) return false;
            dispatcher.BeginInvoke(() => island.CycleTabs(back));
            return true;
        };
        KeyboardHook.WinKey = () =>
        {
            if (!Settings.WinKeyOpensIsland || KeyTarget(needTabs: false) is not IslandWindow island) return false;
            dispatcher.BeginInvoke(island.ToggleFromKeyboard);
            return true;
        };
        KeyboardHook.Start();
        Notifications.Arrived += Notify;
        Notifications.Apply();
        if (!Settings.TutorialDone) dispatcher.InvokeAsync(Tutorial.Open, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>A notification goes to the island on the display being used: the one with the window in front.</summary>
    static void Notify(Notice n)
    {
        var fg = Native.GetForegroundWindow();
        string? device = fg != IntPtr.Zero ? Forms.Screen.FromHandle(fg).DeviceName : null;
        (All.FirstOrDefault(i => i.Display == device) ?? All.FirstOrDefault())?.ShowNotice(n);
    }

    /// <summary>The island the keyboard shortcuts act on: the one on the display under the mouse, or for Alt+Tab
    /// the first that has docked tabs if that one has none.</summary>
    static IslandWindow? KeyTarget(bool needTabs)
    {
        Native.GetCursorPos(out var p);
        string device = Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).DeviceName;
        var islands = All.ToArray(); // read from the hook's thread
        var here = islands.FirstOrDefault(i => i.Display == device) ?? islands.FirstOrDefault();
        if (!needTabs || here is { HasTabs: true }) return here;
        return islands.FirstOrDefault(i => i.HasTabs) ?? here;
    }

    static void OnDisplaysChanged(object? sender, EventArgs e) => dispatcher.BeginInvoke(Sync);

    /// <summary>The displays that get an island: all but those turned off, and never none.</summary>
    static List<Forms.Screen> Wanted()
    {
        var screens = Forms.Screen.AllScreens;
        var on = screens.Where(s => !Settings.DisabledDisplays.Contains(s.DeviceName)).ToList();
        return on.Count > 0 ? on : new() { Forms.Screen.PrimaryScreen ?? screens[0] };
    }

    /// <summary>Opens islands on displays that should have one and closes those whose display went away or was turned off.</summary>
    static void Sync()
    {
        if (exiting) return;
        var wanted = Wanted();
        foreach (var island in All.Where(i => wanted.All(s => s.DeviceName != i.Display)).ToList())
        {
            All.Remove(island);
            island.Shut();
        }
        foreach (var s in wanted.Where(s => All.All(i => i.Display != s.DeviceName)))
        {
            bool pin = pinnedArg && s.Primary;
            if (pin) pinnedArg = false;
            var island = new IslandWindow(s.DeviceName, pin);
            All.Add(island);
            island.Show();
        }
        All.Sort((a, b) => IsPrimary(b.Display).CompareTo(IsPrimary(a.Display)));
    }

    static bool IsPrimary(string display) => Forms.Screen.PrimaryScreen?.DeviceName == display;

    public static bool IsEnabled(string display) => All.Any(i => i.Display == display);

    public static void SetEnabled(string display, bool on)
    {
        Settings.DisabledDisplays.Remove(display);
        if (!on) Settings.DisabledDisplays.Add(display);
        Settings.Save();
        Sync();
    }

    /// <summary>"Display 2 (2560×1440, main)".</summary>
    public static string Label(Forms.Screen s)
    {
        string name = s.DeviceName.TrimStart('\\', '.');
        if (name.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) name = "Display " + name[7..];
        return $"{name} ({s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? ", main" : "")})";
    }

    /// <summary>Which island takes a new Explorer window as a tab: the one with Explorer docked, else the main one.</summary>
    public static IslandWindow? ExplorerOwner => All.FirstOrDefault(i => i.HasDockedExplorer) ?? All.FirstOrDefault();

    /// <summary>Discord's window, if any island has it docked.</summary>
    public static IntPtr DockedDiscord => All.Select(i => i.DockedDiscord).FirstOrDefault(h => h != IntPtr.Zero);

    public static void UndockAll()
    {
        foreach (var island in All.ToList()) island.UndockAll();
    }

    /// <summary>Hides or brings back the Windows taskbar, per <see cref="Settings.HideTaskbar"/>.</summary>
    public static void ApplyTaskbar()
    {
        if (Settings.HideTaskbar && !exiting)
        {
            if (Settings.TaskbarAutoHideBefore == null)
            {
                Settings.TaskbarAutoHideBefore = TaskbarHider.AutoHide;
                Settings.Save();
            }
            if (!TaskbarHider.AutoHide) TaskbarHider.AutoHide = true;
            TaskbarHider.SetVisible(false);
        }
        else if (Settings.TaskbarAutoHideBefore is bool before)
        {
            TaskbarHider.AutoHide = before;
            TaskbarHider.SetVisible(true);
            Settings.TaskbarAutoHideBefore = null;
            Settings.Save();
        }
    }

    public static void ExitAll()
    {
        if (exiting) return;
        exiting = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        KeyboardHook.Stop();
        foreach (var island in All.ToList()) island.Shut();
        ApplyTaskbar(); // exiting: brings the taskbar back, but remembers to hide it next time
        Application.Current.Shutdown();
    }
}
