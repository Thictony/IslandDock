using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static IslandDock.Native;
using Forms = System.Windows.Forms;

namespace IslandDock;

public partial class IslandWindow : Window
{
    enum Edge { Top, Bottom, Left, Right }

    // Sizes in DIPs.
    const double CollapsedMaxLong = 340, CollapsedShort = 36, ExpandedW = 780, ExpandedH = 540, EdgeMargin = 8, DropSlop = 30;
    const double MinScale = 1.0, MaxScale = 1.6, ScaleStep = 0.1;
    static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(450);
    const long TypingHoldMs = 5000;
    static readonly TimeSpan HoverOpenDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan IdleFadeDelay = TimeSpan.FromSeconds(10);
    const double IdleOpacity = 0.2;

    readonly Settings settings = Islands.Settings;
    readonly ObservableCollection<DockedWindow> docked = new();
    // What the tab strip and pill show: docked windows plus every other taskbar window (TaskbarEntry), in taskbar fashion.
    readonly ObservableCollection<object> tabs = new();
    readonly System.Collections.Generic.Dictionary<IntPtr, (string? AppId, string? Path)> windowIds = new();
    (PinEntry Pin, DateTime Until)? launching;
    System.Collections.Generic.HashSet<IntPtr> launchBefore = new();
    // The window in front when the launch started, and when that was: an app that's already open brings its window forward.
    IntPtr launchForeground;
    DateTime launchStarted;
    DispatcherTimer? launchPoll;
    // Apps that are only in the tray, listed in their own dropdown.
    readonly ObservableCollection<TrayEntry> trayApps = new();
    DateTime trayCheckedAt = DateTime.MinValue;
    readonly HostWindow host = new();
    readonly ContextMenu menu;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };

    IntPtr hwnd;
    Edge edge;
    double along;
    string? monitor;

    // Current animated bounds, physical pixels.
    double cx, cy, cw, ch;
    double vw, vh, va; // spring velocities: width, height, centre along the edge
    /// <summary>The closed pill's length, fitted to its clock and icons; the frame loop eases toward it.</summary>
    double collapsedLong = 120;
    bool hasRect;
    RECT applied;

    bool focusOnShow;
    /// <summary>Set by a click on the closed island; the next frame opens it.</summary>
    bool openRequested;
    bool expanded, pinned, pickerOpen, mixerOpen, searchOpen, trayOpen, spotsOpen, updateOpen, menuOpen, exiting;
    TimeSpan? leaveSince, hoverSince;
    /// <summary>Opened by Alt+Tab or the Windows key; see <see cref="Frame"/>.</summary>
    bool keyOpened, closeRequested;
    TimeSpan keyOpenedAt;
    static readonly TimeSpan KeyFocusGrace = TimeSpan.FromMilliseconds(600);

    // A Windows notification shown on the closed island; its few seconds start once the island is closed.
    Notice? notice;
    TimeSpan? noticeUntil;
    const double NoticeW = 380, NoticeH = 84;
    static readonly TimeSpan NoticeShown = TimeSpan.FromSeconds(5);

    // Fading out of the way while a game or fullscreen video has the screen.
    bool immersive;
    TimeSpan lastEngaged;
    TimeSpan lastFrame;

    // Dragging the island itself.
    bool dragPending, dragging;
    POINT dragStart;
    /// <summary>
    /// moveArmed: held long enough that moving now drags the island. pressMoved: this press moved before that,
    /// so it's a pull (or nothing) rather than a click, and click handlers ignore its release.
    /// </summary>
    bool moveArmed, pressMoved;
    long pressTick;
    const long HoldToMoveMs = 1300, HoldHintMs = 300;
    const double PullToOpen = 20;
    readonly ScaleTransform lift = new();
    double liftAmount, liftVelocity;

    /// <summary>True when the current press is a gesture (a drag or a pull), not a click.</summary>
    bool Gestured => dragging || pressMoved || moveArmed;

    // Dragging another window onto the island.
    WinEventDelegate? winEventProc;
    IntPtr winHook, showHook, dropCandidate;
    /// <summary>Menus and dialogs of docked apps raised above the island; kept there when the island re-raises itself.</summary>
    readonly System.Collections.Generic.HashSet<IntPtr> raisedPopups = new();
    RECT dropStartRect;
    bool dropHover;
    /// <summary>Explorer windows already seen shown, so re-showing one (docking, switching tabs) isn't taken for a new one.</summary>
    readonly System.Collections.Generic.HashSet<IntPtr> seenExplorers = new();
    // A file being dragged over an open app's icon switches to that app after a moment, like the taskbar.
    object? fileHoverTab;
    long fileHoverSince;
    const long FileHoverMs = 450;

    DockedWindow? active;
    RECT hostRect;
    int statusTicks;

    // Expanded size multiplier: each docked window keeps its own, this is used when none is docked and for new ones.
    double defaultScale;
    // Never smaller than the active window's minimum size allows, or its edges (e.g. Spotify's player bar) get cropped.
    double PanelScale => active is { } a ? Math.Max(a.Scale, a.FitScale) : defaultScale;

    /// <summary>The display this island lives on; it stays there.</summary>
    public string Display { get; }
    readonly bool pinnedAtStart;

    public IslandWindow(string display, bool pinnedAtStart)
    {
        InitializeComponent();
        Display = monitor = display;
        this.pinnedAtStart = pinnedAtStart;
        // The single island of older versions keeps its place on the display it was on (or the main one).
        bool legacy = settings.Monitor == display
            || Forms.Screen.AllScreens.All(s => s.DeviceName != settings.Monitor) && Forms.Screen.PrimaryScreen?.DeviceName == display;
        var place = settings.Displays.TryGetValue(display, out var p) ? p
            : legacy ? new Placement { Edge = settings.Edge, Along = settings.Along } : new Placement();
        edge = Enum.TryParse(place.Edge, out Edge e) ? e : Edge.Top;
        along = Math.Clamp(place.Along, 0, 1);
        defaultScale = Math.Clamp(settings.PanelScale, MinScale, MaxScale);
        TabsList.ItemsSource = tabs;
        TrayList.ItemsSource = trayApps;

        menu = BuildMenu();
        Shell.ContextMenu = menu;

        SourceInitialized += OnSourceInitialized;
        Closing += (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true;
            Dispatcher.BeginInvoke(Exit);
        };
        tick.Tick += OnTick;
    }

    ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var startup = new MenuItem { Header = "Run at startup", IsCheckable = true, IsChecked = settings.RunAtStartup };
        startup.Click += (_, _) =>
        {
            settings.RunAtStartup = startup.IsChecked;
            Startup.Apply(settings.RunAtStartup);
            settings.Save();
        };
        var software = new MenuItem { Header = "Software rendering (if the island goes invisible)", IsCheckable = true, IsChecked = settings.UseSoftwareRendering };
        software.Click += (_, _) =>
        {
            settings.SoftwareRendering = software.IsChecked;
            foreach (var i in Islands.All) i.ApplyRenderMode();
            settings.Save();
        };
        var taskbar = new MenuItem { Header = "Hide the Windows taskbar", IsCheckable = true, IsChecked = settings.HideTaskbar };
        taskbar.Click += (_, _) =>
        {
            settings.HideTaskbar = taskbar.IsChecked;
            settings.Save();
            Islands.ApplyTaskbar();
        };
        var altTab = new MenuItem { Header = "Alt+Tab switches island tabs", IsCheckable = true, IsChecked = settings.AltTabSwitchesTabs };
        altTab.Click += (_, _) =>
        {
            settings.AltTabSwitchesTabs = altTab.IsChecked;
            settings.Save();
        };
        var winKey = new MenuItem { Header = "Windows key opens the island", IsCheckable = true, IsChecked = settings.WinKeyOpensIsland };
        winKey.Click += (_, _) =>
        {
            settings.WinKeyOpensIsland = winKey.IsChecked;
            settings.Save();
        };
        var fadeAll = new MenuItem { Header = "Fade islands on other displays during games", IsCheckable = true, IsChecked = settings.FadeOnOtherDisplays };
        fadeAll.Click += (_, _) =>
        {
            settings.FadeOnOtherDisplays = fadeAll.IsChecked;
            settings.Save();
        };
        var notify = new MenuItem { Header = "Show notifications in the island", IsCheckable = true, IsChecked = settings.IslandNotifications };
        var banners = new MenuItem { Header = "Hide Windows' own notification pop-ups", IsCheckable = true, IsChecked = settings.HideWindowsBanners, IsEnabled = settings.IslandNotifications };
        notify.Click += (_, _) =>
        {
            settings.IslandNotifications = notify.IsChecked;
            banners.IsEnabled = notify.IsChecked; // with nothing in the island, the pop-ups are all there is
            settings.Save();
            Notifications.Apply();
        };
        banners.Click += (_, _) =>
        {
            settings.HideWindowsBanners = banners.IsChecked;
            settings.Save();
            Notifications.Apply();
        };
        // An island per display, each switched on or off here; filled in when the menu opens, as displays come and go.
        var displays = new MenuItem { Header = "Island on displays" };
        displays.Items.Add(new MenuItem());
        var location = new MenuItem { Header = "Docking location" };
        location.Click += (_, _) => OpenLocationPanel();
        var undockAll = new MenuItem { Header = "Undock all windows" };
        undockAll.Click += (_, _) => UndockAll();
        var update = new MenuItem { Header = UpdateLabel };
        update.Click += (_, _) => CheckForUpdates();
        var tour = new MenuItem { Header = "Show the tour again" };
        tour.Click += (_, _) => Tutorial.Open();
        var exit = new MenuItem { Header = "Exit Island" };
        exit.Click += (_, _) => Exit();

        m.Items.Add(startup);
        m.Items.Add(software);
        m.Items.Add(taskbar);
        m.Items.Add(altTab);
        m.Items.Add(winKey);
        m.Items.Add(fadeAll);
        m.Items.Add(notify);
        m.Items.Add(banners);
        m.Items.Add(displays);
        m.Items.Add(location);
        m.Items.Add(undockAll);
        m.Items.Add(tour);
        m.Items.Add(update);
        m.Items.Add(new Separator());
        m.Items.Add(exit);
        m.Opened += (_, _) =>
        {
            // Another island's menu may have changed these.
            startup.IsChecked = settings.RunAtStartup;
            software.IsChecked = settings.UseSoftwareRendering;
            taskbar.IsChecked = settings.HideTaskbar;
            altTab.IsChecked = settings.AltTabSwitchesTabs;
            winKey.IsChecked = settings.WinKeyOpensIsland;
            fadeAll.IsChecked = settings.FadeOnOtherDisplays;
            notify.IsChecked = settings.IslandNotifications;
            banners.IsChecked = settings.HideWindowsBanners;
            banners.IsEnabled = settings.IslandNotifications;
            update.Header = UpdateLabel;
            update.IsEnabled = !Updater.Busy;
            displays.Items.Clear();
            var screens = Forms.Screen.AllScreens;
            foreach (var s in screens)
            {
                bool on = Islands.IsEnabled(s.DeviceName);
                string name = s.DeviceName;
                var item = new MenuItem
                {
                    Header = Islands.Label(s) + (name == Display ? " (this one)" : ""),
                    IsCheckable = true,
                    IsChecked = on,
                    // The last island can't be turned off; Exit Island is for that.
                    IsEnabled = !on || Islands.All.Count > 1,
                };
                item.Click += (_, _) => Dispatcher.BeginInvoke(() => Islands.SetEnabled(name, item.IsChecked));
                displays.Items.Add(item);
            }
            displays.Visibility = screens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        };
        Dress(m);
        return m;
    }

    /// <summary>
    /// The island's look for a right-click menu, kept above the island and docked windows (both topmost, so a plain
    /// menu could open behind them). When it closes, the tab in front gets the keyboard back.
    /// </summary>
    void Dress(ContextMenu m)
    {
        m.Style = (Style)FindResource("IslandMenu");
        IntPtr popup = IntPtr.Zero;
        m.Opened += (_, _) =>
        {
            menuOpen = true;
            if (PresentationSource.FromVisual(m) is HwndSource src)
            {
                popup = src.Handle;
                SetWindowPos(popup, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                raisedPopups.Add(popup);
            }
        };
        m.Closed += (_, _) =>
        {
            menuOpen = false;
            raisedPopups.Remove(popup);
            if (expanded && !exiting) { focusOnShow = true; Wake(); }
        };
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: no taskbar button, not in Alt+Tab.
        SetLong(hwnd, GWL_EXSTYLE, GetLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        ApplyRenderMode();

        host.Init(this);

        winEventProc = OnWinEvent;
        winHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        // Explorer windows open before the island aren't new ones to merge.
        EnumWindows((h, _) => { if (IsExplorer(h)) seenExplorers.Add(h); return true; }, IntPtr.Zero);

        AllowDrop = true;
        PreviewDragOver += OnFileDragOver;
        PreviewDragLeave += (_, _) => fileHoverTab = null;
        PreviewDrop += (_, _) => fileHoverTab = null;

        Startup.Apply(settings.RunAtStartup);
        Islands.ApplyTaskbar();
        ApplyOrientation();
        UpdateScaleUi();
        InitQuickSettings();
        InitMedia();
        if (pinnedAtStart) Pin_Click(this, new RoutedEventArgs());

        CompositionTarget.Rendering += OnRender;
        // A press or the mouse coming onto the island shouldn't wait for the next idle check.
        PreviewMouseDown += (_, _) => Wake();
        MouseEnter += (_, _) => Wake();
        // Open apps take their pins' places from the start, not a tick later.
        RefreshTabs();
        tick.Start();
        meterTick?.Start();
    }

    // ---------------------------------------------------------------- geometry

    Forms.Screen CurrentScreen() =>
        Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == monitor) ?? Forms.Screen.PrimaryScreen!;

    static double ScaleFor(Forms.Screen s)
    {
        var b = s.Bounds;
        var hmon = MonitorFromPoint(new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, 2);
        return GetDpiForMonitor(hmon, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    Rect ComputeRect(bool exp)
    {
        var screen = CurrentScreen();
        var wa = screen.WorkingArea;
        double k = ScaleFor(screen), m = EdgeMargin * k;
        bool vertical = edge is Edge.Left or Edge.Right;

        double w, h;
        if (exp)
        {
            w = Math.Min(ExpandedW * PanelScale * k, wa.Width - 2 * m);
            h = Math.Min(ExpandedH * PanelScale * k, wa.Height - 2 * m);
        }
        else if (notice != null && !vertical)
        {
            // The pill stays as it is and the card hangs below it (above, on the bottom edge).
            w = Math.Min(Math.Max(NoticeW, collapsedLong) * k, wa.Width - 2 * m);
            h = (CollapsedShort + NoticeH) * k;
        }
        else if (notice != null)
        {
            // On a side edge the pill is too narrow to hang a card off, so it turns into the card.
            w = Math.Min(NoticeW * k, wa.Width - 2 * m);
            h = NoticeH * k;
        }
        else
        {
            w = (vertical ? CollapsedShort : collapsedLong) * k;
            h = (vertical ? collapsedLong : CollapsedShort) * k;
        }

        double x, y;
        if (vertical)
        {
            double centerY = wa.Top + along * wa.Height;
            y = Math.Clamp(centerY - h / 2, wa.Top + m, wa.Bottom - m - h);
            x = edge == Edge.Left ? wa.Left + m : wa.Right - m - w;
        }
        else
        {
            double centerX = wa.Left + along * wa.Width;
            x = Math.Clamp(centerX - w / 2, wa.Left + m, wa.Right - m - w);
            y = edge == Edge.Top ? wa.Top + m : wa.Bottom - m - h;
        }
        return new Rect(x, y, w, h);
    }

    /// <summary>Moves the island to the edge of its own display nearest the cursor; each display has its own island.</summary>
    void SnapToCursor(POINT p)
    {
        var wa = CurrentScreen().WorkingArea;
        double dl = p.X - wa.Left, dr = wa.Right - p.X, dt = p.Y - wa.Top, db = wa.Bottom - p.Y;
        double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));

        var newEdge = min == dt ? Edge.Top : min == db ? Edge.Bottom : min == dl ? Edge.Left : Edge.Right;
        along = newEdge is Edge.Top or Edge.Bottom
            ? Math.Clamp((p.X - wa.Left) / (double)wa.Width, 0, 1)
            : Math.Clamp((p.Y - wa.Top) / (double)wa.Height, 0, 1);
        if (newEdge != edge) { edge = newEdge; ApplyOrientation(); }
    }

    bool Vertical => edge is Edge.Left or Edge.Right;

    /// <summary>
    /// On a side edge the pill's contents cascade top to bottom, each upright, rather than the row being turned on
    /// its side: the clock as hours over minutes, then Discord's buttons, then the wave and app tiles at the bottom.
    /// </summary>
    void ApplyOrientation()
    {
        bool v = Vertical;
        var stack = v ? Orientation.Vertical : Orientation.Horizontal;
        CollapsedInner.Margin = v ? new Thickness(0, 12, 0, 12) : new Thickness(14, 0, 14, 0);
        CollapsedLead.Orientation = CollapsedTrail.Orientation = DiscordPanel.Orientation = CollapsedIcons.Orientation = stack;
        CollapsedLead.HorizontalAlignment = CollapsedTrail.HorizontalAlignment = v ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        if (!v) CollapsedTrail.HorizontalAlignment = HorizontalAlignment.Right;
        CollapsedLead.VerticalAlignment = v ? VerticalAlignment.Top : VerticalAlignment.Center;
        CollapsedTrail.VerticalAlignment = v ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        DiscordPanel.Margin = DiscordCall.Margin = v ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
        DiscordCall.Orientation = stack;
        ShowCall(callMembers); // spacing between avatars runs along the pill too
        DiscordHeadsetButton.Margin = DiscordShareButton.Margin = v ? new Thickness(0, 4, 0, 0) : new Thickness(4, 0, 0, 0);
        CollapsedWave.Margin = v ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 6, 0);
        ClockText.LineHeight = v ? 14 : double.NaN;
        ClockText.LineStackingStrategy = v ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
        ClockText.Text = ClockString();
        UpdateMiniPlayer();
        RefreshCollapsedIcons();
    }

    string ClockString()
    {
        var now = DateTime.Now;
        // Sideways the colon lies flat too: two middle dots between the hour and the minutes.
        return Vertical ? $"{now:%h}\n··\n{now:mm}" : now.ToString("h:mm");
    }

    // ---------------------------------------------------------------- frame loop

    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    TimeSpan lastRendering;
    DispatcherTimer? idleTick;
    bool idle;

    void OnRender(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == lastRendering) return;
        lastRendering = t;
        Frame();
    }

    /// <summary>
    /// Being hooked to every frame keeps WPF redrawing at the display's rate, which over a game is 144+ times a
    /// second for nothing. Once nothing is moving, the island checks the mouse a few times a second instead, and goes
    /// back to every frame as soon as it's hovered, pressed or has something to animate.
    /// </summary>
    void SetIdle(bool value)
    {
        if (value == idle) return;
        idle = value;
        if (idle)
        {
            CompositionTarget.Rendering -= OnRender;
            (idleTick ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Frame(), Dispatcher)).Start();
        }
        else
        {
            idleTick?.Stop();
            CompositionTarget.Rendering += OnRender;
        }
    }

    void Wake() { if (!exiting) SetIdle(false); }

    /// <summary>See <see cref="Settings.SoftwareRendering"/>. Only the island itself; docked apps draw their own windows.</summary>
    internal void ApplyRenderMode()
    {
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is HwndTarget t)
            t.RenderMode = settings.UseSoftwareRendering ? RenderMode.SoftwareOnly : RenderMode.Default;
    }

    void Frame()
    {
        var now = clock.Elapsed;
        double dt = Math.Clamp((now - lastFrame).TotalSeconds, 0, 0.05);
        lastFrame = now;

        GetCursorPos(out var p);
        bool mouseDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        double k = VisualTreeHelper.GetDpi(this).DpiScaleX;

        // Island drag: hold still for HoldToMove to pick the island up, then move it along the screen edges.
        // Pulling it away from its edge without holding first opens it instead.
        if (dragPending)
        {
            if (!mouseDown)
            {
                dragPending = false; // moveArmed stays until the next press, so letting go of a hold isn't a click
                if (dragging) { dragging = false; SaveSettings(); }
            }
            else if (!dragging)
            {
                int dx = p.X - dragStart.X, dy = p.Y - dragStart.Y;
                bool moved = Math.Abs(dx) > 6 || Math.Abs(dy) > 6;
                if (moveArmed)
                {
                    if (moved) { dragging = true; pickerOpen = mixerOpen = searchOpen = trayOpen = spotsOpen = updateOpen = false; }
                }
                else if (!pressMoved)
                {
                    if (moved) pressMoved = true;
                    else if (Environment.TickCount64 - pressTick >= HoldToMoveMs) moveArmed = true;
                }
                int inward = edge switch { Edge.Top => dy, Edge.Bottom => -dy, Edge.Left => dx, _ => -dx };
                if (pressMoved && !expanded && inward > PullToOpen * k) openRequested = true;
            }
            if (dragging) SnapToCursor(p);
        }
        UpdateLift(dt);

        // Hover / drop detection against the current bounds.
        var bounds = new Rect(cx, cy, cw, ch);
        bool over = hasRect && bounds.Contains(p.X, p.Y);
        var slop = bounds; slop.Inflate(DropSlop * k, DropSlop * k);
        dropHover = dropCandidate != IntPtr.Zero && hasRect && slop.Contains(p.X, p.Y);
        WatchFileDragOut(p, mouseDown, over, slop);

        // Mouse capture covers dragging the volume slider out past the island's edge.
        // Typing into a docked window counts too, so the island doesn't snap shut when the mouse drifts off it. Not over
        // a game, though: there the mouse going back to the game means back to playing, and it shouldn't take a click.
        host.FocusTarget = active is { Floating: false } ? active.Handle : IntPtr.Zero;
        // Only actual typing: opening the island hands the docked window the keyboard, which alone would keep it open for good.
        bool typing = expanded && !immersive && host.FocusTarget != IntPtr.Zero && GetForegroundWindow() == host.Handle
                      && Environment.TickCount64 - KeyboardHook.LastKeyAt < TypingHoldMs;
        // Same for typing a file search: the island itself has the keyboard then.
        bool searching = expanded && searchOpen && IsActive;
        // Opened from the keyboard (Alt+Tab, the Windows key): stays open while it has the keyboard, until it's clicked
        // away from or the mouse comes onto it and takes over. A moment's grace while the keyboard is handed over.
        if (keyOpened && (over || now - keyOpenedAt > KeyFocusGrace && !OwnsForeground())) keyOpened = false;
        bool keyed = expanded && keyOpened;
        bool engaged = over || menuOpen || updateOpen || dropHover || typing || searching || keyed || (IsMouseCaptureWithin && !dragging);
        if (engaged) leaveSince = null;
        else leaveSince ??= now;

        // A click opens the island, and so does resting the mouse on it for a second (not while pressing it to move it).
        // Not while it shows a notification, which is clicked to open its app.
        if (over && !expanded && notice == null && !dragPending && !dragging) hoverSince ??= now;
        else hoverSince = null;
        if (now - hoverSince >= HoverOpenDelay) openRequested = true;
        bool want = expanded;
        if (dragging) { want = false; openRequested = false; }
        else if (pinned || dropHover) want = true;
        else if (closeRequested) want = false;
        else if (!expanded && openRequested) want = true;
        else if (expanded && !engaged && now - leaveSince >= CollapseDelay) want = false;
        openRequested = closeRequested = false;
        if (want != expanded)
        {
            expanded = want;
            // Opening hands the keyboard to the active tab once it's shown, so typing works without a click first.
            focusOnShow = expanded;
            if (!expanded) pickerOpen = mixerOpen = searchOpen = trayOpen = spotsOpen = updateOpen = false;
        }

        // A notification waits while the island is open (dropped if that's long), then shows for a few seconds,
        // longer while the mouse is on it.
        if (notice != null)
        {
            if (expanded || dragging) { noticeUntil = null; if (DateTime.Now - notice.Arrived > TimeSpan.FromSeconds(30)) notice = null; }
            else if (over) noticeUntil = now + NoticeShown;
            else if (noticeUntil == null) noticeUntil = now + NoticeShown;
            else if (now >= noticeUntil) { notice = null; noticeUntil = null; }
        }

        // After 10s untouched during a game or fullscreen video, fade to a faint ghost.
        if (engaged || expanded || dragging || notice != null) lastEngaged = now;
        double fade = immersive && now - lastEngaged >= IdleFadeDelay ? IdleOpacity : 1;
        double rate = fade < Shell.Opacity ? 2 : 10; // fade out gently, come back quickly
        double o = Shell.Opacity + (fade - Shell.Opacity) * (1 - Math.Exp(-dt * rate));
        if (Math.Abs(o - fade) < 0.005) o = fade;
        if (o != Shell.Opacity) Shell.Opacity = o;

        // Animate toward the target rect.
        var target = ComputeRect(expanded);
        bool settled;
        if (!hasRect)
        {
            (cx, cy, cw, ch) = (target.X, target.Y, target.Width, target.Height);
            hasRect = true;
            settled = true;
        }
        else
        {
            // Size and the centre along the edge bounce on springs; the side against the screen edge stays put,
            // so the overshoot swells into the screen rather than lifting off the edge. Closing is damped more,
            // so the pill doesn't dip below its resting size.
            bool horizontal = edge is Edge.Top or Edge.Bottom;
            double along = horizontal ? cx + cw / 2 : cy + ch / 2;
            double alongTarget = horizontal ? target.X + target.Width / 2 : target.Y + target.Height / 2;
            double zeta = expanded || notice != null ? SpringDampingOpen : SpringDampingClose;
            for (double left = dt; left > 0; left -= SpringStep)
            {
                double h = Math.Min(SpringStep, left);
                Spring(ref cw, ref vw, target.Width, zeta, h);
                Spring(ref ch, ref vh, target.Height, zeta, h);
                if (!dragging) Spring(ref along, ref va, alongTarget, zeta, h);
            }
            cw = Math.Max(cw, 4); ch = Math.Max(ch, 4);
            // While dragging, the pill stays under the cursor; easing toward it made it trail behind.
            if (dragging) { along = alongTarget; va = 0; }
            if (horizontal) { cx = along - cw / 2; cy = edge == Edge.Top ? target.Y : target.Bottom - ch; }
            else { cy = along - ch / 2; cx = edge == Edge.Left ? target.X : target.Right - cw; }

            // The spring's last pixel of travel takes longest and can't be seen, so it counts as there a little early.
            settled = Math.Abs(target.X - cx) < 1 && Math.Abs(target.Y - cy) < 1
                   && Math.Abs(target.Width - cw) < 1 && Math.Abs(target.Height - ch) < 1
                   && Math.Abs(vw) + Math.Abs(vh) + Math.Abs(va) < 40;
            if (settled) { (cx, cy, cw, ch) = (target.X, target.Y, target.Width, target.Height); vw = vh = va = 0; }
        }
        ApplyBounds(target, settled, k);

        UpdateVisuals(k);
        UpdateHost(settled, k);

        SetIdle(!exiting && settled && o == fade && liftAmount == 0 && hoverSince == null && notice == null
                && !dragPending && !dragging && !dropHover && dropCandidate == IntPtr.Zero);
    }

    /// <summary>
    /// Resizing a transparent window means reallocating and re-uploading its whole surface, which is what made
    /// open/close stutter (badly over games). So while animating, the window is held at a size covering the whole
    /// animation and only the panel moves inside it; the window is resized at most once, at the start.
    /// The transparent rest of the window doesn't take clicks, and hover uses the panel's own bounds.
    /// </summary>
    void ApplyBounds(Rect target, bool settled, double k)
    {
        var r = new RECT
        {
            Left = (int)Math.Round(cx), Top = (int)Math.Round(cy),
            Right = (int)Math.Round(cx + cw), Bottom = (int)Math.Round(cy + ch)
        };
        var frame = r;
        // Dragging moves the small pill a long way, where a growing frame would cover the screen; it's cheap to resize.
        if (!settled && !dragging)
        {
            var t = new RECT
            {
                Left = (int)Math.Floor(target.Left), Top = (int)Math.Floor(target.Top),
                Right = (int)Math.Ceiling(target.Right), Bottom = (int)Math.Ceiling(target.Bottom)
            };
            // Room for the springs to overshoot, so the window isn't resized mid-bounce.
            int ox = (int)Math.Ceiling(target.Width * 0.15), oy = (int)Math.Ceiling(target.Height * 0.15);
            t = new RECT { Left = t.Left - ox, Top = t.Top - oy, Right = t.Right + ox, Bottom = t.Bottom + oy };
            frame = Union(Union(r, t), applied.Width > 0 ? applied : r);
            frame = WithinDesktop(frame);
        }
        else if (!dragging && applied.Width > 0 && r.Left >= applied.Left && r.Top >= applied.Top
                 && r.Right <= applied.Right && r.Bottom <= applied.Bottom)
        {
            // Once settled inside the frame, keep the frame: resizing a transparent WPF window blanks it for a frame,
            // which showed as a flicker at the very end of every open and close. The transparent rest takes no clicks,
            // and the next open or close then needs no resize either.
            frame = applied;
        }

        var margin = new Thickness((r.Left - frame.Left) / k, (r.Top - frame.Top) / k,
                                   (frame.Right - r.Right) / k, (frame.Bottom - r.Bottom) / k);
        if (!frame.Equals(applied))
        {
            applied = frame;
            SetWindowPos(hwnd, HWND_TOPMOST, frame.Left, frame.Top, frame.Width, frame.Height, SWP_NOACTIVATE);
        }
        if (Shell.Margin != margin) Shell.Margin = margin;
    }

    const double SpringFrequency = 27, SpringDampingOpen = 0.62, SpringDampingClose = 0.85, SpringStep = 0.004;

    /// <summary>One step of a damped spring pulling x toward target.</summary>
    static void Spring(ref double x, ref double v, double target, double zeta, double dt)
    {
        const double w = SpringFrequency;
        v += (w * w * (target - x) - 2 * zeta * w * v) * dt;
        x += v * dt;
    }

    // Windows won't make a window much taller or wider than the desktop, and the panel would be squeezed to fit what's
    // left. A frame spanning the screen comes up when the island moves from one edge to the opposite one while open.
    static RECT WithinDesktop(RECT f)
    {
        var d = Forms.SystemInformation.VirtualScreen;
        return new RECT { Left = Math.Max(f.Left, d.Left), Top = Math.Max(f.Top, d.Top), Right = Math.Min(f.Right, d.Right), Bottom = Math.Min(f.Bottom, d.Bottom) };
    }

    static RECT Union(RECT a, RECT b) => new()
    {
        Left = Math.Min(a.Left, b.Left), Top = Math.Min(a.Top, b.Top),
        Right = Math.Max(a.Right, b.Right), Bottom = Math.Max(a.Bottom, b.Bottom)
    };

    /// <summary>
    /// The pick-up cue. While the press is held still the border warms toward the accent colour; once the hold
    /// completes the island sinks slightly and its border pulses until it's let go.
    /// </summary>
    void UpdateLift(double dt)
    {
        bool lifted = moveArmed && dragPending || dragging;
        double hold = lifted ? 1
            : dragPending && !pressMoved ? Math.Clamp((Environment.TickCount64 - pressTick - HoldHintMs) / (double)(HoldToMoveMs - HoldHintMs), 0, 1)
            : 0;
        for (double left = dt; left > 0; left -= SpringStep)
            Spring(ref liftAmount, ref liftVelocity, lifted ? 1 : 0, 0.45, Math.Min(SpringStep, left));
        if (!lifted && Math.Abs(liftAmount) < 0.002 && Math.Abs(liftVelocity) < 0.05) liftAmount = liftVelocity = 0;

        if (Shell.RenderTransform != lift) { Shell.RenderTransformOrigin = new Point(0.5, 0.5); Shell.RenderTransform = lift; }
        // Only the pill sinks: when open, the docked window is a separate window that wouldn't scale with it.
        double s = expanded ? 1 : 1 - 0.05 * liftAmount;
        if (lift.ScaleX != s) lift.ScaleX = lift.ScaleY = s;

        double pulse = lifted ?0.7 + 0.3 * Math.Sin(Environment.TickCount64 / 1000.0 * 6) : hold;
        byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        var c = hold == 0 ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(Lerp(0x26, 0xFF, pulse), Lerp(0xFF, 0x4C, hold), Lerp(0xFF, 0xC2, hold), 0xFF);
        if (Shell.BorderBrush is not SolidColorBrush b || b.Color != c) Shell.BorderBrush = new SolidColorBrush(c);
    }

    void UpdateVisuals(double k)
    {
        double wd = cw / k, hd = ch / k, shortSide = Math.Min(wd, hd);
        // Measured against the smallest expanded size, so resizing between scales never fades the panel.
        double expShort = Math.Min(ExpandedW, ExpandedH) * MinScale;
        double progress = Math.Clamp((shortSide - CollapsedShort) / Math.Max(1, expShort - CollapsedShort), 0, 1);

        double radius = Math.Min(shortSide / 2, 24);
        if (Math.Abs(Shell.CornerRadius.TopLeft - radius) > 0.05) Shell.CornerRadius = new CornerRadius(radius);

        // A notification card: on the top or bottom edge it hangs off the closed pill, which keeps showing; on a side
        // edge it takes the pill over as it grows into it, and gives it back as it shrinks.
        bool noticing = notice != null && !expanded;
        // The pill keeps its own size at the screen edge while the card is out, and while it folds away again.
        bool stacked = !Vertical && !expanded && (noticing || noticeStacked && hd > CollapsedShort + 1);
        noticeStacked = stacked;
        LayOutNotice(stacked);
        double no = !noticing ? 0
            : stacked ? Math.Clamp((hd - CollapsedShort) / NoticeH * 1.6 - 0.6, 0, 1)
            : Math.Clamp((hd - CollapsedShort) / (NoticeH - CollapsedShort) * 1.6 - 0.6, 0, 1);
        NoticeView.Opacity = no;
        NoticeView.Visibility = no > 0 ? Visibility.Visible : Visibility.Collapsed;
        double co = noticing ? (stacked ? 1 : Math.Clamp(1 - no * 2, 0, 1))
            : stacked ? 1 : Math.Clamp(1 - progress * 4, 0, 1);
        CollapsedView.Opacity = co;
        CollapsedView.Visibility = co > 0 ? Visibility.Visible : Visibility.Hidden;

        ExpandedView.Opacity = Math.Clamp((progress - 0.55) / 0.45, 0, 1);
        ExpandedView.Visibility = progress > 0.3 && !noticing ? Visibility.Visible : Visibility.Collapsed;

        DropOverlay.Visibility = dropHover ? Visibility.Visible : Visibility.Collapsed;
        PickerPanel.Visibility = pickerOpen && !dropHover ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = searchOpen && !pickerOpen && !dropHover ? Visibility.Visible : Visibility.Collapsed;
        MixerPanel.Visibility = mixerOpen && !pickerOpen && !searchOpen && !dropHover ? Visibility.Visible : Visibility.Collapsed;
        TrayPanel.Visibility = trayOpen && !pickerOpen && !searchOpen && !mixerOpen && !dropHover ? Visibility.Visible : Visibility.Collapsed;
        LocationPanel.Visibility = spotsOpen && !updateOpen && !pickerOpen && !searchOpen && !mixerOpen && !trayOpen && !dropHover ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanel.Visibility = updateOpen && !dropHover ? Visibility.Visible : Visibility.Collapsed;
        TrayEmpty.Visibility = trayApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = active == null && !pickerOpen && !mixerOpen && !searchOpen && !trayOpen && !spotsOpen && !updateOpen && !dropHover ? Visibility.Visible : Visibility.Collapsed;
        PopOutButton.IsEnabled = active != null;
    }

    bool noticeStacked;

    /// <summary>
    /// Stacked, the pill keeps its closed size against the screen edge and the card fills the rest; otherwise both
    /// fill the island.
    /// </summary>
    void LayOutNotice(bool stacked)
    {
        bool top = edge == Edge.Top;
        double height = stacked ? CollapsedShort : double.NaN, width = stacked ? collapsedLong : double.NaN;
        var align = !stacked ? VerticalAlignment.Stretch : top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        var margin = !stacked ? new Thickness(14, 0, 16, 0)
            : top ? new Thickness(14, CollapsedShort - 2, 16, 6) : new Thickness(14, 6, 16, CollapsedShort - 2);
        if (!CollapsedView.Height.Equals(height)) CollapsedView.Height = height;
        if (!CollapsedView.Width.Equals(width)) CollapsedView.Width = width;
        if (CollapsedView.VerticalAlignment != align) CollapsedView.VerticalAlignment = align;
        if (NoticeView.Margin != margin) NoticeView.Margin = margin;
        // The card keeps its size while the island grows, so it slides out from under the pill rather than stretching.
        var cardAlign = !stacked ? VerticalAlignment.Stretch : top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        double cardHeight = stacked ? NoticeH - 6 : double.NaN;
        if (NoticeView.VerticalAlignment != cardAlign) NoticeView.VerticalAlignment = cardAlign;
        if (!NoticeView.Height.Equals(cardHeight)) NoticeView.Height = cardHeight;
    }

    void UpdateHost(bool settled, double k)
    {
        // Leave a floating window alone while the user is dragging it (dragging it away undocks it).
        if (active is { Floating: true } && active.Handle == dropCandidate) return;

        bool show = expanded && settled && active != null && !pickerOpen && !mixerOpen && !searchOpen && !trayOpen && !spotsOpen && !updateOpen && !dropHover && !dragging
                    && ExpandedView.IsVisible && ContentArea.ActualWidth > 0;
        if (!show)
        {
            if (host.IsVisible) host.Hide();
            if (active is { Floating: true } && IsWindowVisible(active.Handle)) ShowWindow(active.Handle, SW_HIDE);
            hostRect = default;
            return;
        }

        var origin = ContentArea.PointToScreen(new Point(0, 0));
        var r = new RECT { Left = (int)Math.Round(origin.X), Top = (int)Math.Round(origin.Y) };
        r.Right = r.Left + (int)Math.Round(ContentArea.ActualWidth * k);
        r.Bottom = r.Top + (int)Math.Round(ContentArea.ActualHeight * k);

        // A floating window sits over the content area itself, so the host stays out of its way.
        bool floating = active!.Floating;
        if (floating && host.IsVisible) host.Hide();
        if (r.Equals(hostRect) && (floating || host.IsVisible))
        {
            // Keep it fitted if the app resized itself (fullscreen video/screenshare inside the island).
            // That size is the app's choice, not a minimum, so it mustn't grow the island.
            if (active.IsMisplaced() && !active.Insists()) LayoutActive(fit: false);
            return;
        }
        hostRect = r;
        // Size the host and the app while still hidden. Showing first flashed the old, wrongly sized panel and
        // the app's stale picture until it caught up; only Chromium apps like Discord repaint fast enough to hide that.
        if (!floating) SetWindowPos(host.Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        LayoutActive();
        if (!floating && !host.IsVisible)
        {
            host.Show();
            // WPF can put back the size it last knew about when it shows the window, so size it once more.
            SetWindowPos(host.Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            LayoutActive(fit: false);
            // Hiding and showing the app here, and nudging its size once it was open, were for an app cut off until
            // clicked; that was the host's old window region, and both showed as a flicker at the end of opening.
            RedrawWindow(active.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
        }
        if (focusOnShow)
        {
            focusOnShow = false;
            // The host passes its focus on to the docked window; a floating one takes it itself.
            host.FocusTarget = floating ? IntPtr.Zero : active.Handle;
            ForceForeground(floating ? active.Handle : host.Handle);
        }
    }

    /// <summary>Makes a just-shown tab redo its layout at the host's size once it has settled (see <see cref="DockedWindow.Nudge"/>).</summary>
    async void NudgeSoon(DockedWindow d, int delayMs)
    {
        await System.Threading.Tasks.Task.Delay(delayMs);
        await d.Nudge(() => active == d && host.IsVisible && hostRect.Width > 0 ? hostRect : null);
    }

    void LayoutActive(bool fit = true)
    {
        if (active == null || hostRect.Width <= 0) return;
        active.Place(hostRect, hostRect);
        if (active.Floating) ShowWindow(active.Handle, SW_SHOWNA);
        if (fit) FitToMinimum(active);
    }

    /// <summary>
    /// If the window truly can't shrink to the content area (it would lose part of its UI), grow the island just
    /// enough for it to fit. Anything less, and windows stay at the scale the user picked.
    /// </summary>
    async void FitToMinimum(DockedWindow d)
    {
        double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var (minW, minH) = Minimum(d, k);
        // A few pixels over is rounding or a border, not hidden UI.
        double slack = 8 * k;
        if (minW - hostRect.Width <= slack && minH - hostRect.Height <= slack) return;

        // Apps often answer the first resize while still settling (restoring their old size, a DPI change), so only
        // believe the minimum if the same refusal comes back once the app has had time to settle. Asking again straight
        // away caught apps mid-settle, and the island grew past the scale the user picked (160% instead of 150%) for good.
        if (!fitChecks.Add(d)) return;
        await System.Threading.Tasks.Task.Delay(700);
        fitChecks.Remove(d);
        if (active != d || hostRect.Width <= 0 || d.Floating) return;
        d.Place(hostRect, hostRect);
        var (againW, againH) = Minimum(d, k);
        minW = Math.Min(minW, againW);
        minH = Math.Min(minH, againH);
        double overW = minW - hostRect.Width, overH = minH - hostRect.Height;
        if (overW <= slack && overH <= slack) return;

        // The island's chrome (navbar, tabs, margins) stays the same size; only the content area must grow.
        double needW = (cw + Math.Max(0, overW)) / (ExpandedW * k);
        double needH = (ch + Math.Max(0, overH)) / (ExpandedH * k);
        double need = Math.Min(MaxScale, Math.Ceiling(Math.Max(needW, needH) * 100 - 0.01) / 100);
        if (need <= d.FitScale + 0.001) return;
        d.FitScale = need;
        UpdateScaleUi();
    }

    readonly System.Collections.Generic.HashSet<DockedWindow> fitChecks = new();

    static (double w, double h) Minimum(DockedWindow d, double k)
    {
        var p = d.Placed;
        double w = p.Width, h = p.Height;
        if (d.KnownMinimum is Size known) { w = Math.Max(w, known.Width * k); h = Math.Max(h, known.Height * k); }
        return (w, h);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // Activating the island raises it over a floating window; force a re-layout to put it back on top.
        hostRect = default;
    }

    void OnTick(object? sender, EventArgs e)
    {
        string clock = ClockString();
        // "9:59" to "10:00" is wider, and the pill is sized around the clock.
        if (ClockText.Text != clock) { ClockText.Text = clock; RefreshCollapsedIcons(); }

        // Forget windows that closed or re-parented themselves.
        bool changed = false;
        foreach (var d in docked.ToList())
        {
            if (!d.IsLost(host.Handle)) { d.Refresh(); continue; }
            docked.Remove(d);
            changed = true;
            if (d == active) active = null;
        }
        if (changed && active == null && docked.Count > 0) Activate(docked[^1]);
        // Every tick, so windows opening and closing elsewhere show up like on the taskbar.
        RefreshTabs();

        if (statusTicks > 0 && --statusTicks == 0) StatusText.Text = "";

        if (expanded)
        {
            CoreAudio.Refresh();
            UpdateQuickSettings();
            if (mixerOpen) RefreshMixer();
        }
        immersive = WindowUtil.IsImmersive(hwnd, settings.FadeOnOtherDisplays);
        // The Start key and an Explorer restart bring the taskbar back.
        if (settings.HideTaskbar) TaskbarHider.SetVisible(false);
        // The window is only moved when its size changes now, which used to re-raise it as a side effect;
        // games that make themselves topmost while focused would otherwise cover it for good.
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (host.IsVisible) SetWindowPos(host.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        // A floating window (Xbox, Settings) sits over the island, so raising the island just covered it.
        if (active is { Floating: true } && IsWindowVisible(active.Handle) && active.Handle != dropCandidate)
            SetWindowPos(active.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        raisedPopups.RemoveWhere(p => !IsWindow(p));
        foreach (var p in raisedPopups.Where(IsWindowVisible))
            SetWindowPos(p, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        RefreshAudioSources();
    }

    // ---------------------------------------------------------------- docking

    void OnWinEvent(IntPtr hook, uint eventType, IntPtr target, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || exiting) return;

        if (eventType == EVENT_OBJECT_SHOW)
        {
            MergeExplorer(target);
            RaisePopup(target);
            return;
        }
        // A docked tab stays put: dragging its (self-drawn) title bar or minimizing it is undone straight away.
        if (eventType is EVENT_SYSTEM_MOVESIZESTART or EVENT_SYSTEM_MOVESIZEEND or EVENT_SYSTEM_MINIMIZESTART
            && docked.Any(d => d.Handle == target && !d.Floating))
        {
            if (eventType == EVENT_SYSTEM_MOVESIZESTART) PostMessage(target, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
            else hostRect = default;
            return;
        }
        if (eventType == EVENT_SYSTEM_MOVESIZESTART)
        {
            if (WindowUtil.IsCandidate(target))
            {
                dropCandidate = target;
                GetWindowRect(target, out dropStartRect);
            }
        }
        else if (eventType == EVENT_SYSTEM_MOVESIZEEND && target == dropCandidate)
        {
            bool drop = dropHover;
            dropCandidate = IntPtr.Zero;
            dropHover = false;

            // A floating docked window that was dragged off the island is let go where it landed.
            if (docked.FirstOrDefault(d => d.Handle == target && d.Floating) is DockedWindow f)
            {
                if (drop) { hostRect = default; Activate(f); }
                else Release(f);
                return;
            }
            if (!drop) return;

            // If the drag un-maximized the window, its pre-drag rect is the maximized one; use where it ended up instead.
            GetWindowRect(target, out var end);
            var restore = end.Width == dropStartRect.Width && end.Height == dropStartRect.Height ? dropStartRect : end;
            DockWindow(target, restore);
        }
    }

    /// <summary>
    /// A docked app's menus and dialogs are its own top-level windows, not topmost, so they'd open behind the island.
    /// Those are raised above it: windows with an owner (dialogs), menus, and caption-less popups (Chrome-style menus).
    /// An unowned window with a title bar is a new main window and is left alone, or it would stay on top for good.
    /// </summary>
    void RaisePopup(IntPtr h)
    {
        if (docked.Count == 0 || GetAncestor(h, 2 /* GA_ROOT */) != h || !IsWindowVisible(h)) return;
        uint pid = WindowUtil.GetPid(h);
        if (!docked.Any(d => d.ProcessId == pid)) return;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(h, cls, cls.Capacity);
        long style = GetLong(h, GWL_STYLE);
        bool popup = Native.GetWindow(h, GW_OWNER) != IntPtr.Zero || cls.ToString() == "#32768"
            || (style & WS_POPUP) != 0 && (style & WS_CAPTION) != WS_CAPTION;
        if (!popup) return;
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        raisedPopups.Add(h);
    }

    static bool IsExplorer(IntPtr h) => WindowUtil.GetClass(h) == "CabinetWClass";

    /// <summary>
    /// A new Explorer window, while another is open, becomes a tab of that one (the docked one if there is one)
    /// rather than a second island tab. Hidden at once so it doesn't flash; shown again if the move fails.
    /// A window shown while the mouse is down is a tab being dragged out of Explorer, and is left alone.
    /// </summary>
    void MergeExplorer(IntPtr h)
    {
        if (!IsExplorer(h) || GetAncestor(h, 2 /* GA_ROOT */) != h || !seenExplorers.Add(h)) return;
        seenExplorers.RemoveWhere(x => !IsWindow(x));
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) return;
        // Every island sees the window; only one moves it.
        if (Islands.ExplorerOwner != this) return;
        var into = active is { } a && IsExplorer(a.Handle) ? a.Handle
            : docked.FirstOrDefault(d => IsExplorer(d.Handle))?.Handle
            ?? WindowUtil.CandidateHandles(new System.Collections.Generic.HashSet<IntPtr> { h }).FirstOrDefault(IsExplorer);
        if (into == IntPtr.Zero) return;

        ShowWindow(h, SW_HIDE);
        Dispatcher.BeginInvoke(async () =>
        {
            if (!await ExplorerTabs.MoveToTab(h, into))
            {
                if (IsWindow(h)) ShowWindow(h, SW_SHOW);
                return;
            }
            // Opening Explorer from its tab's right-click waits for a new window that now won't come.
            if (launching != null) EndLaunch();
            if (docked.FirstOrDefault(d => d.Handle == into) is DockedWindow d) OpenInto(d);
            else
            {
                if (IsIconic(into)) ShowWindow(into, SW_RESTORE);
                ForceForeground(into);
            }
        });
    }

    bool DockWindow(IntPtr h, RECT? restore)
    {
        var existing = docked.FirstOrDefault(d => d.Handle == h);
        if (existing != null) { Activate(existing); return true; }

        var d = new DockedWindow(h);
        d.Scale = settings.AppScales.TryGetValue(d.App, out var s) ? Math.Clamp(s, MinScale, MaxScale) : defaultScale;
        if (!d.Dock(host.Handle, restore))
        {
            ShowStatus("Couldn't dock that window (it may be running as administrator).");
            return false;
        }
        docked.Add(d);
        Activate(d);
        return true;
    }

    void Activate(DockedWindow d)
    {
        active = d;
        foreach (var x in docked)
        {
            x.IsActive = x == d;
            if (x != d) ShowWindow(x.Handle, SW_HIDE);
        }
        LayoutActive();
        // A floating window is shown by LayoutActive once the island has room for it.
        if (!d.Floating)
        {
            ShowWindow(d.Handle, SW_SHOWNA);
            NudgeSoon(d, 350);
        }
        // Switching tabs from the island: let the new window take the keyboard straight away.
        var fg = GetForegroundWindow();
        if (!d.Floating && (fg == hwnd || fg == host.Handle))
        {
            host.FocusTarget = d.Handle;
            if (fg == host.Handle) host.PassFocus(d.Handle);
            else SetForegroundWindow(host.Handle);
        }
        pickerOpen = mixerOpen = searchOpen = trayOpen = spotsOpen = updateOpen = false;
        RefreshTabs();
        UpdateScaleUi();
    }

    void SetScale(double scale)
    {
        scale = Math.Round(Math.Clamp(scale, MinScale, MaxScale), 2);
        if (active != null)
        {
            active.Scale = scale;
            if (active.App.Length > 0) settings.AppScales[active.App] = scale;
        }
        // Only the picker (no window open) changes the default; one app's size mustn't leak to the next app.
        else
        {
            defaultScale = scale;
            settings.PanelScale = scale;
        }
        settings.Save();
        UpdateScaleUi();
    }

    void UpdateScaleUi()
    {
        ScaleResetButton.Content = $"{Math.Round(PanelScale * 100)}%";
        double floor = Math.Max(MinScale, active?.FitScale ?? 0);
        ScaleDownButton.IsEnabled = PanelScale > floor + 0.001;
        ScaleDownButton.ToolTip = floor > MinScale && PanelScale <= floor + 0.001
            ? "Smaller (this window can't get any smaller)" : "Smaller";
        ScaleUpButton.IsEnabled = PanelScale < MaxScale - 0.001;
    }

    void ScaleDown_Click(object sender, RoutedEventArgs e) => SetScale(PanelScale - ScaleStep);
    void ScaleUp_Click(object sender, RoutedEventArgs e) => SetScale(PanelScale + ScaleStep);
    void ScaleReset_Click(object sender, RoutedEventArgs e) => SetScale(Settings.DefaultScale);

    void Undock(DockedWindow d) => Remove(d, d.Undock);

    void Release(DockedWindow d) => Remove(d, d.Release);

    void Remove(DockedWindow d, Action restore)
    {
        docked.Remove(d);
        restore();
        if (active == d)
        {
            active = null;
            if (docked.Count > 0) Activate(docked[^1]);
        }
        RefreshTabs();
    }

    public void UndockAll()
    {
        foreach (var d in docked.ToList())
        {
            try { d.Undock(); } catch { }
        }
        docked.Clear();
        active = null;
        RefreshTabs();
    }

    /// <summary>
    /// Mirrors the taskbar: its pins in order, each replaced by its app's windows while it's open, then every other
    /// app window. Unpinned windows keep their place, like taskbar buttons, and a window that gets docked or undocked
    /// swaps kinds in its slot; new ones go on the end.
    /// </summary>
    void RefreshTabs()
    {
        var open = WindowUtil.CandidateHandles(docked.Select(d => d.Handle).ToHashSet());
        var openSet = open.ToHashSet();
        var windows = new System.Collections.Generic.List<object>();
        foreach (var tab in tabs)
        {
            switch (tab)
            {
                case DockedWindow d when docked.Contains(d):
                    windows.Add(d);
                    break;
                case DockedWindow d when openSet.Contains(d.Handle):
                    windows.Add(new TaskbarEntry(d.Handle));
                    break;
                case TaskbarEntry t when openSet.Contains(t.Handle):
                    t.Refresh();
                    windows.Add(t);
                    break;
                case TaskbarEntry t when docked.FirstOrDefault(d => d.Handle == t.Handle) is DockedWindow now:
                    windows.Add(now);
                    break;
            }
        }
        foreach (var d in docked)
            if (!windows.Contains(d)) windows.Add(d);
        var shown = windows.Select(HandleOf).ToHashSet();
        var appeared = open.Where(h => !shown.Contains(h)).ToList();
        foreach (var h in appeared) windows.Add(new TaskbarEntry(h));

        // Which pin each window belongs to; looked up once per window.
        foreach (var gone in windowIds.Keys.Where(h => !openSet.Contains(h) && !docked.Any(d => d.Handle == h)).ToList())
            windowIds.Remove(gone);
        (string?, string?) IdsOf(IntPtr h)
        {
            if (!windowIds.TryGetValue(h, out var ids))
                windowIds[h] = ids = (ShellIcon.AppIdFor(h), WindowUtil.GetProcessPath(WindowUtil.GetPid(h)));
            return ids;
        }

        var pins = TaskbarPins.Current();
        // A pin that matches a window exactly takes it over one that only shares its exe (a pinned web app over its browser).
        var owner = windows.ToDictionary(w => w, w => IdsOf(HandleOf(w)) is var (id, path)
            ? pins.FirstOrDefault(p => p.Owns(id, path, loose: false)) ?? pins.FirstOrDefault(p => p.Owns(id, path)) : null);
        var wanted = new System.Collections.Generic.List<object>();
        var placed = new System.Collections.Generic.HashSet<object>();
        foreach (var pin in pins)
        {
            var mine = windows.Where(w => owner[w] == pin).ToList();
            if (mine.Count == 0) { wanted.Add(pin); continue; }
            wanted.AddRange(mine);
            placed.UnionWith(mine);
        }
        wanted.AddRange(windows.Where(w => !placed.Contains(w)));

        // Move rather than rebuild, so tabs that stay don't flicker.
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < tabs.Count && ReferenceEquals(tabs[i], wanted[i])) continue;
            int at = tabs.IndexOf(wanted[i]);
            if (at > i) tabs.Move(at, i);
            else tabs.Insert(i, wanted[i]);
        }
        while (tabs.Count > wanted.Count) tabs.RemoveAt(tabs.Count - 1);

        RefreshTrayApps(open);
        // The strip sizes tabs by which one is active, which it can't see change on its own.
        FindChild<TabStripPanel>(TabsList)?.InvalidateMeasure();
        RefreshCollapsedIcons();
    }

    static IntPtr HandleOf(object tab) => tab switch
    {
        DockedWindow d => d.Handle,
        TaskbarEntry t => t.Handle,
        _ => IntPtr.Zero,
    };

    /// <summary>
    /// Starts a pinned app and docks its first window as a tab. Starting goes through the shell, which can take a
    /// while, so it runs off the UI thread; meanwhile the pin pulses and new windows are checked for several times a
    /// second, so the window is docked the moment it shows instead of on the next one-second tick.
    /// </summary>
    void LaunchPin(PinEntry pin)
    {
        if (launching?.Pin == pin) return;
        if (launching?.Pin is PinEntry previous) previous.IsLoading = false;
        // Windows already open aren't the one being started (a second Explorer window, say).
        launchBefore = WindowUtil.CandidateHandles(new System.Collections.Generic.HashSet<IntPtr>()).ToHashSet();
        launchForeground = GetForegroundWindow();
        launchStarted = DateTime.UtcNow;
        // Apps with an updater or a slow start (Discord, Epic) can take a while to show a window.
        // Taking any new window is only safe for a short while after starting, before the user opens something else.
        launching = (pin, DateTime.UtcNow.AddSeconds(pin.AnyNewWindow ? 15 : 45));
        pin.IsLoading = true;
        FindChild<TabStripPanel>(TabsList)?.InvalidateMeasure();
        RefreshCollapsedIcons();
        // Already open, its pin isn't in the strip to pulse (opening a second window from a tab's right-click).
        if (!tabs.Contains(pin)) ShowStatus($"Opening {pin.Title}…");
        launchPoll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => CheckLaunch(), Dispatcher);
        launchPoll.Start();

        System.Threading.Tasks.Task.Run(() =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pin.Launch, pin.Arguments ?? "") { UseShellExecute = true })?.Dispose(); }
            catch
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (launching?.Pin == pin) EndLaunch();
                    ShowStatus($"Couldn't open {pin.Title}");
                });
            }
        });
    }

    void CheckLaunch()
    {
        if (launching is not var (pin, until)) { EndLaunch(); return; }
        if (DateTime.UtcNow > until) { EndLaunch(); return; }
        foreach (var h in WindowUtil.CandidateHandles(launchBefore))
        {
            if (docked.Any(d => d.Handle == h)) continue;
            if (!pin.AnyNewWindow && !pin.Owns(ShellIcon.AppIdFor(h), WindowUtil.GetProcessPath(WindowUtil.GetPid(h)))) continue;
            Docked(h);
            return;
        }
        // An app that's already open (Steam, Discord, a launcher in the tray) doesn't open another window; the second
        // copy hands over to the first, which brings its existing window forward. That window was open before the
        // launch, so it's taken when it comes to the front instead, though only just after the click, before the user
        // could have switched to something else themselves.
        var fg = GetForegroundWindow();
        if (fg != launchForeground && launchBefore.Contains(fg) && DateTime.UtcNow - launchStarted < TimeSpan.FromSeconds(8)
            && WindowUtil.IsCandidate(fg) && !docked.Any(d => d.Handle == fg))
            Docked(fg);

        void Docked(IntPtr h)
        {
            EndLaunch();
            // A fullscreen game would be squashed into the island; it's only switched to, like its tab.
            if (!WindowUtil.IsFullscreen(h)) DockWindow(h, null);
            else RefreshTabs();
        }
    }

    void EndLaunch()
    {
        launchPoll?.Stop();
        if (launching?.Pin is PinEntry pin) pin.IsLoading = false;
        launching = null;
        FindChild<TabStripPanel>(TabsList)?.InvalidateMeasure();
        RefreshCollapsedIcons();
    }

    /// <summary>Keeps the tray dropdown's list current. Finding them walks the process list, so only every few seconds.</summary>
    void RefreshTrayApps(System.Collections.Generic.List<IntPtr> open)
    {
        if (DateTime.UtcNow - trayCheckedAt < TimeSpan.FromSeconds(3)) return;
        trayCheckedAt = DateTime.UtcNow;

        var windowPaths = open.Concat(docked.Select(d => d.Handle))
            .Select(h => WindowUtil.GetProcessPath(WindowUtil.GetPid(h)))
            .OfType<string>();
        var apps = TrayApps.Running(windowPaths);
        var wanted = apps.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = trayApps.Count - 1; i >= 0; i--)
            if (!wanted.Contains(trayApps[i].Path)) trayApps.RemoveAt(i);
        var shown = trayApps.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, title) in apps)
            if (!shown.Contains(path)) trayApps.Add(new TrayEntry(path, title));
    }

    void TrayButton_Click(object sender, RoutedEventArgs e)
    {
        trayOpen = !trayOpen;
        pickerOpen = mixerOpen = searchOpen = spotsOpen = updateOpen = false;
        if (trayOpen) trayCheckedAt = DateTime.MinValue; // list it fresh
    }

    // Starting a tray app's exe again makes the running copy show its window; it shows up as a tab then.
    void TrayItem_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not TrayEntry r) return;
        trayOpen = false;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(r.Path) }); }
        catch { ShowStatus($"Couldn't open {r.Title}"); }
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is T deeper) return deeper;
        }
        return null;
    }

    /// <summary>
    /// The open apps' icons (not closed pins) at the right of the pill, each on its own rounded tile. The pill grows to
    /// fit them, from just the clock up to <see cref="CollapsedMaxLong"/>; fewer apps get wider gaps. Past the maximum
    /// the tiles overlap like a hand of cards with the active one on top, and past the tightest packing the rest
    /// become a "+N" count.
    /// </summary>
    void RefreshCollapsedIcons()
    {
        CollapsedIcons.Children.Clear();
        var apps = tabs.Where(t => t is not PinEntry p || p.IsLoading).ToList();
        // Lengths here run along the pill: across it when it's on the top or bottom edge, down it on a side edge.
        bool v = Vertical;
        double pad = v ? 12 : 14;
        const double card = 24, glyph = 16, lead = 12, minStep = 10;
        double countW = v ? 16 : 22;
        double gap = Math.Clamp(10 - apps.Count, 5, 8);

        // The clock, Discord status and wave, which always show.
        ClockText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Shell's border takes a pixel off each end, which would otherwise come out of the clock when it's alone.
        double fixedW = 2 * pad + 2 + (v ? ClockText.DesiredSize.Height : ClockText.DesiredSize.Width)
            + (DiscordPanel.Visibility == Visibility.Visible ? 64 : 0)
            + CallLength(v)
            + MiniPlayerLength(v)
            + (CollapsedWave.Visibility == Visibility.Visible ? 22 : 0);
        if (apps.Count == 0) { collapsedLong = Math.Ceiling(fixedW); return; }

        double room = CollapsedMaxLong - fixedW - lead, natural = apps.Count * card + (apps.Count - 1) * gap;
        int fit = apps.Count;
        double step = card + gap;
        if (natural > room)
        {
            fit = 1 + (int)((room - card) / minStep);
            if (apps.Count > fit) fit = Math.Max(1, 1 + (int)((room - card - countW) / minStep)); // leave space for the count
            step = fit > 1 ? Math.Clamp((room - card - (apps.Count > fit ? countW : 0)) / (fit - 1), minStep, card + gap) : 0;
        }
        int overflow = apps.Count - fit;
        collapsedLong = Math.Ceiling(fixedW + lead + card + (fit - 1) * step + (overflow > 0 ? countW : 0));

        var shown = apps.Take(fit).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            var tab = shown[i];
            bool isActive = tab == active;
            var icon = new System.Windows.Controls.Image
            {
                Source = tab switch { DockedWindow d => d.Icon, TaskbarEntry t => t.Icon, PinEntry p => p.Icon, _ => null },
                Width = glyph, Height = glyph, Opacity = isActive ? 1 : 0.85,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var chip = new Border
            {
                Width = card, Height = card, CornerRadius = new CornerRadius(7),
                // The active app's tile is a touch brighter. Opaque enough that overlapping tiles still read apart.
                Background = new SolidColorBrush(isActive ? Color.FromArgb(0xFF, 0x3A, 0x3A, 0x40) : Color.FromArgb(0xFF, 0x26, 0x26, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(isActive ? (byte)0x40 : (byte)0x1F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true,
                Margin = i == 0 ? new Thickness(0) : v ? new Thickness(0, step - card, 0, 0) : new Thickness(step - card, 0, 0, 0),
                Child = icon, Cursor = Cursors.Hand, Tag = tab,
            };
            chip.MouseLeftButtonUp += (_, e) => { if (Gestured) return; e.Handled = true; OpenInto(tab); };
            // Later cards lie over earlier ones, like a hand of cards, always in the same order so each one's
            // visible strip stays where it is and can be clicked; the active app shows by its brighter tile instead.
            Panel.SetZIndex(chip, i);
            CollapsedIcons.Children.Add(chip);
        }
        if (overflow > 0)
            CollapsedIcons.Children.Add(new TextBlock
            {
                Text = "+" + overflow, Foreground = (Brush)new BrushConverter().ConvertFrom("#B3FFFFFF")!,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = v ? new Thickness(0, 2, 0, 0) : new Thickness(4, 0, 0, 0),
            });
    }

    void ShowStatus(string text)
    {
        StatusText.Text = text;
        statusTicks = 4;
    }

    // ---------------------------------------------------------------- input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        for (var el = e.OriginalSource as DependencyObject; el != null; el = VisualTreeHelper.GetParent(el))
            if (el is Slider) return;
        GetCursorPos(out dragStart);
        pressTick = Environment.TickCount64;
        dragPending = true;
        moveArmed = pressMoved = false;
    }

    // ---------------------------------------------------------------- closed island quick actions

    void Collapsed_Click(object sender, MouseButtonEventArgs e)
    {
        if (Gestured || expanded) return;
        openRequested = true;
    }

    /// <summary>Pulls the closed island down into a card for a Windows notification, iOS fashion.</summary>
    internal void ShowNotice(Notice n)
    {
        // Another one while a card is up: the card bounces to show it's new.
        if (notice != null && !expanded) vh += 900 * VisualTreeHelper.GetDpi(this).DpiScaleY;
        notice = n;
        noticeUntil = null;
        NoticeApp.Text = n.App;
        NoticeTitle.Text = n.Title;
        NoticeBody.Text = n.Body;
        NoticeBody.Visibility = n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeIcon.Source = n.Icon;
        NoticeGlyph.Visibility = n.Icon == null ? Visibility.Visible : Visibility.Collapsed;
        Wake();
    }

    /// <summary>Opens the app the notification is from: its tab, its window, or starts it.</summary>
    void Notice_Click(object sender, MouseButtonEventArgs e)
    {
        if (Gestured || notice is not Notice n) return;
        e.Handled = true;
        notice = null;
        var h = n.AppId == null ? IntPtr.Zero
            : windowIds.FirstOrDefault(w => w.Value.AppId == n.AppId && IsWindow(w.Key)).Key;
        if (docked.FirstOrDefault(d => d.Handle == h) is DockedWindow d) OpenInto(d);
        else if (h != IntPtr.Zero)
        {
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            ForceForeground(h);
        }
        else if (n.AppId is string id && !id.StartsWith("NotifyIconGeneratedAumid", StringComparison.Ordinal))
            try { System.Diagnostics.Process.Start("explorer.exe", "shell:AppsFolder\\" + id); } catch { }
        Wake();
    }

    void Notice_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // not the island's menu
        notice = null;
        Wake();
    }

    void DiscordMic_Click(object sender, MouseButtonEventArgs e)
    {
        if (Gestured) return;
        e.Handled = true;
        discord.Toggle(deafenButton: false);
    }

    void DiscordHeadset_Click(object sender, MouseButtonEventArgs e)
    {
        if (Gestured) return;
        e.Handled = true;
        discord.Toggle(deafenButton: true);
    }

    /// <summary>Brings Discord up (its tab, if docked) and opens its screen share picker.</summary>
    void DiscordShare_Click(object sender, MouseButtonEventArgs e)
    {
        if (Gestured) return;
        e.Handled = true;
        ShowDiscordWindow();
        discord.OpenScreenShare();
        // Discord re-lays itself out for the picker, sometimes at a stale size.
        if (docked.FirstOrDefault(d => d.App == "discord") is DockedWindow d) { NudgeSoon(d, 700); NudgeSoon(d, 1500); }
    }

    /// <summary>Brings Discord up: its tab if it's docked, otherwise its own window.</summary>
    void ShowDiscordWindow()
    {
        if (docked.FirstOrDefault(d => d.App == "discord") is DockedWindow d) OpenInto(d);
        else if (DiscordStatus.FindWindow() is var h && h != IntPtr.Zero)
        {
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            ForceForeground(h);
        }
        else
        {
            // Closed to the tray: its link scheme brings the window back.
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("discord://") { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>Opens the island into the tab whose app is loudest right now, i.e. the one the wave is showing.</summary>
    void CollapsedWave_Click(object sender, MouseButtonEventArgs e)
    {
        if (Gestured) return;
        e.Handled = true;
        var source = tabs.Where(t => t is DockedWindow or TaskbarEntry)
            .Select(t => (tab: t, level: levels.For(WindowUtil.GetPid(HandleOf(t)))))
            .Where(x => x.level > 0).OrderByDescending(x => x.level).FirstOrDefault().tab;
        if (source != null) OpenInto(source);
        else openRequested = true;
    }

    /// <summary>A closed-island app icon: open the island with that app's tab showing.</summary>
    void OpenInto(object tab)
    {
        switch (tab)
        {
            case DockedWindow d:
                Activate(d);
                break;
            // Same as clicking its tab: a fullscreen game or a window that won't dock is just brought forward.
            case TaskbarEntry t when WindowUtil.IsFullscreen(t.Handle) || !DockWindow(t.Handle, null):
                if (IsIconic(t.Handle)) ShowWindow(t.Handle, SW_RESTORE);
                SetForegroundWindow(t.Handle);
                return;
        }
        openRequested = true;
    }

    /// <summary>
    /// Dragging a file over an open app's icon, on the closed pill or in the tab strip, opens that app's tab after a
    /// moment so the file can be dropped into it, like holding a file over a taskbar button.
    /// </summary>
    void OnFileDragOver(object sender, DragEventArgs e)
    {
        object? tab = null;
        bool text = false;
        for (var el = InputHitTest(e.GetPosition(this)) as DependencyObject; el != null && tab == null; el = VisualTreeHelper.GetParent(el))
        {
            if (el is TextBox) text = true;
            if (el is FrameworkElement fe)
                tab = fe.Tag is DockedWindow or TaskbarEntry ? fe.Tag : fe.DataContext is DockedWindow or TaskbarEntry ? fe.DataContext : null;
        }
        // The island itself takes no files, except as text in its search box.
        if (!text) { e.Effects = DragDropEffects.None; e.Handled = true; }

        if (!ReferenceEquals(tab, fileHoverTab))
        {
            fileHoverTab = tab;
            fileHoverSince = Environment.TickCount64;
        }
        else if (tab != null && Environment.TickCount64 - fileHoverSince >= FileHoverMs)
        {
            fileHoverSince = long.MaxValue; // once per hover
            if (tab != active || !expanded) OpenInto(tab);
        }
    }

    void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (Gestured) return;
        switch (((FrameworkElement)sender).DataContext)
        {
            case DockedWindow d:
                Activate(d);
                break;
            case PinEntry p:
                LaunchPin(p);
                break;
            // A fullscreen game would be squashed into the island, so just switch to it; same if it won't dock.
            case TaskbarEntry t when WindowUtil.IsFullscreen(t.Handle) || !DockWindow(t.Handle, null):
                if (IsIconic(t.Handle)) ShowWindow(t.Handle, SW_RESTORE);
                SetForegroundWindow(t.Handle);
                break;
        }
    }

    /// <summary>
    /// A tab's menu: a new window (what clicking its taskbar pin does, so apps with more than one window get another
    /// tab), pinning or unpinning its app, and closing it. A closed pin's menu opens or unpins it.
    /// </summary>
    void Tab_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // not the island's own menu
        var tab = ((FrameworkElement)sender).DataContext;
        var m = new ContextMenu();
        if (tab is PinEntry p)
        {
            var start = new MenuItem { Header = "Open " + p.Title };
            start.Click += (_, _) => LaunchPin(p);
            m.Items.Add(start);
            m.Items.Add(UnpinItem(p));
        }
        else
        {
            var h = HandleOf(tab);
            if (h == IntPtr.Zero) return;
            string? appId = ShellIcon.AppIdFor(h), path = WindowUtil.GetProcessPath(WindowUtil.GetPid(h));
            var pins = TaskbarPins.Current();
            var mine = pins.FirstOrDefault(pin => pin.Owns(appId, path, loose: false)) ?? pins.FirstOrDefault(pin => pin.Owns(appId, path));

            var open = new MenuItem { Header = "New window" };
            open.Click += (_, _) => NewWindow(h);
            m.Items.Add(open);
            if (mine != null) m.Items.Add(UnpinItem(mine));
            else if (appId != null || path != null)
            {
                var pin = new MenuItem { Header = "Pin to taskbar" };
                pin.Click += (_, _) => { TaskbarPins.PinWindow(appId, path); RefreshPins(); };
                m.Items.Add(pin);
            }
            foreach (var other in Islands.All.Where(i => i != this))
            {
                var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == other.Display);
                if (screen == null) continue;
                var send = new MenuItem { Header = "Pin to island on " + Islands.Label(screen) };
                send.Click += (_, _) => SendToIsland(tab, other);
                m.Items.Add(send);
            }
            var close = new MenuItem { Header = "Close window" };
            close.Click += (_, _) => CloseTab(h);
            var kill = new MenuItem { Header = "End process" };
            kill.Click += (_, _) => KillTab(h);
            m.Items.Add(new Separator());
            m.Items.Add(close);
            m.Items.Add(kill);
        }
        Dress(m);
        m.PlacementTarget = (UIElement)sender;
        m.IsOpen = true;
    }

    /// <summary>Moves a tab to another display's island and opens it there; a window not docked yet docks there.</summary>
    void SendToIsland(object tab, IslandWindow target)
    {
        if (tab is DockedWindow d)
        {
            if (!docked.Contains(d)) return;
            if (!d.MoveTo(target.host.Handle))
            {
                ShowStatus("Couldn't move that window.");
                return;
            }
            Remove(d, () => { });
            target.docked.Add(d);
        }
        target.OpenInto(tab);
        RefreshTabs();
    }

    MenuItem UnpinItem(PinEntry pin)
    {
        var unpin = new MenuItem { Header = "Unpin from taskbar" };
        unpin.Click += (_, _) => { TaskbarPins.Unpin(pin); RefreshPins(); };
        return unpin;
    }

    /// <summary>Pins are shared, so every display's island shows the change straight away.</summary>
    static void RefreshPins()
    {
        foreach (var island in Islands.All) island.RefreshTabs();
    }

    void NewWindow(IntPtr h)
    {
        string? appId = ShellIcon.AppIdFor(h), path = WindowUtil.GetProcessPath(WindowUtil.GetPid(h));
        if (TaskbarPins.Current().FirstOrDefault(pin => pin.Owns(appId, path)) is PinEntry owner) LaunchPin(owner);
        // Not pinned: start its exe the same way.
        else if (path != null) LaunchPin(new PinEntry(path, appId, path, System.IO.Path.GetFileNameWithoutExtension(path)));
    }

    void CloseTab(IntPtr h)
    {
        if (docked.FirstOrDefault(d => d.Handle == h) is DockedWindow d) Remove(d, d.Close);
        else PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Ends the window's whole app. Explorer and ApplicationFrameHost host other windows (the shell, every UWP app),
    /// so for those only the window is closed.
    /// </summary>
    void KillTab(IntPtr h)
    {
        uint pid = WindowUtil.GetPid(h);
        string exe = System.IO.Path.GetFileNameWithoutExtension(WindowUtil.GetProcessPath(pid) ?? "");
        if (exe.Equals("explorer", StringComparison.OrdinalIgnoreCase) || exe.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            CloseTab(h);
            return;
        }
        if (docked.FirstOrDefault(d => d.Handle == h) is DockedWindow d) Remove(d, () => { });
        try { System.Diagnostics.Process.GetProcessById((int)pid).Kill(entireProcessTree: true); }
        catch (Exception ex) { ShowStatus("Couldn't end " + (exe.Length > 0 ? exe : "it") + ": " + ex.Message); }
    }

    void TabUndock_Click(object sender, RoutedEventArgs e)
    {
        if (HandleOf(((FrameworkElement)sender).DataContext) is var h && h != IntPtr.Zero) CloseTab(h);
        e.Handled = true;
    }

    void AddButton_Click(object sender, RoutedEventArgs e)
    {
        pickerOpen = !pickerOpen;
        mixerOpen = searchOpen = trayOpen = spotsOpen = updateOpen = false;
        if (pickerOpen)
            PickerList.ItemsSource = WindowUtil.EnumerateCandidates(docked.Select(d => d.Handle).ToHashSet());
    }

    void PickerItem_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is WindowInfo info) DockWindow(info.Handle, null);
        pickerOpen = false;
    }

    void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (active != null) Undock(active);
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        pinned = !pinned;
        PinButton.Content = pinned ? "" : "";
        PinButton.Foreground = pinned ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
        PinButton.ToolTip = pinned ? "Unpin" : "Keep expanded";
    }

    void Menu_Click(object sender, RoutedEventArgs e)
    {
        menu.PlacementTarget = MenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- lifetime

    void SaveSettings()
    {
        settings.Displays[Display] = new Placement { Edge = edge.ToString(), Along = along };
        settings.Save();
    }

    // ---------------------------------------------------------------- keyboard

    internal bool HasTabs => docked.Count > 0;

    bool OwnsForeground()
    {
        var fg = GetForegroundWindow();
        return fg == hwnd || fg == host.Handle || active is { Floating: true } && fg == active.Handle;
    }

    void OpenFromKeyboard()
    {
        keyOpened = true;
        keyOpenedAt = clock.Elapsed;
        leaveSince = keyOpenedAt; // or an old one would close it again straight away
        if (!expanded) openRequested = true;
        Wake();
    }

    /// <summary>Alt+Tab: opens the island on the tab it's showing, and each press after that steps to the next tab
    /// (Shift: the one before), handing it the keyboard.</summary>
    internal void CycleTabs(bool back)
    {
        if (exiting) return;
        if (docked.Count == 0)
        {
            // Nothing to step through: just the island, which takes the keyboard so it stays open.
            if (!expanded) { OpenFromKeyboard(); ForceForeground(hwnd); }
            return;
        }
        int n = docked.Count, i = active == null ? -1 : docked.IndexOf(active);
        if (i < 0) Activate(docked[back ? n - 1 : 0]);
        else if (expanded) Activate(docked[((i + (back ? -1 : 1)) % n + n) % n]);
        OpenFromKeyboard();
    }

    /// <summary>The Windows key: opens the island, or closes it if it's open.</summary>
    internal void ToggleFromKeyboard()
    {
        if (exiting) return;
        DismissStartSoon();
        if (expanded && !pinned)
        {
            keyOpened = false;
            closeRequested = true;
            Wake();
            return;
        }
        OpenFromKeyboard();
        // With no tab to give the keyboard to, the island takes it, so it stays open until clicked away from.
        if (active == null) ForceForeground(hwnd);
    }

    // Windows 11 opens Start for the Windows key even when the key never reaches it (the hook holds it back), so if
    // Start comes up just after the island took the key, it's closed again and the keyboard goes back to the island.
    DispatcherTimer? startWatch;
    TimeSpan startWatchUntil;

    void DismissStartSoon()
    {
        startWatchUntil = clock.Elapsed + TimeSpan.FromSeconds(1.5);
        startWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Normal, (_, _) => CheckStart(), Dispatcher);
        startWatch.Start();
    }

    void CheckStart()
    {
        if (clock.Elapsed > startWatchUntil) { startWatch!.Stop(); return; }
        if (!IsStartMenu(GetForegroundWindow())) return;
        startWatch!.Stop();
        KeyboardHook.Tap(0x1B); // Escape closes Start, and the app it covered gets the keyboard back
        if (!expanded && !openRequested) return;
        OpenFromKeyboard(); // Start had the keyboard for a moment, which would have closed it
        ForceForeground(active is { Floating: true } ? active.Handle : active != null ? host.Handle : hwnd);
    }

    static bool IsStartMenu(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)WindowUtil.GetPid(h));
            return p.ProcessName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                || p.ProcessName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal bool HasDockedExplorer => docked.Any(d => IsExplorer(d.Handle));
    internal IntPtr DockedDiscord => docked.FirstOrDefault(d => d.App == "discord")?.Handle ?? IntPtr.Zero;

    /// <summary>Closes every island and the program.</summary>
    public void Exit() => Islands.ExitAll();

    /// <summary>Closes this island alone (its display went away or was turned off, or the program is closing),
    /// giving its docked windows back first.</summary>
    internal void Shut()
    {
        if (exiting) return;
        exiting = true;
        CompositionTarget.Rendering -= OnRender;
        idleTick?.Stop();
        tick.Stop();
        launchPoll?.Stop();
        StopMedia();
        UndockAll();
        if (winHook != IntPtr.Zero) UnhookWinEvent(winHook);
        if (showHook != IntPtr.Zero) UnhookWinEvent(showHook);
        SaveSettings();
        host.AllowClose = true;
        host.Close();
        Close();
    }
}
