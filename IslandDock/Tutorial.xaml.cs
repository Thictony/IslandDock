using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandDock;

/// <summary>
/// The tour shown the first time the island runs, and again from the menu. Closing it any way (finishing, Skip, Esc)
/// counts as seen.
/// </summary>
public partial class Tutorial : Window
{
    sealed record TourPage(string Glyph, string Title, string[] Points);

    static readonly TourPage[] Pages =
    {
        new("", "Welcome to Island", new[]
        {
            "Island sits at the edge of your screen and holds your windows as tabs, plus media, quick settings and notifications.",
            "This takes a minute. You can open it again any time from the menu.",
        }),
        new("", "Open it", new[]
        {
            "Hover over the island for a moment, or click it.",
            "Press the Windows key on its own to open or close it. Start still opens with Windows + another key, or when the island is off.",
            "It tucks back in when the mouse leaves. Use the pin to keep it open.",
        }),
        new("", "Dock windows", new[]
        {
            "Drag any window by its title bar onto the island to dock it as a tab.",
            "Or open the island, press +, and pick a window from the list.",
            "The button on a docked tab pops it back out. “Undock all windows” is in the menu.",
        }),
        new("", "Switch tabs", new[]
        {
            "Click a tab, or press Alt+Tab to step through them (Alt+Shift+Tab goes back).",
            "Other open apps show up as tabs too. The X on one closes it.",
            "Right-click a tab for a new window, close, or end the process.",
        }),
        new("", "Make it yours", new[]
        {
            "Hold the island for about a second, then drag it to any edge of the screen.",
            "Each display has its own island. Choose which displays get one in the menu.",
            "The zoom buttons make a docked window bigger or smaller, remembered per app.",
        }),
        new("", "Notifications", new[]
        {
            "Notifications drop down from the island as a card.",
            "Click a card to open the app; right-click it to dismiss.",
            "Windows still shows its own pop-ups too. Turn on Do not disturb to hide those.",
        }),
        new("", "You’re all set", new[]
        {
            "Settings are under ⋯ on the island, or right-click it.",
            "Choose “Show the tour again” there to come back here.",
        }),
    };

    static Tutorial? open;
    int index;

    /// <summary>Shows the tour, or brings it forward if it's already up.</summary>
    public static void Open()
    {
        if (open != null) { open.Activate(); return; }
        open = new Tutorial();
        open.Show();
        open.Activate();
    }

    Tutorial()
    {
        InitializeComponent();
        foreach (var _ in Pages)
            Dots.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) });
        Show(0, 0);
        SourceInitialized += (_, _) =>
        {
            if (Islands.Settings.UseSoftwareRendering && HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.CompositionTarget is HwndTarget t)
                t.RenderMode = RenderMode.SoftwareOnly;
        };
        // In the middle of the main display, once its height is known.
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + (area.Height - ActualHeight) / 2;
        };
        Closed += (_, _) =>
        {
            open = null;
            Islands.Settings.TutorialDone = true;
            Islands.Settings.Save();
        };
    }

    /// <summary>Puts up a page, sliding in from the side it's coming from (direction 1 forward, -1 back, 0 none).</summary>
    void Show(int i, int direction)
    {
        index = i;
        var p = Pages[i];
        Glyph.Text = p.Glyph;
        Heading.Text = p.Title;
        Points.Children.Clear();
        foreach (var text in p.Points)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var dot = new Border
            {
                Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Brush("#4CC2FF"),
                Margin = new Thickness(2, 8, 12, 0), VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20, Foreground = Brush("#CCFFFFFF")
            });
            Points.Children.Add(row);
        }

        for (int d = 0; d < Dots.Children.Count; d++)
        {
            var dot = (Border)Dots.Children[d];
            dot.Background = Brush(d == i ? "#4CC2FF" : "#40FFFFFF");
            dot.Width = d == i ? 18 : 6;
        }
        BackButton.Visibility = i > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = i < Pages.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = i < Pages.Length - 1 ? "Next" : "Get started";

        if (direction == 0) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(260);
        Page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        PageShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28 * direction, 0, time) { EasingFunction = ease });
    }

    static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    void Next()
    {
        if (index < Pages.Length - 1) Show(index + 1, 1);
        else Close();
    }

    void Back()
    {
        if (index > 0) Show(index - 1, -1);
    }

    void Next_Click(object sender, RoutedEventArgs e) => Next();
    void Back_Click(object sender, RoutedEventArgs e) => Back();
    void Skip_Click(object sender, RoutedEventArgs e) => Close();

    // Buttons handle their own presses, so this is only the card itself.
    void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Right or Key.Enter or Key.Space: Next(); break;
            case Key.Left: Back(); break;
            default: return;
        }
        e.Handled = true;
    }
}
