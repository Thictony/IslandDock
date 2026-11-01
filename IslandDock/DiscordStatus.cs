using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace IslandDock;

/// <summary>Someone in the Discord voice call, as the overlay shows them. Avatar is their profile picture, when it could be read.</summary>
internal sealed record CallMember(string Name, bool Speaking, bool Muted, bool Deafened, BitmapSource? Avatar = null, bool Streaming = false);

/// <summary>
/// Reads Discord's mute/deafen state from its accessibility tree (the buttons in the user panel), on a
/// background thread since UI Automation calls into Discord can be slow. Discord has no local API for this
/// that doesn't need an approved OAuth app, so if its UI isn't exposed the state is simply unknown.
/// </summary>
internal sealed class DiscordStatus
{
    static readonly string[] Names = { "Mute", "Unmute", "Deafen", "Undeafen" };
    static readonly Condition ButtonNames = new OrCondition(
        Names.Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n)).ToArray());

    AutomationElement? mute, deafen;

    /// <summary>Discord's window while it's docked; it isn't the process's main window then.</summary>
    public volatile IntPtr DockedHandle;

    /// <summary>
    /// (muted, deafened, sharing screen); the first two are null when Discord isn't running or its state can't be read.
    /// Raised off the UI thread.
    /// </summary>
    public event Action<bool?, bool?, bool>? Changed;
    /// <summary>What <see cref="Changed"/> last said, for an island opened since.</summary>
    public (bool? Muted, bool? Deafened, bool Sharing) State { get; private set; }

    readonly AutoResetEvent poke = new(false);

    public void Start() => new Thread(Loop) { IsBackground = true, Name = "Discord status" }.Start();

    /// <summary>Presses Discord's own mute or deafen button, off the UI thread, then reads the new state straight away.</summary>
    public void Toggle(bool deafenButton) => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            if ((deafenButton ? deafen : mute) is not AutomationElement e) return;
            if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) ((InvokePattern)p).Invoke();
            else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out p)) ((TogglePattern)p).Toggle();
        }
        catch { }
        poke.Set();
    });

    /// <summary>
    /// Presses Discord's "Share Your Screen" button, which opens its picker of screens and windows to stream.
    /// It only exists while in a voice call; otherwise this does nothing and Discord is just brought up.
    /// </summary>
    public void OpenScreenShare() => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            var h = DockedHandle;
            if (h == IntPtr.Zero) h = FindWindow();
            if (h == IntPtr.Zero) return;
            // Discord renders the button as the window comes up, so give it a few tries.
            for (int i = 0; i < 10; i++)
            {
                foreach (AutomationElement e in AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    var name = e.Current.Name;
                    if (!name.Contains("Share", StringComparison.OrdinalIgnoreCase) || !name.Contains("Screen", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) ((InvokePattern)p).Invoke();
                    return;
                }
                Thread.Sleep(200);
            }
        }
        catch { }
    });

    void Loop()
    {
        bool? lastMuted = null, lastDeafened = null;
        bool lastSharing = false;
        bool first = true;
        int round = 0;
        while (true)
        {
            var (m, d) = Read();
            // Looking for the stream button searches all of Discord's window, which costs Discord too; less often.
            bool s = round++ % 3 == 0 ? Streaming() || Sharing() : lastSharing;
            if (first || m != lastMuted || d != lastDeafened || s != lastSharing)
            {
                first = false;
                (lastMuted, lastDeafened, lastSharing) = (m, d, s);
                State = (m, d, s);
                Changed?.Invoke(m, d, s);
            }
            poke.WaitOne(1500);
        }
    }

    static readonly Condition StopStreamingButton = new PropertyCondition(AutomationElement.NameProperty, "Stop Streaming");

    /// <summary>While Discord streams, its call controls have a "Stop Streaming" button. A quick lookup, even in a big tree.</summary>
    bool Streaming()
    {
        try
        {
            var h = DockedHandle;
            if (h == IntPtr.Zero) h = FindWindow();
            return h != IntPtr.Zero && AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants, StopStreamingButton) != null;
        }
        catch { return false; }
    }

    const string CaptureStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\graphicsCaptureProgrammatic\NonPackaged";

    /// <summary>
    /// Whether Discord is capturing the screen. Windows logs each app's use of screen capture the way it does the
    /// microphone's, with no stop time while it's still capturing. Some Discord versions don't capture that way.
    /// </summary>
    static bool Sharing()
    {
        try
        {
            using var store = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(CaptureStore);
            if (store == null) return false;
            foreach (var p in Process.GetProcessesByName("Discord"))
            {
                using (p)
                {
                    string? path;
                    try { path = p.MainModule?.FileName; } catch { continue; }
                    if (path == null) continue;
                    using var app = store.OpenSubKey(path.Replace('\\', '#'));
                    if (app?.GetValue("LastUsedTimeStop") is long stop && stop == 0
                        && app.GetValue("LastUsedTimeStart") is long start && start != 0)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    (bool?, bool?) Read()
    {
        try
        {
            if (mute != null)
            {
                try { return (IsOn(mute), deafen == null ? null : IsOn(deafen)); }
                catch { mute = deafen = null; } // Discord re-rendered the panel; find the buttons again.
            }

            var h = DockedHandle;
            if (h == IntPtr.Zero) h = FindWindow();
            if (h == IntPtr.Zero) return (null, null);

            foreach (AutomationElement e in AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, ButtonNames))
            {
                var name = e.Current.Name;
                if (name.EndsWith("mute", StringComparison.OrdinalIgnoreCase)) mute ??= e;
                else deafen ??= e;
            }
            if (mute == null) return (null, null);
            return (IsOn(mute), deafen == null ? null : IsOn(deafen));
        }
        catch { mute = deafen = null; return (null, null); }
    }

    public static IntPtr FindWindow() => FindWindow(overlay: false);

    /// <summary>Discord's main window, or the in-game overlay's, which is a separate process's main window.</summary>
    static IntPtr FindWindow(bool overlay)
    {
        IntPtr found = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName("Discord"))
        {
            try
            {
                if (found == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero && (p.MainWindowTitle == OverlayTitle) == overlay)
                    found = p.MainWindowHandle;
            }
            catch { }
            p.Dispose();
        }
        return found;
    }

    // ---------------------------------------------------------------- who's in the call

    const string OverlayTitle = "Discord Overlay";

    /// <summary>Everyone in the voice call, in Discord's order; empty when not in one. Raised off the UI thread.</summary>
    public event Action<IReadOnlyList<CallMember>>? CallChanged;
    /// <summary>What <see cref="CallChanged"/> last said.</summary>
    public IReadOnlyList<CallMember> Call { get; private set; } = new List<CallMember>();

    public void StartCall() => new Thread(CallLoop) { IsBackground = true, Name = "Discord call" }.Start();

    /// <summary>
    /// The call is read from the overlay's voice widget, which Discord keeps rendered whenever it's in a call (as long
    /// as its overlay is turned on) and which lists everyone with their state, whatever the main window is showing.
    /// It's small, so it can be read often enough to follow who's talking.
    /// </summary>
    void CallLoop()
    {
        AutomationElement? root = null;
        IntPtr overlay = IntPtr.Zero;
        var last = new List<CallMember>();
        bool first = true;
        while (true)
        {
            var now = new List<CallMember>();
            var rows = new List<(string Name, System.Windows.Rect Rect)>();
            try
            {
                if (root == null && FindWindow(overlay: true) is var h && h != IntPtr.Zero) { overlay = h; root = AutomationElement.FromHandle(h); }
                if (root != null)
                    foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                        if (e.Current.ClassName.StartsWith("voiceUser", StringComparison.Ordinal) && ReadMember(e) is CallMember m)
                        {
                            now.Add(m);
                            rows.Add((m.Name, e.Current.BoundingRectangle));
                        }
                if (now.Count > 0) { if (rows.Any(r => Stale(r.Name))) CaptureAvatars(overlay, rows); }
                else now = CallFromMain();
                if (now.Any(m => Known(m.Name).Image is not { PixelWidth: >= SharpSize }))
                    CaptureFromMain(now.Select(m => m.Name).ToList());
                for (int i = 0; i < now.Count; i++) now[i] = now[i] with { Avatar = Known(now[i].Name).Image };
            }
            catch { root = null; }
            // Out of a call, look for the overlay afresh now and then: Discord recreates it.
            if (now.Count == 0) root = null;
            if (first || !now.SequenceEqual(last))
            {
                first = false;
                last = now;
                Call = now;
                CallChanged?.Invoke(now);
            }
            Thread.Sleep(root == null ? 3000 : 250);
        }
    }

    static readonly Condition UserPanel = new PropertyCondition(AutomationElement.NameProperty, "User status and settings");
    static readonly Condition DisconnectButton = new PropertyCondition(AutomationElement.NameProperty, "Disconnect");
    static readonly Condition ProfileButton = new PropertyCondition(AutomationElement.NameProperty, "Manage profile and status");
    static readonly Condition Groups = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group);
    static readonly Condition ListItems = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);

    /// <summary>The call as last seen in the main window's channel list, and the channel it was in.</summary>
    List<CallMember> seenCall = new();
    string? seenChannel;

    /// <summary>
    /// Without the overlay (no game running, or one Discord doesn't draw over), the call is read from the main window's
    /// channel list: the voice channel that lists you. The list doesn't say who's talking, or who's muted apart from you
    /// (that's read from your own buttons).
    /// </summary>
    /// <remarks>
    /// The list is only there while the window shows the call's server, so while you look elsewhere the call is the one
    /// last seen, for as long as Discord says you're still in that channel. Nothing else outside the overlay says who's
    /// in it: Discord's log notes people joining, by ID, but not leaving.
    /// </remarks>
    List<CallMember> CallFromMain()
    {
        var call = new List<CallMember>();
        try
        {
            var h = DockedHandle;
            if (h == IntPtr.Zero) h = FindWindow();
            if (h == IntPtr.Zero) return call;
            var main = AutomationElement.FromHandle(h);
            // The panel at the bottom left has a Disconnect button while in a call, and your name by your picture.
            if (main.FindFirst(TreeScope.Descendants, UserPanel) is not AutomationElement panel
                || panel.FindFirst(TreeScope.Descendants, DisconnectButton) == null
                || panel.FindFirst(TreeScope.Descendants, ProfileButton) is not AutomationElement profile
                || Raw.GetNextSibling(profile) is not AutomationElement account)
            {
                seenCall = new();
                seenChannel = null;
                return call;
            }
            string self = FirstText(account, 0)?.Trim() ?? "";
            if (self.Length == 0) return call;
            // "Voice Channel vc", over the server's name: which call this is, whatever the window shows.
            string inChannel = string.Join("/", Texts(panel, 0).Where(t => t.StartsWith("Voice Channel", StringComparison.Ordinal)));

            // A voice channel's list item is named after everything in it, members included.
            foreach (AutomationElement channel in main.FindAll(TreeScope.Descendants, ListItems))
            {
                var label = channel.Current.Name;
                if (!label.StartsWith("Voice", StringComparison.Ordinal) || !label.Contains(self, StringComparison.Ordinal)) continue;
                foreach (AutomationElement row in channel.FindAll(TreeScope.Descendants, Groups))
                    if (row.Current.ClassName.StartsWith("voiceUser", StringComparison.Ordinal) && ReadMember(row) is CallMember m)
                        call.Add(m);
                if (call.Any(m => m.Name == self)) break;
                call.Clear();
            }
            if (call.Count > 0) { seenCall = call.ToList(); seenChannel = inChannel; }
            else if (seenChannel == inChannel &&seenCall.Any(m => m.Name == self)) call = seenCall.ToList();
            else return call;

            bool muted = false, deafened = false;
            foreach (AutomationElement b in panel.FindAll(TreeScope.Descendants, ButtonNames))
                if (b.Current.Name.EndsWith("mute", StringComparison.OrdinalIgnoreCase)) muted |= IsOn(b);
                else deafened |= IsOn(b);
            int i = call.FindIndex(m => m.Name == self);
            call[i] = call[i] with { Muted = call[i].Muted || muted || deafened, Deafened = call[i].Deafened || deafened };
        }
        catch { call.Clear(); }
        return call;
    }

    /// <summary>The first text under an element. Discord's texts only show up walking the raw tree, not to FindFirst.</summary>
    static string? FirstText(AutomationElement e, int depth)
    {
        if (e.Current.ControlType == ControlType.Text) return e.Current.Name;
        if (depth > 6) return null;
        for (var ch = Raw.GetFirstChild(e); ch != null; ch = Raw.GetNextSibling(ch))
            if (FirstText(ch, depth + 1) is string s) return s;
        return null;
    }

    /// <summary>Every text under an element, walking the raw tree like <see cref="FirstText"/>.</summary>
    static IEnumerable<string> Texts(AutomationElement e, int depth)
    {
        if (e.Current.ControlType == ControlType.Text) { yield return e.Current.Name; yield break; }
        if (depth > 8) yield break;
        for (var ch = Raw.GetFirstChild(e); ch != null; ch = Raw.GetNextSibling(ch))
            foreach (var s in Texts(ch, depth + 1)) yield return s;
    }

    // ---------------------------------------------------------------- profile pictures

    /// <summary>How long a captured picture is kept before it's taken again, in case it changed or hadn't loaded yet.</summary>
    const long AvatarLifetime = 30_000;

    readonly Dictionary<string, (BitmapSource? Image, long Taken)> avatars = new();

    /// <summary>Pictures are kept on disk too, so they show straight away next time, even while the overlay isn't drawing.</summary>
    static readonly string AvatarDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandDock", "avatars");

    static string AvatarFile(string name) =>
        System.IO.Path.Combine(AvatarDir, string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".png");

    /// <summary>The picture known for them, read from disk the first time they're seen.</summary>
    (BitmapSource? Image, long Taken) Known(string name)
    {
        if (!avatars.TryGetValue(name, out var a)) avatars[name] = a = (LoadAvatar(name), 0);
        return a;
    }

    bool Stale(string name) => Environment.TickCount64 - Known(name).Taken > AvatarLifetime;

    static BitmapSource? LoadAvatar(string name)
    {
        try
        {
            var file = AvatarFile(name);
            if (!System.IO.File.Exists(file)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    static void SaveAvatar(string name, BitmapSource image)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AvatarDir);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = System.IO.File.Create(AvatarFile(name));
            png.Save(file);
        }
        catch { }
    }

    /// <summary>
    /// Discord's accessibility tree leaves profile pictures out, but its overlay window draws them at the left of each
    /// row, and the overlay can be drawn into a bitmap even though it's invisible on the desktop. Each row starts with
    /// its picture, which <see cref="FindPicture"/> locates.
    /// </summary>
    void CaptureAvatars(IntPtr overlay, List<(string Name, System.Windows.Rect Rect)> rows)
    {
        if (!GetWindowRect(overlay, out var win)) return;
        int w = win.Right - win.Left, h = win.Bottom - win.Top;
        if (w <= 0 || h <= 0) return;
        IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        ReleaseDC(IntPtr.Zero, screen);
        var old = SelectObject(dc, bmp);
        try
        {
            if (!PrintWindow(overlay, dc, PW_RENDERFULLCONTENT)) return;
            SelectObject(dc, old);
            long now = Environment.TickCount64;
            foreach (var (name, r) in rows)
            {
                // The start of the row, a little wider than tall, holds the picture with some padding around it.
                int rh = (int)r.Height, rw = Math.Min((int)r.Width, rh + rh / 4);
                int x = (int)r.X - win.Left, y = (int)r.Y - win.Top;
                if (rh <= 0 || x < 0 || y < 0 || x + rw > w || y + rh > h) continue;
                // PrintWindow leaves the alpha byte unset, so read the pixels as opaque.
                var row = new FormatConvertedBitmap(
                    Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, new System.Windows.Int32Rect(x, y, rw, rh),
                                                          BitmapSizeOptions.FromEmptyOptions()),
                    System.Windows.Media.PixelFormats.Bgr32, null, 0);
                row.Freeze();
                // The overlay only draws while Discord shows it; otherwise keep the picture already known, and keep a
                // sharper one from the main window rather than replace it with the overlay's small one.
                var known = avatars.GetValueOrDefault(name).Image;
                if (Blank(row) || FindPicture(row) is not System.Windows.Int32Rect pic || known?.PixelWidth > pic.Width)
                {
                    avatars[name] = (known, now);
                    continue;
                }
                var image = new CroppedBitmap(row, pic);
                image.Freeze();
                avatars[name] = (image, now);
                SaveAvatar(name, image);
            }
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
        }
    }

    /// <summary>Pictures smaller than this (in pixels) are worth replacing with the main window's call tiles.</summary>
    const int SharpSize = 40;

    static readonly Condition Images = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image);
    long mainTried;

    /// <summary>
    /// The overlay only draws in-game, and small. Discord's main window names its pictures: the call's tiles by
    /// display name (large), and the channel list's voice members by username. It can only be drawn into a bitmap while
    /// it's on screen, i.e. while it's the island's open tab, so this tries now and then until it gets them.
    /// </summary>
    void CaptureFromMain(List<string> names)
    {
        long now = Environment.TickCount64;
        if (now - mainTried < 4000) return;
        mainTried = now;
        var h = DockedHandle;
        if (h == IntPtr.Zero) h = FindWindow();
        if (h == IntPtr.Zero || !IsWindowVisible(h) || !GetWindowRect(h, out var win)) return;

        var found = new Dictionary<string, System.Windows.Rect>();
        foreach (AutomationElement e in AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, Images))
        {
            var c = e.Current;
            var r = c.BoundingRectangle;
            if (r.IsEmpty || r.Width < 16 || names.FirstOrDefault(n => n.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is not string name) continue;
            // The largest wins: a call tile over the channel list's small one.
            if (!found.TryGetValue(name, out var had) || had.Width < r.Width) found[name] = r;
        }
        if (found.Count == 0) return;

        int w = win.Right - win.Left, ht = win.Bottom - win.Top;
        IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, ht);
        ReleaseDC(IntPtr.Zero, screen);
        var old = SelectObject(dc, bmp);
        try
        {
            if (!PrintWindow(h, dc, PW_RENDERFULLCONTENT)) return;
            SelectObject(dc, old);
            foreach (var (name, r) in found)
            {
                int size = (int)Math.Min(r.Width, r.Height);
                int x = (int)(r.X + (r.Width - size) / 2) - win.Left, y = (int)(r.Y + (r.Height - size) / 2) - win.Top;
                if (x < 0 || y < 0 || x + size > w || y + size > ht) continue;
                var square = new FormatConvertedBitmap(
                    Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, new System.Windows.Int32Rect(x, y, size, size),
                                                          BitmapSizeOptions.FromEmptyOptions()),
                    System.Windows.Media.PixelFormats.Bgr32, null, 0);
                square.Freeze();
                if (Blank(square)) continue;
                // Trimmed to the picture inside a speaking ring, when there is one.
                BitmapSource image = square;
                if (FindPicture(square) is System.Windows.Int32Rect pic && pic.Width < size) { image = new CroppedBitmap(square, pic); image.Freeze(); }
                if (avatars.GetValueOrDefault(name).Image?.PixelWidth >= image.PixelWidth) continue;
                avatars[name] = (image, now);
                SaveAvatar(name, image);
            }
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
        }
    }

    /// <summary>
    /// The square the round picture fills in the start of a row. A fixed offset cut it off-centre, so it's found
    /// instead: everything that differs from the row's background (sampled at a corner and at the middle of the left
    /// edge, in case the row is a rounded pill), leaving out the green speaking ring around it.
    /// </summary>
    static System.Windows.Int32Rect? FindPicture(BitmapSource row)
    {
        int w = row.PixelWidth, h = row.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        row.CopyPixels(px, stride, 0);
        int corner = 0, edge = (h / 2) * stride;
        bool Differs(int i, int bg) => Math.Abs(px[i] - px[bg]) + Math.Abs(px[i + 1] - px[bg + 1]) + Math.Abs(px[i + 2] - px[bg + 2]) > 40;
        int left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                if (!Differs(i, corner) || !Differs(i, edge)) continue;
                if (g > 120 && g > r + 60 && g > b + 40) continue; // the speaking ring
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        // A picture that's dark at its edge reads smaller on that side, so take the larger side, centred on what was found.
        int size = Math.Max(right - left, bottom - top) + 1;
        if (right < 0 || size < h / 2 || size > h) return null;
        int cx = (left + right + 1) / 2, cy = (top + bottom + 1) / 2;
        int sx = Math.Clamp(cx - size / 2, 0, w - size), sy = Math.Clamp(cy - size / 2, 0, h - size);
        return new System.Windows.Int32Rect(sx, sy, size, size);
    }

    /// <summary>An overlay that isn't drawing gives one flat colour; better the initial than a black disc.</summary>
    static bool Blank(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        var px = new byte[stride * image.PixelHeight];
        image.CopyPixels(px, stride, 0);
        for (int i = 4; i < px.Length; i += 4)
            if (Math.Abs(px[i] - px[0]) + Math.Abs(px[i + 1] - px[1]) + Math.Abs(px[i + 2] - px[2]) > 24) return false;
        return true;
    }

    const uint PW_RENDERFULLCONTENT = 2;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

    static readonly TreeWalker Raw = TreeWalker.RawViewWalker;

    /// <summary>
    /// One row of the overlay's voice list: the name is its first text, and talking, muted and deafened show as
    /// class names and icons on the row and its children.
    /// </summary>
    static CallMember? ReadMember(AutomationElement row)
    {
        string? name = null;
        bool speaking = false, muted = false, deafened = false, streaming = false;
        void Visit(AutomationElement e, int depth)
        {
            var c = e.Current;
            string cls = c.ClassName;
            if (cls.Contains("speaking", StringComparison.OrdinalIgnoreCase)) speaking = true;
            // Someone sharing their screen gets a "LIVE" badge after their name.
            if (name != null && c.ControlType == ControlType.Text && c.Name == "LIVE") streaming = true;
            if (c.ControlType == ControlType.Text) name ??= c.Name;
            else
            {
                // Only icons and classes, never the name text: someone could be called "mute".
                string label = c.ControlType == ControlType.Image ? cls + " " + c.Name : cls;
                if (label.Contains("deaf", StringComparison.OrdinalIgnoreCase)) deafened = true;
                else if (label.Contains("mute", StringComparison.OrdinalIgnoreCase)) muted = true;
            }
            if (depth > 6) return;
            for (var ch = Raw.GetFirstChild(e); ch != null; ch = Raw.GetNextSibling(ch)) Visit(ch, depth + 1);
        }
        Visit(row, 0);
        return string.IsNullOrWhiteSpace(name) ? null : new CallMember(name.Trim(), speaking, muted || deafened, deafened, Streaming: streaming);
    }

    /// <summary>Older builds relabel the button ("Unmute"); newer ones keep the label and expose a switch state.</summary>
    static bool IsOn(AutomationElement e)
    {
        if (e.Current.Name.StartsWith("Un", StringComparison.OrdinalIgnoreCase)) return true;
        return e.TryGetCurrentPattern(TogglePattern.Pattern, out var p) && ((TogglePattern)p).Current.ToggleState == ToggleState.On;
    }
}
