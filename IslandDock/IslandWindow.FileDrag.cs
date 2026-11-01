using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static IslandDock.Native;

namespace IslandDock;

public partial class IslandWindow
{
    /// <summary>Wide enough that the mouse stays on it between frames, in pixels.</summary>
    const int DragProbeSize = 96;

    Window? dragProbe;
    bool pressedInside;

    /// <summary>
    /// A file dragged from a docked app out past the island closes it, so the place it's going can be seen. Windows
    /// doesn't say when another app is dragging something, and holding the button past the edge can just as well be
    /// selecting text. So once the mouse is out there, a nearly clear window follows it: only a real drag reports
    /// itself to a window it passes over, and anything else never notices.
    /// </summary>
    void WatchFileDragOut(POINT p, bool mouseDown, bool over, Rect slop)
    {
        if (!mouseDown) pressedInside = over;

        bool watching = mouseDown && pressedInside && expanded && !pinned && !dragging && !slop.Contains(p.X, p.Y)
                        && dropCandidate == IntPtr.Zero && active is { Floating: false };
        if (!watching)
        {
            dragProbe?.Hide();
            return;
        }
        dragProbe ??= MakeDragProbe();
        if (!dragProbe.IsVisible) dragProbe.Show();
        SetWindowPos(new WindowInteropHelper(dragProbe).Handle, HWND_TOPMOST, p.X - DragProbeSize / 2, p.Y - DragProbeSize / 2,
            DragProbeSize, DragProbeSize, SWP_NOACTIVATE);
    }

    Window MakeDragProbe()
    {
        var probe = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            // Fully clear would let the mouse, and so the drag, straight through.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            AllowDrop = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, Width = 1, Height = 1,
        };
        probe.DragEnter += OnDragProbe;
        probe.DragOver += OnDragProbe;
        var h = new WindowInteropHelper(probe).EnsureHandle();
        SetLong(h, GWL_EXSTYLE, GetLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        return probe;
    }

    void OnDragProbe(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        closeRequested = true;
    }
}
