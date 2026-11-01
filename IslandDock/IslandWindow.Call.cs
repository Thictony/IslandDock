using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IslandDock;

// The people in the Discord call, shown on the closed pill: a green ring while they talk, a red badge when muted or deafened.
public partial class IslandWindow
{
    /// <summary>Most avatars the pill shows; past that the talker gets the first slot and the rest are a count.</summary>
    const int CallSlots = 4;
    const double AvatarSize = 18;

    IReadOnlyList<CallMember> callMembers = Array.Empty<CallMember>();
    string? featuredSpeaker;

    static readonly Color[] AvatarColors =
    {
        Color.FromRgb(0x58, 0x65, 0xF2), Color.FromRgb(0x3B, 0xA5, 0x5C), Color.FromRgb(0xEB, 0x45, 0x9E),
        Color.FromRgb(0xED, 0x42, 0x45), Color.FromRgb(0xF0, 0xB2, 0x32), Color.FromRgb(0x1A, 0xBC, 0x9C),
        Color.FromRgb(0x9B, 0x59, 0xB6), Color.FromRgb(0x74, 0x7F, 0x8D),
    };
    static readonly SolidColorBrush MutedRed = new(Color.FromRgb(0xFF, 0x45, 0x3A));

    /// <summary>One brush pulses for every streaming ring, so rebuilding the avatars doesn't restart the pulse.</summary>
    static readonly SolidColorBrush StreamPulse = new(Color.FromRgb(0xFF, 0x45, 0x3A));
    static bool pulsing;

    /// <summary>
    /// A running animation keeps WPF redrawing, so the pulse only runs while someone streams, and slowly: a gentle
    /// fade looks the same at 15 frames a second and costs a quarter of 60.
    /// </summary>
    static void Pulse(bool on)
    {
        if (on == pulsing) return;
        pulsing = on;
        if (!on) { StreamPulse.BeginAnimation(Brush.OpacityProperty, null); return; }
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(1.2))
        {
            AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fade, 15);
        StreamPulse.BeginAnimation(Brush.OpacityProperty, fade);
    }

    void ShowCall(IReadOnlyList<CallMember> members)
    {
        callMembers = members;
        Pulse(members.Any(m => m.Streaming));
        DiscordCall.Children.Clear();
        DiscordCall.Visibility = members.Count > 0 && discordRelevant ? Visibility.Visible : Visibility.Collapsed;

        var shown = members.ToList();
        int extra = 0;
        if (members.Count > CallSlots)
        {
            // Whoever is talking takes the first slot, and keeps it until someone else talks, so it doesn't flicker
            // between people at every pause.
            if (!members.Any(m => m.Speaking && m.Name == featuredSpeaker))
                featuredSpeaker = members.FirstOrDefault(m => m.Speaking)?.Name ?? featuredSpeaker;
            var lead = members.FirstOrDefault(m => m.Name == featuredSpeaker);
            var order = lead == null ? members : members.Where(m => m != lead).Prepend(lead);
            shown = order.Take(CallSlots - 1).ToList();
            extra = members.Count - shown.Count;
        }

        bool v = Vertical;
        var gap = v ? new Thickness(0, AvatarGap, 0, 0) : new Thickness(AvatarGap, 0, 0, 0);
        for (int i = 0; i < shown.Count; i++)
        {
            var avatar = Avatar(shown[i]);
            // A streaming ring reaches further out, so it gets room of its own on both sides.
            double before = i > 0 ? AvatarGap : 0, after = 0;
            if (shown[i].Streaming) { before += 3; after = 3; }
            avatar.Margin = v ? new Thickness(0, before, 0, after) : new Thickness(before, 0, after, 0);
            DiscordCall.Children.Add(avatar);
        }
        if (extra > 0)
            DiscordCall.Children.Add(new TextBlock
            {
                Text = "+" + extra, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = gap,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = string.Join("\n", members.Except(shown).Select(Describe)),
            });
        RefreshCollapsedIcons();
    }

    /// <summary>Their profile picture, or until it's been read, an initial on a colour of their own.</summary>
    FrameworkElement Avatar(CallMember m)
    {
        // The rings are drawn in a larger square around the avatar, all centred on it with whole-pixel offsets;
        // negative margins on the rings themselves left them a little off-centre after layout rounding.
        // The negative outer margin keeps the avatar's footprint in the pill at AvatarSize.
        var outer = new Grid
        {
            Width = AvatarSize + 2 * RingRoom, Height = AvatarSize + 2 * RingRoom, Margin = new Thickness(-RingRoom),
            ToolTip = Describe(m), Cursor = Cursors.Hand,
        };
        var g = new Grid
        {
            Width = AvatarSize, Height = AvatarSize, Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (m.Avatar != null)
            g.Children.Add(new Ellipse { Fill = new ImageBrush(m.Avatar) { Stretch = Stretch.UniformToFill } });
        else
        {
            int hash = m.Name.Aggregate(17, (h, c) => h * 31 + c);
            g.Children.Add(new Ellipse { Fill = new SolidColorBrush(AvatarColors[(hash & 0x7FFFFFFF) % AvatarColors.Length]) });
            g.Children.Add(new TextBlock
            {
                Text = StringInfo.GetNextTextElement(m.Name).ToUpperInvariant(), Foreground = Brushes.White,
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        }
        outer.Children.Add(g);
        Ellipse Ring(Brush stroke, double size) => new()
        {
            Stroke = stroke, StrokeThickness = RingThickness, Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        // Discord's speaking ring, just outside the avatar.
        if (m.Speaking) outer.Children.Add(Ring(DiscordGreen, AvatarSize + 4));
        // Streaming: a slowly pulsing red ring, which widens to go around the green one while they talk too.
        if (m.Streaming) outer.Children.Add(Ring(StreamPulse, AvatarSize + (m.Speaking ? 8 : 4)));
        if (m.Muted)
        {
            var badge = new Grid { Width = 10, Height = 10, Margin = new Thickness(0, 0, -3, -3),
                                   HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            badge.Children.Add(new Ellipse { Fill = MutedRed, Stroke = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10)), StrokeThickness = 1 });
            badge.Children.Add(new TextBlock
            {
                Text = m.Deafened ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 6, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            // Over the rings, as in Discord.
            outer.Children.Add(badge);
            badge.Margin = new Thickness(0, 0, RingRoom - 3, RingRoom - 3);
        }
        outer.MouseLeftButtonUp += (_, e) => { if (Gestured) return; e.Handled = true; ShowDiscordWindow(); };
        return outer;
    }

    /// <summary>Space on each side of an avatar for its rings: the widest is 4px out, including its own stroke.</summary>
    const double RingRoom = 4;
    const double RingThickness = 1.25;
    const double AvatarGap = 2;

    /// <summary>How much of the pill's length the avatars take, margin included.</summary>
    double CallLength(bool vertical)
    {
        if (DiscordCall.Visibility != Visibility.Visible) return 0;
        DiscordCall.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return 8 + (vertical ? DiscordCall.DesiredSize.Height : DiscordCall.DesiredSize.Width);
    }

    static string Describe(CallMember m) =>
        m.Name + (m.Deafened ? " (deafened)" : m.Muted ? " (muted)" : m.Speaking ? " (talking)" : "")
        + (m.Streaming ? " (streaming)" : "");
}
