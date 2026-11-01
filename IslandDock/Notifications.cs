using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
            if (access != UserNotificationListenerAccessStatus.Allowed) { timer.Stop(); return; }

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
