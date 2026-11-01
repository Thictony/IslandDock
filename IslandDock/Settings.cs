using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace IslandDock;

internal sealed class Settings
{
    public string Edge { get; set; } = "Top";
    public double Along { get; set; } = 0.5;
    public string? Monitor { get; set; }
    /// <summary>Where each display's island sits, by display name. Edge/Along/Monitor above are from when there was
    /// only one island; that display's island starts where it was.</summary>
    public System.Collections.Generic.Dictionary<string, Placement> Displays { get; set; } = new();
    /// <summary>Displays the user turned the island off on.</summary>
    public System.Collections.Generic.List<string> DisabledDisplays { get; set; } = new();
    public bool RunAtStartup { get; set; } = true;
    /// <summary>
    /// Draws the island on the CPU. Some laptop graphics drivers (often switchable Intel + NVIDIA/AMD) stop showing a
    /// transparent window once it has been resized, so the island vanished after its first open. Unset means on for
    /// laptops (a battery), where that's been seen, and off elsewhere.
    /// </summary>
    public bool? SoftwareRendering { get; set; }

    /// <summary>Hides the Windows taskbar while the island runs.</summary>
    public bool HideTaskbar { get; set; }
    /// <summary>Alt+Tab opens the island and steps through its docked tabs (Windows' own when nothing is docked).</summary>
    public bool AltTabSwitchesTabs { get; set; } = true;
    /// <summary>The Windows key on its own opens or closes the island instead of Start.</summary>
    public bool WinKeyOpensIsland { get; set; } = true;
    /// <summary>A game or fullscreen video fades the islands on every display, not just the one it's on.</summary>
    public bool FadeOnOtherDisplays { get; set; }
    /// <summary>Windows notifications are shown by the island instead of as Windows' own pop-ups.</summary>
    public bool IslandNotifications { get; set; } = true;
    /// <summary>The first-run tour has been seen (finished or skipped).</summary>
    public bool TutorialDone { get; set; }
    /// <summary>Apps pinned from the island, after the Windows taskbar's own pins (exe paths or shell:AppsFolder\ links).</summary>
    public System.Collections.Generic.List<string> ExtraPins { get; set; } = new();
    /// <summary>Windows taskbar pins unpinned from the island, so it leaves them out.</summary>
    public System.Collections.Generic.List<string> HiddenPins { get; set; } = new();
    /// <summary>The user's own taskbar auto-hide setting, kept while the island has it hidden so it can be put back,
    /// even after the island was stopped without closing (a crash, a shutdown).</summary>
    public bool? TaskbarAutoHideBefore { get; set; }

    public bool UseSoftwareRendering => SoftwareRendering
        ?? System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
    public const double DefaultScale = 1.5;
    public double PanelScale { get; set; } = DefaultScale;
    /// <summary>The scale last chosen for each app (lower-case process name), reused when it's docked again.</summary>
    public System.Collections.Generic.Dictionary<string, double> AppScales { get; set; } = new();

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandDock", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

internal sealed class Placement
{
    public string Edge { get; set; } = "Top";
    public double Along { get; set; } = 0.5;
}

internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "IslandDock";

    /// <summary>Where publish puts the program; startup always runs this copy, not whichever build happened to run last.</summary>
    public static string InstalledPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "IslandDock", "IslandDock.exe");

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            string? path = File.Exists(InstalledPath) ? InstalledPath : Environment.ProcessPath;
            if (enabled && path != null) key.SetValue(ValueName, $"\"{path}\"");
            else key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
