using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using static IslandDock.Native;

namespace IslandDock;

public partial class IslandWindow
{
    /// <summary>A place the docking location panel offers. Row and Column are where it sits in the panel's 3×3 grid,
    /// which is laid out like the screen.</summary>
    sealed record Spot(string Name, Edge Edge, double Along, int Row, int Column);

    static readonly Spot[] Spots =
    {
        new("Top left", Edge.Top, 0, 0, 0),
        new("Top middle", Edge.Top, 0.5, 0, 1),
        new("Top right", Edge.Top, 1, 0, 2),
        new("Center left", Edge.Left, 0.5, 1, 0),
        new("Center right", Edge.Right, 0.5, 1, 2),
        new("Bottom left", Edge.Bottom, 0, 2, 0),
        new("Bottom middle", Edge.Bottom, 0.5, 2, 1),
        new("Bottom right", Edge.Bottom, 1, 2, 2),
    };

    readonly List<(Spot Spot, Border Screen, Border Pill, TextBlock Label)> spotViews = new();

    /// <summary>Opens the panel from the right-click menu, which works with the island closed and the mouse elsewhere.</summary>
    void OpenLocationPanel()
    {
        pickerOpen = mixerOpen = searchOpen = trayOpen = updateOpen = false;
        spotsOpen = true;
        ShowSpots();
        OpenFromKeyboard();
        ForceForeground(hwnd); // it stays open while it has the keyboard, until the mouse comes onto it
    }

    void LocationButton_Click(object sender, RoutedEventArgs e)
    {
        spotsOpen = !spotsOpen;
        pickerOpen = mixerOpen = searchOpen = trayOpen = updateOpen = false;
        if (spotsOpen) ShowSpots();
    }

    void Spot_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Spot spot) return;
        along = spot.Along;
        if (spot.Edge != edge) { edge = spot.Edge; ApplyOrientation(); }
        SaveSettings();
        ShowSpots();
        Wake();
    }

    // The buttons are made the first time; after that this only marks the one the island is on. An island dragged
    // somewhere in between has none marked.
    void ShowSpots()
    {
        if (spotViews.Count == 0) BuildSpots();
        var accent = (Brush)FindResource("Accent");
        var dim = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        var text = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF));
        foreach (var (spot, screen, pill, label) in spotViews)
        {
            bool here = spot.Edge == edge && Math.Abs(spot.Along - along) < 0.01;
            screen.BorderBrush = here ? accent : dim;
            pill.Background = here ? accent : dim;
            label.Foreground = here ? Brushes.White : text;
            label.FontWeight = here ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    void BuildSpots()
    {
        var cells = new UIElement[9];
        foreach (var spot in Spots)
        {
            bool side = spot.Edge is Edge.Left or Edge.Right;
            var pill = new Border
            {
                Width = side ? 4 : 16,
                Height = side ? 16 : 4,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(3),
                HorizontalAlignment = spot.Edge switch
                {
                    Edge.Left => HorizontalAlignment.Left,
                    Edge.Right => HorizontalAlignment.Right,
                    _ => spot.Along == 0 ? HorizontalAlignment.Left : spot.Along == 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                },
                VerticalAlignment = spot.Edge switch
                {
                    Edge.Top => VerticalAlignment.Top,
                    Edge.Bottom => VerticalAlignment.Bottom,
                    _ => VerticalAlignment.Center,
                },
            };
            var screen = new Border
            {
                Width = 58,
                Height = 36,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Child = pill,
            };
            var label = new TextBlock { Text = spot.Name, FontSize = 11, Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { screen, label } };
            var button = new Button { Style = (Style)FindResource("RowButton"), Tag = spot, Content = content };
            AutomationProperties.SetName(button, spot.Name);
            button.Click += Spot_Click;
            cells[spot.Row * 3 + spot.Column] = button;
            spotViews.Add((spot, screen, pill, label));
        }
        // The grid is laid out like the screen, so its middle stays empty.
        foreach (var cell in cells) SpotGrid.Children.Add(cell ?? new Border());
    }
}
