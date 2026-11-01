using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static IslandDock.Native;

namespace IslandDock;

internal sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, ImageSource? Icon);

internal static class WindowUtil
{
    static readonly HashSet<string> IgnoredClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow"
    };

    public static string GetTitle(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint GetPid(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        return pid;
    }

    static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int c, 4) == 0 && c != 0;

    /// <summary>A normal, visible, top-level app window that belongs to another process.</summary>
    public static bool IsCandidate(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindowVisible(h)) return false;
        if (GetAncestor(h, 2 /* GA_ROOT */) != h || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
        if (GetPid(h) == (uint)Environment.ProcessId) return false;
        if ((GetLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetTitle(h).Length == 0 || IsCloaked(h)) return false;
        return !IgnoredClasses.Contains(GetClass(h));
    }

    public static List<IntPtr> CandidateHandles(ISet<IntPtr> exclude)
    {
        var handles = new List<IntPtr>();
        EnumWindows((h, _) => { if (!exclude.Contains(h) && IsCandidate(h)) handles.Add(h); return true; }, IntPtr.Zero);
        return handles;
    }

    /// <summary>
    /// Covers its whole monitor like a borderless or exclusive-fullscreen game. Docking one would shrink it into the
    /// island, so these are only ever switched to. A maximized window with a title bar only overhangs its work area.
    /// </summary>
    public static bool IsFullscreen(IntPtr h)
    {
        RECT r;
        if (IsIconic(h))
        {
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(h, ref wp)) return false;
            r = wp.rcNormalPosition;
        }
        else if (!GetWindowRect(h, out r)) return false;
        long style = GetLong(h, GWL_STYLE);
        if (IsZoomed(h) && (style & WS_CAPTION) == WS_CAPTION) return false;
        // An app in its own full-screen mode (Discord's covers the taskbar without being maximized) keeps its resizable
        // frame; a game's full-screen window is a bare popup.
        if ((style & WS_THICKFRAME) != 0) return false;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */), ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public static List<WindowInfo> EnumerateCandidates(ISet<IntPtr> exclude)
    {
        var handles = CandidateHandles(exclude);

        var result = new List<WindowInfo>();
        foreach (var h in handles)
            result.Add(new WindowInfo(h, GetTitle(h), GetProcessName(h), GetIcon(h)));
        return result;
    }

    /// <summary>The Settings app's frame: an ApplicationFrameWindow hosting a SystemSettings content window.</summary>
    public static IntPtr FindSettingsWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (GetClass(h) != "ApplicationFrameWindow") return true;
            EnumChildWindows(h, (c, _) =>
            {
                if (GetClass(c) == "Windows.UI.Core.CoreWindow" && GetProcessName(c) == "SystemSettings") found = h;
                return found == IntPtr.Zero;
            }, IntPtr.Zero);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// True while a game or fullscreen video owns the screen: Windows reports exclusive/busy fullscreen,
    /// or the foreground window (borderless games, browser fullscreen video) covers its whole monitor.
    /// Unless <paramref name="anyDisplay"/>, only when that's on <paramref name="self"/>'s display.
    /// </summary>
    public static bool IsImmersive(IntPtr self, bool anyDisplay = true)
    {
        var fg = GetForegroundWindow();
        if (!anyDisplay && (fg == IntPtr.Zero || MonitorFromWindow(fg, 2) != MonitorFromWindow(self, 2))) return false;
        if (SHQueryUserNotificationState(out int state) == 0
            && state is 2 /* QUNS_BUSY */ or 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */ or 4 /* QUNS_PRESENTATION_MODE */)
            return true;

        if (fg == IntPtr.Zero || fg == self || GetPid(fg) == (uint)Environment.ProcessId) return false;
        if (IgnoredClasses.Contains(GetClass(fg)) || !GetWindowRect(fg, out var r)) return false;
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */), ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public static string GetProcessName(IntPtr h)
    {
        try { using var p = Process.GetProcessById((int)GetPid(h)); return p.ProcessName; }
        catch { return ""; }
    }

    public static string? GetProcessPath(uint pid)
    {
        var hp = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(hp); }
    }

    public static ImageSource? GetIcon(IntPtr h)
    {
        // Store apps (Settings, Calculator…) sit in an ApplicationFrameHost window with a blank icon; ask the shell instead.
        if (GetClass(h) == "ApplicationFrameWindow" && ShellIcon.ForWindow(h) is ImageSource store) return store;

        IntPtr hIcon = IntPtr.Zero;
        // The big (32px) icon first: scaled down it stays crisp, where the 16px one blurs on a high-DPI screen.
        foreach (int type in new[] { 1 /* ICON_BIG */, 2 /* ICON_SMALL2 */, 0 /* ICON_SMALL */ })
        {
            if (SendMessageTimeout(h, 0x7F /* WM_GETICON */, (IntPtr)type, IntPtr.Zero, 2 /* SMTO_ABORTIFHUNG */, 100, out hIcon) != IntPtr.Zero
                && hIcon != IntPtr.Zero) break;
            hIcon = IntPtr.Zero;
        }
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(h, -14 /* GCLP_HICON */);
        if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(h, -34 /* GCLP_HICONSM */);

        if (hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { }
        }

        // Fall back to the executable's icon (UWP/WinUI windows often don't expose one).
        return GetExeIcon(GetProcessPath(GetPid(h)));
    }

    public static ImageSource? GetExeIcon(string? path)
    {
        if (path == null) return null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
