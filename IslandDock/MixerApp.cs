using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace IslandDock;

/// <summary>One row of the volume mixer: every playback session of one process (or System sounds).</summary>
internal sealed class MixerApp : INotifyPropertyChanged
{
    double volume;
    bool muted;

    public uint ProcessId { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public List<CoreAudio.ISimpleAudioVolume> Sessions { get; set; } = new();

    public MixerApp(uint pid, bool systemSounds)
    {
        ProcessId = pid;
        if (systemSounds)
        {
            Name = "System sounds";
            return;
        }
        var path = WindowUtil.GetProcessPath(pid);
        Icon = WindowUtil.GetExeIcon(path);
        string? name = null;
        try { if (path != null) name = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
        if (string.IsNullOrWhiteSpace(name))
        {
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
        }
        Name = string.IsNullOrWhiteSpace(name) ? $"Process {pid}" : name!;
    }

    /// <summary>0–100. Setting it applies to all of the app's sessions and unmutes them.</summary>
    public double Volume
    {
        get => volume;
        set
        {
            if (Math.Abs(volume - value) < 0.01) return;
            volume = value;
            var ctx = Guid.Empty;
            foreach (var s in Sessions)
            {
                try
                {
                    s.SetMasterVolume((float)(value / 100), ref ctx);
                    if (muted) s.SetMute(false, ref ctx);
                }
                catch { }
            }
            if (muted) { muted = false; OnChanged(nameof(Muted)); OnChanged(nameof(Glyph)); }
            OnChanged();
            OnChanged(nameof(Glyph));
        }
    }

    public bool Muted
    {
        get => muted;
        set
        {
            if (muted == value) return;
            muted = value;
            var ctx = Guid.Empty;
            foreach (var s in Sessions) { try { s.SetMute(value, ref ctx); } catch { } }
            OnChanged();
            OnChanged(nameof(Glyph));
        }
    }

    public string Glyph => muted || volume <= 0 ? "" : "";

    /// <summary>Re-reads volume and mute from the first session, without writing anything back.</summary>
    public void Pull()
    {
        if (Sessions.Count == 0) return;
        try
        {
            Sessions[0].GetMasterVolume(out float v);
            Sessions[0].GetMute(out bool m);
            double nv = Math.Round(v * 100);
            if (Math.Abs(nv - volume) >= 1) { volume = nv; OnChanged(nameof(Volume)); }
            if (m != muted) { muted = m; OnChanged(nameof(Muted)); }
            OnChanged(nameof(Glyph));
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
