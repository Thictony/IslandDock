using System;
using System.Windows;
using System.Windows.Controls;

namespace IslandDock;

/// <summary>
/// Lays tabs out in a row like taskbar buttons: the active tab is wide enough for its title, and every other tab is
/// just its icon, so a whole taskbar's worth fits. If even the icons don't fit, they all shrink together.
/// Icon-only tabs are flagged <see cref="IsCompactProperty"/> so the template can drop the title.
/// </summary>
public sealed class TabStripPanel : Panel
{
    // In DIPs including the tab's gap. An icon tab has room for the icon plus its sound wave, and never gets
    // narrower than the bare icon; the active tab gets up to MaxActive, and needs CompactBelow to show a title.
    public const double IconTab = 40, MinIconTab = 24, MaxActive = 240, CompactBelow = 100;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached("IsCompact",
        typeof(bool), typeof(TabStripPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject o) => (bool)o.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject o, bool v) => o.SetValue(IsCompactProperty, v);

    double[] widths = Array.Empty<double>();

    static bool IsActiveTab(UIElement kid) => kid is FrameworkElement { DataContext: DockedWindow { IsActive: true } };

    // A pin whose app is starting already opens out into the tab its window will dock into.
    static bool IsOpeningTab(UIElement kid) => kid is FrameworkElement { DataContext: PinEntry { IsLoading: true } };

    protected override Size MeasureOverride(Size available)
    {
        var kids = InternalChildren;
        if (kids.Count == 0) return default;

        // Nothing to fill (only during odd layout passes): fall back to a comfortable width.
        double room = double.IsInfinity(available.Width) ? kids.Count * IconTab + MaxActive : available.Width;
        int activeIndex = -1;
        for (int i = 0; i < kids.Count; i++)
            if (IsOpeningTab(kids[i])) { activeIndex = i; break; }
        if (activeIndex < 0)
            for (int i = 0; i < kids.Count; i++)
                if (IsActiveTab(kids[i])) { activeIndex = i; break; }

        int icons = kids.Count - (activeIndex >= 0 ? 1 : 0);
        double icon = icons == 0 ? 0 : Math.Max(MinIconTab, Math.Min(IconTab, (room - (activeIndex >= 0 ? CompactBelow : 0)) / icons));
        double active = Math.Clamp(room - icon * icons, Math.Min(IconTab, room), MaxActive);

        widths = new double[kids.Count];
        double height = 0, total = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            var kid = kids[i];
            double w = i == activeIndex ? active : icon;
            bool compact = w < CompactBelow;
            if (GetIsCompact(kid) != compact) SetIsCompact(kid, compact);
            kid.Measure(new Size(w, available.Height));
            height = Math.Max(height, kid.DesiredSize.Height);
            widths[i] = w;
            total += w;
        }
        return new Size(Math.Min(total, room), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        var kids = InternalChildren;
        for (int i = 0; i < kids.Count; i++)
        {
            double w = i < widths.Length ? widths[i] : IconTab;
            kids[i].Arrange(new Rect(x, 0, w, final.Height));
            x += w;
        }
        return final;
    }
}
