using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace IslandDock;

/// <summary>
/// A taskbar window that isn't docked, shown as a tab beside the docked ones. It has the same bindable shape as
/// <see cref="DockedWindow"/> so both share the tab template; clicking it docks it (or switches to it, for a game).
/// </summary>
internal sealed class TaskbarEntry : INotifyPropertyChanged
{
    string title = "";

    public IntPtr Handle { get; }
    public ImageSource? Icon { get; }
    public bool IsPin => false;
    public bool IsDocked => false;
    public bool IsActive => false;
    public double Level => 0;
    public bool Playing => false;

    public string Title
    {
        get => title;
        set { if (title != value) { title = value; OnChanged(); } }
    }

    public TaskbarEntry(IntPtr handle)
    {
        Handle = handle;
        Icon = WindowUtil.GetIcon(handle);
        Refresh();
    }

    public void Refresh() => Title = WindowUtil.GetTitle(Handle);

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
