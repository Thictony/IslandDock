using System;
using System.Threading.Tasks;

namespace IslandDock;

// The menu's update link.
public partial class IslandWindow
{
    static string UpdateLabel => Updater.Busy ? "Downloading the update…"
        : Updater.Available is UpdateInfo u ? $"Update to version {u.Version} (you have {Updater.Current})"
        : $"Check for updates (version {Updater.Current})";

    /// <summary>A quiet check soon after starting, so the menu's link can say when there's an update.</summary>
    internal static async void CheckForUpdatesInBackground()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        Updater.CleanUp();
        try
        {
            if (await Updater.CheckAsync() is UpdateInfo u && Islands.All.Count > 0)
                Islands.All[0].ShowStatus($"Update {u.Version} available: see the right-click menu");
        }
        catch { } // Offline, most likely. The menu's link reports problems when it's asked directly.
    }

    async void CheckForUpdates()
    {
        if (Updater.Busy) return;
        Updater.Busy = true;
        try
        {
            UpdateInfo? update;
            try { update = await Updater.CheckAsync(); }
            catch (Exception ex)
            {
                Updater.Tell("Couldn't check for updates.\n\n" + ex.Message);
                return;
            }
            if (update == null)
            {
                Updater.Tell($"IslandDock is up to date (version {Updater.Current}).");
                return;
            }
            if (!Updater.Ask($"Version {update.Version} is available. You have {Updater.Current}.\n\n"
                + "Update now? It downloads in the background, then the island closes and reopens by itself. "
                + "Docked windows go back to being normal windows, and your settings are kept."))
                return;

            ShowStatus("Downloading the update…");
            try { Updater.RunInstaller(await Updater.DownloadAsync(update)); }
            catch (Exception ex) { Updater.Tell("Couldn't download the update.\n\n" + ex.Message); }
        }
        finally { Updater.Busy = false; }
    }
}
