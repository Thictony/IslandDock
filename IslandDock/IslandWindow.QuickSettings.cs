using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace IslandDock;

/// <summary>A toggle whose checked state is only ever set from code, to mirror real system state.</summary>
public sealed class StateButton : ToggleButton
{
    protected override void OnToggle() { }
}

// The navbar that mirrors Windows 11 Quick Settings.
public partial class IslandWindow
{
    Radio? wifiRadio, bluetoothRadio;
    bool updatingVolume;
    DispatcherTimer? settingsWait;
    readonly System.Collections.Generic.List<MixerApp> mixerApps = new();

    async void InitQuickSettings()
    {
        CoreAudio.Refresh();
        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (updatingVolume) return;
            CoreAudio.Volume = (float)(e.NewValue / 100);
            if (CoreAudio.Muted == true) CoreAudio.Muted = false;
            UpdateVolumeGlyph();
        };

        try
        {
            if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
            {
                foreach (var r in await Radio.GetRadiosAsync())
                {
                    if (r.Kind == RadioKind.WiFi) wifiRadio ??= r;
                    else if (r.Kind == RadioKind.Bluetooth) bluetoothRadio ??= r;
                }
                foreach (var r in new[] { wifiRadio, bluetoothRadio })
                    if (r != null) r.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateQuickSettings);
            }
        }
        catch { }
        // WinRT awaits can resume on a thread-pool thread, so hop back to the UI thread.
        _ = Dispatcher.BeginInvoke(UpdateQuickSettings);
    }

    void UpdateQuickSettings()
    {
        bool wifiOn = wifiRadio?.State == RadioState.On;
        WifiToggle.IsChecked = WifiMore.IsChecked = wifiOn;
        WifiToggle.ToolTip = wifiRadio == null ? "Wi-Fi (no adapter found)" : wifiOn ? "Wi-Fi: on" : "Wi-Fi: off";

        bool btOn = bluetoothRadio?.State == RadioState.On;
        BluetoothToggle.IsChecked = BluetoothMore.IsChecked = btOn;
        BluetoothToggle.ToolTip = bluetoothRadio == null ? "Bluetooth (no adapter found)" : btOn ? "Bluetooth: on" : "Bluetooth: off";

        if (!VolumeSlider.IsMouseCaptureWithin && CoreAudio.Volume is float v)
        {
            updatingVolume = true;
            VolumeSlider.Value = Math.Round(v * 100);
            updatingVolume = false;
        }
        UpdateVolumeGlyph();
    }

    void UpdateVolumeGlyph()
    {
        bool muted = CoreAudio.Muted == true || VolumeSlider.Value <= 0;
        MuteButton.Content = muted ? "" : "";
        MuteButton.ToolTip = $"Volume {Math.Round(VolumeSlider.Value)}%{(muted ? " (muted)" : "")}";
    }

    async void ToggleRadio(Radio? radio, string settingsPage)
    {
        if (radio == null) { OpenSettings(settingsPage); return; }
        var target = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
        try
        {
            if (await radio.SetStateAsync(target) != RadioAccessStatus.Allowed) OpenSettings(settingsPage);
        }
        catch { OpenSettings(settingsPage); }
        _ = Dispatcher.BeginInvoke(UpdateQuickSettings);
    }

    /// <summary>Opens a Settings page and docks the Settings window into the island as the active tab.</summary>
    void OpenSettings(string page)
    {
        try { Process.Start(new ProcessStartInfo($"ms-settings:{page}") { UseShellExecute = true }); }
        catch { return; }

        // Settings opens (or navigates) asynchronously, so watch for its window for a few seconds.
        settingsWait?.Stop();
        var until = DateTime.UtcNow.AddSeconds(8);
        settingsWait = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) =>
        {
            var h = WindowUtil.FindSettingsWindow();
            if (h != IntPtr.Zero && (docked.Any(d => d.Handle == h) || WindowUtil.IsCandidate(h)))
            {
                settingsWait!.Stop();
                hostRect = default; // Windows may have moved it while navigating; re-place it.
                DockWindow(h, null);
            }
            else if (DateTime.UtcNow > until) settingsWait!.Stop();
        }, Dispatcher);
    }

    void Wifi_Click(object sender, RoutedEventArgs e) => ToggleRadio(wifiRadio, "network-wifi");
    void WifiMore_Click(object sender, RoutedEventArgs e) => OpenSettings("network-wifi");
    void Bluetooth_Click(object sender, RoutedEventArgs e) => ToggleRadio(bluetoothRadio, "bluetooth");
    void BluetoothMore_Click(object sender, RoutedEventArgs e) => OpenSettings("bluetooth");
    void SoundOutput_Click(object sender, RoutedEventArgs e)
    {
        mixerOpen = !mixerOpen;
        pickerOpen = searchOpen = trayOpen = spotsOpen = updateOpen = false;
        if (mixerOpen) { mixerApps.Clear(); RefreshMixer(); }
    }

    void SoundSettings_Click(object sender, RoutedEventArgs e)
    {
        mixerOpen = false;
        OpenSettings("sound");
    }

    void MixerMute_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is MixerApp app) app.Muted = !app.Muted;
    }

    /// <summary>Syncs the mixer rows with the live audio sessions, keeping existing rows so a dragged slider isn't rebuilt.</summary>
    void RefreshMixer()
    {
        var groups = CoreAudio.GetSessions()
            .GroupBy(s => s.IsSystemSounds ? 0u : s.ProcessId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Volume).ToList());

        mixerApps.RemoveAll(a => !groups.ContainsKey(a.ProcessId));
        foreach (var (pid, sessions) in groups)
        {
            var app = mixerApps.FirstOrDefault(a => a.ProcessId == pid);
            if (app == null) mixerApps.Add(app = new MixerApp(pid, pid == 0));
            app.Sessions = sessions;
            if (!MixerList.IsMouseCaptureWithin) app.Pull();
        }
        // System sounds last, like the Windows mixer lists apps first.
        var sorted = mixerApps.OrderBy(a => a.ProcessId == 0).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        mixerApps.Clear();
        mixerApps.AddRange(sorted);

        var ids = mixerApps.Select(a => a.ProcessId).ToList();
        if (MixerList.ItemsSource is not System.Collections.Generic.List<MixerApp> shown || !shown.Select(a => a.ProcessId).SequenceEqual(ids))
            MixerList.ItemsSource = mixerApps.ToList();
        MixerEmpty.Visibility = mixerApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings("");

    /// <summary>The power menu: sign out, restart or shut down, straight away like Start's (apps can still ask to save).</summary>
    void Power_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        void Add(string header, string args)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false }); }
                catch (Exception ex) { ShowStatus("Couldn't " + header.ToLowerInvariant() + ": " + ex.Message); }
            };
            m.Items.Add(item);
        }
        Add("Sign out", "/l");
        m.Items.Add(new Separator());
        Add("Restart", "/r /t 0");
        Add("Shut down", "/s /t 0");
        Dress(m);
        m.PlacementTarget = (UIElement)sender;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        CoreAudio.Muted = !(CoreAudio.Muted ?? false);
        UpdateVolumeGlyph();
    }
}
