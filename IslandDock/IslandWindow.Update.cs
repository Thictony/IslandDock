using System;
using System.Threading.Tasks;

namespace IslandDock;

// The menu's update link, and the same question soon after starting.
public partial class IslandWindow
{
    static string UpdateLabel => Updater.Busy ? "Downloading the update…"
        : Updater.Available is UpdateInfo u ? $"Update to version {u.Version} (you have {Updater.Current})"
        : $"Check for updates (version {Updater.Current})";

    /// <summary>A check soon after starting: when this copy is behind the latest release, it asks to update.</summary>
    internal static async void CheckForUpdatesInBackground()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (Updater.Busy) return; // the menu's link got there first
        Updater.CleanUp();
        Updater.Busy = true;
        try
        {
            if (await Updater.CheckAsync() is UpdateInfo u && Islands.All.Count > 0)
                await Islands.All[0].OfferUpdate(u);
        }
        catch { } // Offline, most likely. The menu's link reports problems when it's asked directly.
        finally { Updater.Busy = false; }
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
            await OfferUpdate(update);
        }
        finally { Updater.Busy = false; }
    }

    async Task OfferUpdate(UpdateInfo update)
    {
        if (!Updater.Ask($"Version {update.Version} is available. You have {Updater.Current}.\n\n"
            + "Update now? It downloads in the background, then the island closes and reopens by itself. "
            + "Docked windows go back to being normal windows, and your settings are kept."))
            return;

        ShowStatus("Downloading the update…");
        try { Updater.RunInstaller(await Updater.DownloadAsync(update)); }
        catch (Exception ex) { Updater.Tell("Couldn't download the update.\n\n" + ex.Message); }
    }
}
