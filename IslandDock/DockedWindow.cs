using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using static IslandDock.Native;

namespace IslandDock;

/// <summary>A foreign top-level window that has been re-parented into the island's host window.</summary>
internal sealed class DockedWindow : INotifyPropertyChanged
{
    const long StripStyle = WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
    const long StripExStyle = WS_EX_APPWINDOW | WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE;

    string title = "";
    bool isActive;
    long origStyle, origExStyle;
    RECT origRect;
    bool wasMaximized;

    public IntPtr Handle { get; }
    public ImageSource? Icon { get; }
    public bool IsDocked => true;

    /// <summary>Size of the expanded island while this window is the active tab, relative to the default.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>
    /// True when Windows refused to re-parent the window (UWP frames such as Settings, Calculator), so it
    /// stays top-level and is kept positioned over the island's content area instead.
    /// </summary>
    public bool Floating { get; private set; }

    public string Title
    {
        get => title;
        set { if (title != value) { title = value; OnChanged(); } }
    }

    public bool IsActive
    {
        get => isActive;
        set { if (isActive != value) { isActive = value; OnChanged(); } }
    }

    /// <summary>Lower-case process name, used to remember per-app settings.</summary>
    public string App { get; }

    /// <summary>
    /// Apps that accept any window size but keep drawing their UI at a minimum (in DIPs), so it gets cropped.
    /// Windows can't tell us this, unlike a normal minimum size, which <see cref="Placed"/> reveals.
    /// </summary>
    static readonly System.Collections.Generic.Dictionary<string, System.Windows.Size> CroppingMinimums = new()
    {
        ["spotify"] = new(800, 600),
    };

    public System.Windows.Size? KnownMinimum => CroppingMinimums.TryGetValue(App, out var s) ? s : null;

    public uint ProcessId { get; }

    double level;
    bool playing;

    /// <summary>Smoothed loudness (0–1) of the app's audio, for the tab's wave.</summary>
    public double Level
    {
        get => level;
        private set { if (level != value) { level = value; OnChanged(); } }
    }

    /// <summary>The app has made sound in the last moment; held briefly so the wave doesn't flicker between words.</summary>
    public bool Playing
    {
        get => playing;
        private set { if (playing != value) { playing = value; OnChanged(); } }
    }

    long lastSound;

    public void UpdateLevel(float peak) => (Level, Playing) = Smooth(peak, level, ref lastSound);

    /// <summary>Shared by tabs and the collapsed pill: fast attack, slow release, and a hold before "not playing".</summary>
    public static (double level, bool playing) Smooth(float peak, double previous, ref long lastSound)
    {
        double target = Math.Sqrt(peak); // closer to perceived loudness than the raw amplitude
        double next = Math.Max(target, previous * 0.82);
        if (next < 0.01) next = 0;
        long now = Environment.TickCount64;
        if (peak > 0.003) lastSound = now;
        return (next, now - lastSound < 1500);
    }

    public DockedWindow(IntPtr handle)
    {
        Handle = handle;
        GetWindowThreadProcessId(handle, out uint pid);
        ProcessId = pid;
        App = WindowUtil.GetProcessName(handle).ToLowerInvariant();
        Icon = WindowUtil.GetIcon(handle);
        Refresh();
    }

    public void Refresh() => Title = WindowUtil.GetTitle(Handle);

    public bool Dock(IntPtr host, RECT? restoreRect)
    {
        var h = Handle;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        wasMaximized = IsZoomed(h);
        if (wasMaximized) ShowWindow(h, SW_RESTORE);

        if (restoreRect is RECT rr) origRect = rr;
        else GetWindowRect(h, out origRect);

        origStyle = GetLong(h, GWL_STYLE);
        origExStyle = GetLong(h, GWL_EXSTYLE);

        // Per SetParent docs: switch to WS_CHILD before re-parenting a top-level window.
        long style = (origStyle & ~StripStyle & ~WS_VISIBLE) | WS_CHILD | (origStyle & WS_VISIBLE);
        SetLong(h, GWL_STYLE, style);
        SetLong(h, GWL_EXSTYLE, origExStyle & ~StripExStyle);

        SetParent(h, host);
        if (GetParent(h) != host)
        {
            SetLong(h, GWL_STYLE, origStyle);
            SetLong(h, GWL_EXSTYLE, origExStyle);
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);

            // Fall back to floating. This still fails under UIPI, when the target runs elevated and we don't.
            if (!SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)) return false;
            Floating = true;
        }

        Taskbar.Remove(h);
        if (!Floating)
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
        return true;
    }

    /// <summary>Hands the window to another island's host. It's still docked, and still remembers how it looked before.</summary>
    public bool MoveTo(IntPtr host)
    {
        if (Floating) return true;
        SetParent(Handle, host);
        return GetParent(Handle) == host;
    }

    /// <summary>For a floating window, the screen rect it occupies; for a docked child, ignored in favour of the host's client area.</summary>
    public void Place(RECT screen, RECT hostClient)
    {
        if (Floating)
            SetWindowPos(Handle, HWND_TOPMOST, screen.Left, screen.Top, screen.Width, screen.Height, SWP_NOACTIVATE);
        else
        {
            // Apps minimize or maximize themselves even while docked (Discord does when it's brought back for a
            // screen share). Sizing a minimized window only changes its restore size, and it then comes back at its
            // old full-screen size, cut off on the right and bottom.
            if (IsIconic(Handle) || IsZoomed(Handle)) ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, hostClient.Width, hostClient.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        // Where it actually ended up: apps with a minimum size won't shrink all the way, and that's fine.
        GetWindowRect(Handle, out placed);
    }

    /// <summary>
    /// Chromium apps (Discord) sometimes keep drawing at the size they had while hidden, so the window is the right
    /// size but its content is cropped. Resizing it and back makes them lay out again, but only if the smaller size
    /// lasts a few frames: an immediate resize back is coalesced away.
    /// </summary>
    public async Task Nudge(Func<RECT?> hostClient)
    {
        if (Floating || !IsWindow(Handle) || hostClient() is not RECT r) return;
        // Briefly taller rather than shorter: the host clips the extra, so no gap shows at the bottom (that flickered).
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, r.Width + 2, r.Height + 2, SWP_NOZORDER | SWP_NOACTIVATE);
        // Counted as placed, or the island's misplaced-window check puts it straight back the next frame and the
        // app never sees the resize (why the nudge did nothing).
        GetWindowRect(Handle, out placed);
        await Task.Delay(150);
        if (IsWindow(Handle) && hostClient() is RECT now)
        {
            Place(now, now);
            // Clicking into the window also fixes the cropping; activating it is what the click does for it.
            PostMessage(Handle, WM_ACTIVATE, (IntPtr)1 /* WA_ACTIVE */, IntPtr.Zero);
        }
    }

    RECT placed;

    /// <summary>Where the last <see cref="Place"/> actually put the window, in screen pixels.</summary>
    public RECT Placed => placed;

    /// <summary>Smallest island scale at which the window fits without being cropped (learned from its minimum size).</summary>
    public double FitScale { get; set; }

    /// <summary>
    /// The window moved or resized itself away from where it was placed, e.g. an app going "fullscreen"
    /// sizes itself to the monitor, which as a child lands offset inside the island.
    /// </summary>
    public bool IsMisplaced() => GetWindowRect(Handle, out var r) && !r.Equals(placed);

    RECT insisted;
    long insistedAt;

    /// <summary>
    /// Some apps put themselves back to a size of their own choosing after every resize. Once one moves to the same
    /// rect twice in quick succession, leave it there, or the two keep fighting every frame and the window flickers.
    /// </summary>
    public bool Insists()
    {
        GetWindowRect(Handle, out var r);
        long now = Environment.TickCount64;
        if (r.Equals(insisted) && now - insistedAt < 1000) { placed = r; return true; }
        (insisted, insistedAt) = (r, now);
        return false;
    }

    /// <summary>The window can't be kept in the island any more (closed, or re-parented by its owner).</summary>
    public bool IsLost(IntPtr host) => !IsWindow(Handle) || (!Floating && GetParent(Handle) != host);

    /// <summary>Lets a floating window go where the user dragged it.</summary>
    public void Release()
    {
        if (!IsWindow(Handle)) return;
        SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Taskbar.Add(Handle);
    }

    public void Undock() => Restore(show: true);

    /// <summary>
    /// Gives the window back to the desktop still hidden and asks it to close, as its own close button would.
    /// Apps that close to the tray (Discord, Spotify) then stay where they'd normally be, not stuck in the island.
    /// </summary>
    public void Close()
    {
        Restore(show: false);
        if (IsWindow(Handle)) PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    void Restore(bool show)
    {
        var h = Handle;
        if (!IsWindow(h)) return;
        uint visible = show ? SWP_SHOWWINDOW : 0;

        if (Floating)
        {
            SetWindowPos(h, HWND_NOTOPMOST, origRect.Left, origRect.Top, origRect.Width, origRect.Height, visible);
            Taskbar.Add(h);
            if (!show) return;
            if (wasMaximized) ShowWindow(h, SW_MAXIMIZE);
            SetForegroundWindow(h);
            return;
        }

        SetLong(h, GWL_STYLE, origStyle & ~WS_VISIBLE);
        SetLong(h, GWL_EXSTYLE, origExStyle);
        SetParent(h, IntPtr.Zero);
        SetWindowPos(h, IntPtr.Zero, origRect.Left, origRect.Top, origRect.Width, origRect.Height,
            SWP_NOZORDER | SWP_FRAMECHANGED | visible);
        Taskbar.Add(h);
        if (!show) return;
        if (wasMaximized) ShowWindow(h, SW_MAXIMIZE);
        SetForegroundWindow(h);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
