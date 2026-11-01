using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IslandDock;

/// <summary>Master volume of the default playback device, via the Core Audio endpoint API.</summary>
internal static class CoreAudio
{
    static IAudioEndpointVolume? endpoint;
    static IAudioSessionManager2? sessions;

    /// <summary>Re-resolves the default device (it changes when headphones etc. are plugged in).</summary>
    public static void Refresh()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
            endpoint = (IAudioEndpointVolume)obj;
            iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 23, IntPtr.Zero, out obj);
            sessions = (IAudioSessionManager2)obj;
        }
        catch { endpoint = null; sessions = null; }
    }

    /// <summary>A playback session on the default device. Pid 0 is the "System sounds" session.</summary>
    public sealed record Session(uint ProcessId, bool IsSystemSounds, ISimpleAudioVolume Volume, IAudioMeterInformation? Meter);

    /// <summary>Current peak sample (0–1) of a session's output, after its volume is applied.</summary>
    public static float Peak(IAudioMeterInformation meter)
    {
        try { meter.GetPeakValue(out var p); return p; }
        catch { return 0; }
    }

    /// <summary>Live (not expired) playback sessions on the default device.</summary>
    public static List<Session> GetSessions()
    {
        var result = new List<Session>();
        try
        {
            if (sessions == null) return result;
            sessions.GetSessionEnumerator(out var list);
            list.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                list.GetSession(i, out var control);
                if (control is not IAudioSessionControl2 c2 || control is not ISimpleAudioVolume volume) continue;
                c2.GetState(out int state);
                if (state == 2 /* AudioSessionStateExpired */) continue;
                c2.GetProcessId(out uint pid);
                result.Add(new Session(pid, c2.IsSystemSoundsSession() == 0, volume, control as IAudioMeterInformation));
            }
        }
        catch { }
        return result;
    }

    public static float? Volume
    {
        get
        {
            try { if (endpoint == null) return null; endpoint.GetMasterVolumeLevelScalar(out var v); return v; }
            catch { return null; }
        }
        set
        {
            try { if (endpoint != null && value is float v) { var ctx = Guid.Empty; endpoint.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), ref ctx); } }
            catch { }
        }
    }

    public static bool? Muted
    {
        get
        {
            try { if (endpoint == null) return null; endpoint.GetMute(out var m); return m; }
            catch { return null; }
        }
        set
        {
            try { if (endpoint != null && value is bool m) { var ctx = Guid.Empty; endpoint.SetMute(m, ref ctx); } }
            catch { }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float db, ref Guid ctx);
        void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        void GetMasterVolumeLevel(out float db);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float db, ref Guid ctx);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        void GetChannelVolumeLevel(uint channel, out float db);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        void GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out int state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        void GetGroupingParam(out Guid group);
        void SetGroupingParam(ref Guid group, ref Guid ctx);
        void RegisterAudioSessionNotification(IntPtr client);
        void UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid ctx);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Session controls also implement this, which is what the Windows mixer's level bars read.
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
}
