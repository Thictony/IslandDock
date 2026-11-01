using System;
using System.Windows;
using System.Windows.Media;

namespace IslandDock;

/// <summary>
/// A small animated equaliser. Windows only exposes a level per app, not the waveform, so the bars follow the
/// loudness and each wobbles at its own rate to look alive.
/// </summary>
public sealed class WaveBars : FrameworkElement
{
    const int Bars = 4;
    const double BarW = 2.5, Gap = 2, BarH = 14;
    static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double),
        typeof(WaveBars), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–1, already smoothed by the caller.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override Size MeasureOverride(Size available) => new(Bars * BarW + (Bars - 1) * Gap, BarH);

    protected override void OnRender(DrawingContext dc)
    {
        double t = Environment.TickCount64 / 1000.0, h = ActualHeight, level = Math.Clamp(Level, 0, 1);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // clickable between the bars too
        for (int i = 0; i < Bars; i++)
        {
            double wobble = 0.55 + 0.45 * Math.Sin(t * (7 + i * 2.3) + i * 1.7);
            double bh = Math.Max(BarW, h * level * wobble);
            dc.DrawRoundedRectangle(Fill, null, new Rect(i * (BarW + Gap), (h - bh) / 2, BarW, bh), BarW / 2, BarW / 2);
        }
    }

    internal static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

/// <summary>
/// The same equaliser wrapped around a tab's icon: short bars standing out from a circle, each following the level
/// and wobbling at its own rate. Sized to surround a 16px icon placed at its centre.
/// </summary>
public sealed class WaveRing : FrameworkElement
{
    const int Bars = 14;
    const double Radius = 10.5, MinLen = 1, MaxLen = 3.5, Thickness = 1.6;
    static readonly Pen Stroke = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), Thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double),
        typeof(WaveRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–1, already smoothed by the caller.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override Size MeasureOverride(Size available) => new(2 * (Radius + MaxLen + Thickness), 2 * (Radius + MaxLen + Thickness));

    protected override void OnRender(DrawingContext dc)
    {
        double t = Environment.TickCount64 / 1000.0, level = Math.Clamp(Level, 0, 1);
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        for (int i = 0; i < Bars; i++)
        {
            double angle = i * 2 * Math.PI / Bars - Math.PI / 2;
            double wobble = 0.55 + 0.45 * Math.Sin(t * (7 + (i % 5) * 2.3) + i * 1.7);
            double len = MinLen + (MaxLen - MinLen) * level * wobble;
            var dir = new Vector(Math.Cos(angle), Math.Sin(angle));
            dc.DrawLine(Stroke, c + dir * Radius, c + dir * (Radius + len));
        }
    }

    static Pen Freeze(Pen p) { p.Freeze(); return p; }
}
