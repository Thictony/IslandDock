using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace IslandDock;

/// <summary>A Windows notification as the island shows it.</summary>
internal sealed record Notice(string App, string Title, string Body, ImageSource? Icon, string? AppId, DateTime Arrived);

/// <summary>
/// Reads Windows notifications as they come in and hands them to the island. The listener's change event needs the app
/// to be packaged, so the notification list is checked every second instead; anything already there at start is skipped.
/// </summary>
internal static class Notifications
{
    static readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    static HashSet<uint>? seen;
    static bool busy;

    public static event Action<Notice>? Arrived;

    /// <summary>Starts or stops watching, per <see cref="Settings.IslandNotifications"/>.</summary>
    public static void Apply()
    {
        timer.Tick -= Poll;
        if (Islands.Settings.IslandNotifications)
        {
            timer.Tick += Poll;
            timer.Start();
        }
        else
        {
            timer.Stop();
            seen = null;
        }
        if (!Islands.Settings.IslandNotifications || !Islands.Settings.HideWindowsBanners) WindowsBanners.Restore();
    }

    /// <summary>The island is going away, and Windows' pop-ups come back with it.</summary>
    public static void Stop()
    {
        timer.Stop();
        WindowsBanners.Restore();
    }

    static async void Poll(object? sender, EventArgs e)
    {
        if (busy) return;
        busy = true;
        try
        {
            var listener = UserNotificationListener.Current;
            var access = listener.GetAccessStatus();
            if (access == UserNotificationListenerAccessStatus.Unspecified) access = await listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                // The island can't show them, so Windows' pop-ups have to.
                WindowsBanners.Restore();
                timer.Stop();
                return;
            }
            if (Islands.Settings.HideWindowsBanners) WindowsBanners.Hide();

            var list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (seen != null)
                foreach (var n in list.Where(n => !seen.Contains(n.Id)).OrderBy(n => n.CreationTime))
                    // One that turns up late (the PC was asleep, an app re-posting old ones) isn't news.
                    if (DateTimeOffset.Now - n.CreationTime < TimeSpan.FromSeconds(30) && await Read(n) is Notice notice)
                        Arrived?.Invoke(notice);
            seen = list.Select(n => n.Id).ToHashSet();
        }
        catch { }
        finally { busy = false; }
    }

    static async System.Threading.Tasks.Task<Notice?> Read(UserNotification n)
    {
        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements().Select(t => Clean(t.Text)).Where(t => t.Length > 0).ToList() ?? new();
        if (texts.Count == 0) return null;

        string app = "", appId = "";
        ImageSource? icon = null;
        try
        {
            app = n.AppInfo.DisplayInfo.DisplayName;
            appId = n.AppInfo.AppUserModelId;
            var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await logo.OpenReadAsync();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream.AsStreamForRead();
            bmp.EndInit();
            bmp.Freeze();
            icon = bmp;
        }
        catch { }
        return new Notice(app, texts[0], string.Join("\n", texts.Skip(1)), icon, appId.Length > 0 ? appId : null, DateTime.Now);
    }

    /// <summary>Without the direction marks some apps (Discord) wrap names in.</summary>
    static string Clean(string? s) => (s ?? "").Replace("⁨", "").Replace("⁩", "").Trim();
}

/// <summary>
/// Turns off the pop-ups Windows shows for notifications, app by app: the "Show notification banners" switch under
/// System > Notifications in Windows' settings. Notifications still reach Windows' list, which is where the island
/// reads them. Only for as long as the island runs and can read them; the apps it switched off are remembered in the
/// registry, so they can be put back even after it was stopped without closing (the uninstaller does the same).
/// </summary>
internal static class WindowsBanners
{
    const string AppsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    const string ChangedKey = @"Software\IslandDock\HiddenBanners";
    const string Banner = "ShowBanner";
    static int appsSeen = -1;

    /// <summary>Switches off any app not done yet; Windows adds an app the first time it posts a notification.</summary>
    public static void Hide()
    {
        try
        {
            using var apps = Registry.CurrentUser.OpenSubKey(AppsKey);
            if (apps == null || apps.SubKeyCount == appsSeen) return;
            appsSeen = apps.SubKeyCount;
            using var changed = Registry.CurrentUser.CreateSubKey(ChangedKey);
            foreach (var name in apps.GetSubKeyNames())
            {
                using var app = apps.OpenSubKey(name, true);
                // One that's already off was the user's choice, and stays off when the rest come back.
                if (app == null || app.GetValue(Banner) is 0) continue;
                changed.SetValue(name, 1);
                app.SetValue(Banner, 0, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    /// <summary>Puts back every app it switched off, unless the user has since changed it themselves.</summary>
    public static void Restore()
    {
        appsSeen = -1;
        try
        {
            using var changed = Registry.CurrentUser.OpenSubKey(ChangedKey);
            if (changed == null) return;
            foreach (var name in changed.GetValueNames())
            {
                using var app = Registry.CurrentUser.OpenSubKey(AppsKey + "\\" + name, true);
                if (app?.GetValue(Banner) is 0) app.DeleteValue(Banner, false);
            }
        }
        catch { return; }
        try { Registry.CurrentUser.DeleteSubKeyTree(ChangedKey, false); } catch { }
    }
}
