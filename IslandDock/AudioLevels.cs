using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace IslandDock;

/// <summary>
/// How loud each media player is right now. Only apps playing through the system media controls count (music,
/// video), so a game or a Discord call doesn't light up the wave. Browsers and Electron apps (Chrome, Spotify) play
/// audio from a child process, so a window's level includes its process's descendants.
/// </summary>
internal sealed class AudioLevels
{
    List<CoreAudio.Session> sessions = new();
    Dictionary<uint, uint> parents = new();
    readonly Dictionary<uint, float> peaks = new();

    /// <summary>Loudest player at the last <see cref="Sample"/>.</summary>
    public float Overall { get; private set; }

    /// <summary>
    /// Re-reads the session list and process tree; cheap enough for once a second, not every sample. A player is
    /// named by its media-controls id: an exe for desktop apps ("Spotify.exe", "MSEdge"), an AUMID otherwise, which
    /// <paramref name="windowPids"/> resolves through the windows carrying it.
    /// </summary>
    public void Refresh(IReadOnlyCollection<string> playerIds, Func<string, IEnumerable<uint>> windowPids)
    {
        var (map, names) = ProcessTree();
        parents = map;
        var players = new HashSet<uint>();
        foreach (var id in playerIds)
        {
            string exe = id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;
            foreach (var (pid, name) in names)
                if (Path.GetFileNameWithoutExtension(name).Equals(exe, StringComparison.OrdinalIgnoreCase)) players.Add(pid);
            players.UnionWith(windowPids(id));
        }
        sessions = CoreAudio.GetSessions().Where(s => !s.IsSystemSounds && players.Any(p => IsSelfOrDescendant(s.ProcessId, p))).ToList();
    }

    public void Sample()
    {
        peaks.Clear();
        float overall = 0;
        foreach (var s in sessions)
        {
            if (s.Meter is null) continue;
            float p = CoreAudio.Peak(s.Meter);
            overall = Math.Max(overall, p);
            peaks[s.ProcessId] = Math.Max(peaks.GetValueOrDefault(s.ProcessId), p);
        }
        Overall = overall;
    }

    public float For(uint pid)
    {
        float best = 0;
        foreach (var (spid, p) in peaks)
            if (p > best && IsSelfOrDescendant(spid, pid)) best = p;
        return best;
    }

    bool IsSelfOrDescendant(uint child, uint ancestor)
    {
        // Bounded walk: a reused pid can make the parent chain loop.
        for (int i = 0; i < 16 && child != 0; i++)
        {
            if (child == ancestor) return true;
            if (!parents.TryGetValue(child, out child)) return false;
        }
        return false;
    }

    /// <summary>Each process's parent and exe name.</summary>
    static (Dictionary<uint, uint>, Dictionary<uint, string>) ProcessTree()
    {
        var map = new Dictionary<uint, uint>();
        var names = new Dictionary<uint, string>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return (map, names);
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
            {
                map[e.th32ProcessID] = e.th32ParentProcessID;
                names[e.th32ProcessID] = e.szExeFile;
            }
        }
        finally { CloseHandle(snap); }
        return (map, names);
    }

    const uint TH32CS_SNAPPROCESS = 0x2;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
