using System;
using System.Threading.Tasks;
using System.Windows;

namespace IslandDock;

// The menu's update link and the same question soon after starting, both answered in a panel inside the island.
public partial class IslandWindow
{
    UpdateInfo? offeredUpdate;

    static string UpdateLabel => Updater.Busy ? "Downloading the update…"
        : Updater.Available is UpdateInfo u ? $"Update to version {u.Version} (you have {Updater.Current})"
        : $"Check for updates (version {Updater.Current})";

    /// <summary>A check soon after starting: when this copy is behind the latest release, it asks to update.</summary>
    internal static async void CheckForUpdatesInBackground()
    {
        if (Updater.TakeInstallError() is string failed && Islands.All.Count > 0)
            Islands.All[0].ShowInstallError(failed);
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (Updater.Busy) return; // the menu's link got there first
        Updater.CleanUp();
        Updater.Busy = true;
        UpdateInfo? update = null;
        try { update = await Updater.CheckAsync(); }
        catch { } // Offline, most likely. The menu's link reports problems when it's asked directly.
        finally { Updater.Busy = false; }
        if (update != null && Islands.All.Count > 0) Islands.All[0].OfferUpdate(update);
    }

    async void CheckForUpdates()
    {
        if (Updater.Busy) return;
        Updater.Busy = true;
        try
        {
            if (await Updater.CheckAsync() is UpdateInfo update) OfferUpdate(update);
            else ShowUpdatePanel("You're up to date", $"IslandDock is on the latest version ({Updater.Current}).");
        }
        catch (Exception ex) { ShowUpdatePanel("Couldn't check for updates", ex.Message); }
        finally { Updater.Busy = false; }
    }

    void OfferUpdate(UpdateInfo update) => ShowUpdatePanel($"Version {update.Version} is available",
        $"You have {Updater.Current}. It downloads in the background, then the island closes and reopens by itself. "
        + "Docked windows go back to being normal windows, and your settings are kept.", update);

    void ShowInstallError(string message) => ShowUpdatePanel("Couldn't install the update",
        message + " You can download it again from github.com/Thictony/IslandDock/releases.");

    /// <summary>Opens the island on the panel, which stays until it's answered. Its buttons can't take keyboard focus,
    /// so typing can't start an update when it appears mid-sentence.</summary>
    void ShowUpdatePanel(string title, string text, UpdateInfo? offer = null, string dismiss = "OK")
    {
        offeredUpdate = offer;
        UpdateTitle.Text = title;
        UpdateText.Text = text;
        UpdateNowButton.Visibility = offer != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateDismissButton.Content = offer != null ? "Not now" : dismiss;
        pickerOpen = mixerOpen = searchOpen = trayOpen = spotsOpen = false;
        updateOpen = true;
        if (!expanded) openRequested = true;
        Wake();
    }

    async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (offeredUpdate is not UpdateInfo update || Updater.Busy) return;
        Updater.Busy = true;
        ShowUpdatePanel("Downloading the update…", "The island closes and reopens by itself when it's ready.", dismiss: "Hide");
        try
        {
            Updater.RunInstaller(await Updater.DownloadAsync(update));
            // The installer normally closes this copy. If it can't, it leaves word of why.
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(1000);
                if (Updater.TakeInstallError() is string failed)
                {
                    ShowInstallError(failed);
                    return;
                }
            }
        }
        catch (Exception ex) { ShowUpdatePanel("Couldn't download the update", ex.Message); }
        finally { Updater.Busy = false; }
    }

    void UpdateDismiss_Click(object sender, RoutedEventArgs e)
    {
        updateOpen = false;
        Wake();
    }
}
