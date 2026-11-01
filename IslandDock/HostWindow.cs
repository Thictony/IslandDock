using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static IslandDock.Native;

namespace IslandDock;

/// <summary>
/// Opaque window that sits over the island's content area and parents the docked windows.
/// It is separate from the island because child HWNDs don't render inside a transparent (layered) WPF window.
/// </summary>
internal sealed class HostWindow : Window
{
    public IntPtr Handle { get; private set; }
    public bool AllowClose { get; set; }

    public HostWindow()
    {
        Title = "IslandDock Host";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000; Width = 100; Height = 100;
        Closing += (_, e) => e.Cancel = !AllowClose;
    }

    /// <summary>The docked window that should have the keyboard while the host is active.</summary>
    public IntPtr FocusTarget { get; set; }

    public void Init(Window owner)
    {
        Owner = owner;
        Handle = new WindowInteropHelper(this).EnsureHandle();
        SetLong(Handle, GWL_STYLE, GetLong(Handle, GWL_STYLE) | WS_CLIPCHILDREN);
        SetLong(Handle, GWL_EXSTYLE, GetLong(Handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        // Windows rounds the corners itself, at whatever size the window really is. A window region did this before,
        // but it could stay at an older, smaller size and cut the docked app off until it was clicked.
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        HwndSource.FromHwnd(Handle).AddHook(WndProc);
    }

    /// <summary>
    /// Clicking a docked window activates the host (its top-level parent), and WPF then hands the keyboard back to
    /// its own (empty) window, so the docked app never sees a key. Pass focus on to the docked window instead.
    /// </summary>
    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETFOCUS = 0x7, WM_ACTIVATE = 0x6;
        var target = FocusTarget;
        if (target == IntPtr.Zero || !IsWindow(target) || GetParent(target) != Handle) return IntPtr.Zero;

        if (msg == WM_SETFOCUS || (msg == WM_ACTIVATE && (wParam.ToInt64() & 0xFFFF) != 0))
        {
            PassFocus(target);
            handled = true;
        }
        else if (msg == WM_ACTIVATE)
            PostMessage(target, WM_ACTIVATE, IntPtr.Zero /* WA_INACTIVE */, IntPtr.Zero);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Gives the docked window the keyboard, and tells it it's the active window again. As a child it never gets
    /// WM_ACTIVATE from Windows, and Chromium apps (Opera) that last heard they were inactive (after one of their
    /// own popups, say) drop every key, however often they're clicked.
    /// </summary>
    public void PassFocus(IntPtr target)
    {
        // Keep focus on whichever control inside the app the click landed on.
        var focus = GetFocus();
        if (focus == IntPtr.Zero || focus == Handle || !IsChild(target, focus) && focus != target) SetFocus(target);
        PostMessage(target, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        PostMessage(target, WM_ACTIVATE, (IntPtr)1 /* WA_ACTIVE */, IntPtr.Zero);
    }
}
