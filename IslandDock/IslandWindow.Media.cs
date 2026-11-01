using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace IslandDock;

// Audio-driven bits: the wave bars, the Discord voice state and the system media controls.
public partial class IslandWindow
{
    readonly AudioLevels levels = new();
    // One reader for every island.
    static DiscordStatus discord => Islands.Discord;
    Action<bool?, bool?, bool>? discordChanged;
    Action<System.Collections.Generic.IReadOnlyList<CallMember>>? callChanged;
    DispatcherTimer? meterTick;
    double collapsedLevel;
    long collapsedLastSound;

    MediaManager? mediaManager;
    MediaSession? mediaSession;
    string? mediaArtKey;

    void InitMedia()
    {
        RefreshAudioSources();
        meterTick = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => SampleLevels(), Dispatcher);

        discordChanged = (muted, deafened, sharing) => Dispatcher.BeginInvoke(() => ShowDiscord(muted, deafened, sharing));
        discord.Changed += discordChanged;
        callChanged = members => Dispatcher.BeginInvoke(() => ShowCall(members));
        discord.CallChanged += callChanged;
        // An island opened later (a display plugged in) starts from what the others show.
        var (m, d, s) = discord.State;
        ShowDiscord(m, d, s);
        ShowCall(discord.Call);

        InitMediaSession();
    }

    void StopMedia()
    {
        meterTick?.Stop();
        discord.Changed -= discordChanged;
        discord.CallChanged -= callChanged;
    }

    void SampleLevels()
    {
        levels.Sample();
        foreach (var d in docked) d.UpdateLevel(levels.For(d.ProcessId));

        (collapsedLevel, bool playing) = DockedWindow.Smooth(levels.Overall, collapsedLevel, ref collapsedLastSound);
        CollapsedWave.Level = collapsedLevel;
        var vis = playing ? Visibility.Visible : Visibility.Collapsed;
        if (CollapsedWave.Visibility != vis) { CollapsedWave.Visibility = vis; RefreshCollapsedIcons(); }
    }

    /// <summary>Called from the one-second tick: sessions come and go, and the docked Discord window can be read too.</summary>
    void RefreshAudioSources()
    {
        var playing = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var s in mediaManager?.GetSessions() ?? (System.Collections.Generic.IReadOnlyList<MediaSession>)Array.Empty<MediaSession>())
                if (s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && !string.IsNullOrEmpty(s.SourceAppUserModelId))
                    playing.Add(s.SourceAppUserModelId);
        }
        catch { }
        // A Store player (Media Player) is known by its AUMID, so find it through its windows; any app of its package counts.
        static string Family(string id) => id.Split('!')[0];
        levels.Refresh(playing, id => windowIds
            .Where(w => w.Value.Item1 is string a && Family(a).Equals(Family(id), StringComparison.OrdinalIgnoreCase))
            .Select(w => WindowUtil.GetPid(w.Key)));
        discord.DockedHandle = Islands.DockedDiscord;
        if (MiniPlayer.Visibility == Visibility.Visible && !mediaPlaying) UpdateMiniPlayer();

        bool relevant = DiscordRelevant();
        if (relevant != discordRelevant)
        {
            discordRelevant = relevant;
            var (m, d, s) = discord.State;
            ShowDiscord(m, d, s);
            ShowCall(discord.Call);
            RefreshCollapsedIcons();
            Wake();
        }
    }

    /// <summary>Whether the closed island shows Discord's voice state and call.</summary>
    bool discordRelevant;

    /// <summary>
    /// Only when Discord's window is docked (in any island), or open on a display other than this island's, where
    /// Discord itself can't be seen from here. On the same display, or minimized to the tray, the island leaves it out.
    /// </summary>
    bool DiscordRelevant()
    {
        if (Islands.DockedDiscord != IntPtr.Zero) return true;
        var h = DiscordStatus.FindWindow();
        if (h == IntPtr.Zero || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
        return System.Windows.Forms.Screen.FromHandle(h).DeviceName != Display;
    }

    static readonly System.Windows.Media.SolidColorBrush DiscordGreen = new(System.Windows.Media.Color.FromRgb(0x23, 0xA5, 0x5A));

    void ShowDiscord(bool? muted, bool? deafened, bool sharing)
    {
        var vis = muted is null && !sharing || !discordRelevant ? Visibility.Collapsed : Visibility.Visible;
        DiscordShare.Foreground = sharing ? DiscordGreen : System.Windows.Media.Brushes.White;
        DiscordShare.Opacity = sharing ? 1 : 0.8;
        bool resized = DiscordPanel.Visibility != vis;
        DiscordPanel.Visibility = vis;

        // Discord shows the mic as muted while deafened, so do the same.
        bool micOff = muted == true || deafened == true;
        DiscordMicSlash.Visibility = micOff ? Visibility.Visible : Visibility.Collapsed;
        DiscordMic.Foreground = micOff ? DiscordMicSlash.Stroke : System.Windows.Media.Brushes.White;
        DiscordMic.Opacity = micOff ? 1 : 0.8;
        DiscordHeadsetSlash.Visibility = deafened == true ? Visibility.Visible : Visibility.Collapsed;
        DiscordHeadset.Foreground = deafened == true ? DiscordHeadsetSlash.Stroke : System.Windows.Media.Brushes.White;
        DiscordHeadset.Opacity = deafened == true ? 1 : 0.8;

        if (resized) RefreshCollapsedIcons();
    }

    // ---------------------------------------------------------------- media controls

    async void InitMediaSession()
    {
        try
        {
            mediaManager = await MediaManager.RequestAsync();
            mediaManager.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(HookMediaSession);
            _ = Dispatcher.BeginInvoke(HookMediaSession);
        }
        catch { }
    }

    void HookMediaSession()
    {
        if (mediaSession != null)
        {
            mediaSession.MediaPropertiesChanged -= OnMediaChanged;
            mediaSession.PlaybackInfoChanged -= OnMediaChanged;
        }
        try { mediaSession = mediaManager?.GetCurrentSession(); }
        catch { mediaSession = null; }
        if (mediaSession != null)
        {
            mediaSession.MediaPropertiesChanged += OnMediaChanged;
            mediaSession.PlaybackInfoChanged += OnMediaChanged;
        }
        UpdateMedia();
    }

    void OnMediaChanged(MediaSession s, object e) => Dispatcher.BeginInvoke(UpdateMedia);

    async void UpdateMedia()
    {
        var s = mediaSession;
        if (s == null) { MediaPanel.Visibility = Visibility.Collapsed; UpdateMiniPlayer(); return; }
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var info = s.GetPlaybackInfo();
            await Dispatcher.InvokeAsync(async () =>
            {
                if (s != mediaSession) return;
                MediaTitle.Text = string.IsNullOrEmpty(props.Title) ? AppName(s) : props.Title;
                MediaArtist.Text = string.IsNullOrEmpty(props.Artist) ? AppName(s) : props.Artist;
                MediaPanel.ToolTip = string.IsNullOrEmpty(props.Artist) ? MediaTitle.Text : $"{props.Title} — {props.Artist}";
                bool isPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                MediaPlayPause.Content = isPlaying ? "" : "";
                MediaPlayPause.ToolTip = isPlaying ? "Pause" : "Play";
                MediaPrev.IsEnabled = info.Controls.IsPreviousEnabled;
                MediaNext.IsEnabled = info.Controls.IsNextEnabled;
                MediaPanel.Visibility = Visibility.Visible;
                string miniText = string.IsNullOrEmpty(props.Artist) ? MediaTitle.Text : $"{MediaTitle.Text} · {props.Artist}";
                if (MiniTitle.Text != miniText) { MiniTitle.Text = miniText; StartMiniScroll(); }
                MiniPlayer.ToolTip = MediaPanel.ToolTip;
                MiniPlayPause.Content = MediaPlayPause.Content;
                MiniPlayPause.ToolTip = MediaPlayPause.ToolTip;
                MiniPrev.IsEnabled = MediaPrev.IsEnabled;
                MiniNext.IsEnabled = MediaNext.IsEnabled;
                mediaPlaying = isPlaying;
                if (isPlaying) mediaLastPlayed = DateTime.Now;
                UpdateMiniPlayer();

                // Only reload the cover when the track changes; it arrives as a stream each time.
                string key = props.Title + "\n" + props.Artist + "\n" + props.AlbumTitle;
                if (key != mediaArtKey)
                {
                    mediaArtKey = key;
                    MediaArt.Source = MiniArt.Source = props.Thumbnail == null ? null : await LoadThumbnail(props.Thumbnail);
                }
            });
        }
        catch { }
    }

    bool mediaPlaying;
    DateTime mediaLastPlayed;
    /// <summary>How long the closed island keeps the mini player after a pause, so it can be resumed from there.</summary>
    static readonly TimeSpan MiniPlayerLinger = TimeSpan.FromSeconds(60);

    /// <summary>The closed island's mini player shows while something plays and for a minute after it's paused.
    /// Also called from the one-second tick, which is what hides it once the minute is up.</summary>
    void UpdateMiniPlayer()
    {
        bool show = mediaSession != null && (mediaPlaying || DateTime.Now - mediaLastPlayed < MiniPlayerLinger);
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        // On a side edge there's only room across the pill for the cover.
        var parts = Vertical ? Visibility.Collapsed : Visibility.Visible;
        bool changed = MiniPlayer.Visibility != vis || MiniTitleBox.Visibility != parts;
        MiniPlayer.Visibility = vis;
        MiniTitleBox.Visibility = MiniPrev.Visibility = MiniPlayPause.Visibility = MiniNext.Visibility = parts;
        if (changed) StartMiniScroll();
        MiniPlayer.Orientation = Vertical ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;
        MiniPlayer.Margin = Vertical ? new Thickness(0, 8, 0, 0) : new Thickness(10, 0, 0, 0);
        if (changed || show) { RefreshCollapsedIcons(); Wake(); }
    }

    /// <summary>The length the mini player takes along the closed pill.</summary>
    double MiniPlayerLength(bool vertical)
    {
        if (MiniPlayer.Visibility != Visibility.Visible) return 0;
        if (vertical) return 8 + 20;
        return 10 + 20 + 6 + MiniTitleBox.Width + 3 + 3 * 22;
    }

    const double MiniScrollGap = 28, MiniScrollSpeed = 28; // px between the copies; px a second

    /// <summary>
    /// Scrolls the title round inside its box when it doesn't fit: a pause at the start, then along until the copy
    /// behind it is where it began, and again. A title that fits just sits still.
    /// </summary>
    void StartMiniScroll()
    {
        MiniTitleShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        MiniTitleShift.X = 0;
        MiniTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = MiniTitle.DesiredSize.Width, room = MiniTitleBox.Width - 12;
        bool scroll = w > room && MiniPlayer.Visibility == Visibility.Visible && MiniTitleBox.Visibility == Visibility.Visible;
        MiniTitleCopy.Text = MiniTitle.Text;
        MiniTitleCopy.Margin = new Thickness(MiniScrollGap, 0, 0, 0);
        MiniTitleCopy.Visibility = scroll ? Visibility.Visible : Visibility.Collapsed;
        if (!scroll) return;

        double distance = w + MiniScrollGap;
        var hold = TimeSpan.FromSeconds(2);
        var run = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = hold + TimeSpan.FromSeconds(distance / MiniScrollSpeed),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        run.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        run.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(hold)));
        run.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(-distance, System.Windows.Media.Animation.KeyTime.FromTimeSpan(run.Duration.TimeSpan)));
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(run, 30);
        MiniTitleShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, run);
    }

    /// <summary>The cover or title: open the island into the player's tab, as the wave does.</summary>
    void MiniPlayer_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => CollapsedWave_Click(sender, e);

    static string AppName(MediaSession s)
    {
        var id = s.SourceAppUserModelId ?? "";
        // Desktop apps report their exe ("Spotify.exe"), Store apps a package id ("Microsoft.ZuneMusic_8wekyb3d8bbwe!App").
        id = id.Split('!')[0].Split('_')[0];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        return id.Split('.').Last();
    }

    static async System.Threading.Tasks.Task<BitmapImage?> LoadThumbnail(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var source = stream.AsStreamForRead();
            var copy = new MemoryStream();
            await source.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 48;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    async void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        try { if (mediaSession != null) await mediaSession.TryTogglePlayPauseAsync(); } catch { }
    }

    async void MediaPrev_Click(object sender, RoutedEventArgs e)
    {
        try { if (mediaSession != null) await mediaSession.TrySkipPreviousAsync(); } catch { }
    }

    async void MediaNext_Click(object sender, RoutedEventArgs e)
    {
        try { if (mediaSession != null) await mediaSession.TrySkipNextAsync(); } catch { }
    }
}
