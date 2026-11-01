using System;
using System.Runtime.InteropServices;
using static IslandDock.Native;

namespace IslandDock;

/// <summary>
/// Hides the Windows taskbar while the island stands in for it. Hiding its windows alone would leave their strip of
/// the screen reserved, so the taskbar is also set to auto-hide, which gives that space back; the user's own
/// auto-hide choice is put back when it's shown again.
/// </summary>
internal static class TaskbarHider
{
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);

    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

    static APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null) };

    public static bool AutoHide
    {
        get { var d = Data(); return ((long)SHAppBarMessage(ABM_GETSTATE, ref d) & ABS_AUTOHIDE) != 0; }
        set { var d = Data(); d.lParam = (IntPtr)(ABS_ALWAYSONTOP | (value ? ABS_AUTOHIDE : 0)); SHAppBarMessage(ABM_SETSTATE, ref d); }
    }

    /// <summary>Shows or hides the main taskbar and those on other monitors. Cheap to repeat; Explorer brings it back
    /// when it restarts.</summary>
    public static void SetVisible(bool visible)
    {
        EnumWindows((h, _) =>
        {
            if (WindowUtil.GetClass(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && IsWindowVisible(h) != visible)
                ShowWindow(h, visible ? SW_SHOWNA : SW_HIDE);
            return true;
        }, IntPtr.Zero);
    }
}
