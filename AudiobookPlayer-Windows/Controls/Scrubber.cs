using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AudiobookPlayer;

/// Draggable progress bar (0..1). Raises Dragged while dragging and Committed on release.
public sealed class Scrubber : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(Scrubber), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Scrubber), new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThumbSizeProperty = DependencyProperty.Register(
        nameof(ThumbSize), typeof(double), typeof(Scrubber), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public double ThumbSize { get => (double)GetValue(ThumbSizeProperty); set => SetValue(ThumbSizeProperty, value); }

    public event Action<double>? Dragged;
    public event Action<double>? Committed;

    private bool dragging, hover;
    private double dragFraction;

    private static readonly Brush TrackBrush = Make(new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)));
    private static readonly Brush FillBrush = Make(new LinearGradientBrush(
        Color.FromRgb(0x8A, 0x74, 0x4A), Color.FromRgb(0xC8, 0xA9, 0x6E), 0));
    private static readonly Brush ThumbBrush = Make(new SolidColorBrush(Color.FromRgb(0xC8, 0xA9, 0x6E)));
    private static readonly Pen ThumbPen = new(Make(new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x14))), 2);

    static Scrubber() { ThumbPen.Freeze(); }

    private static Brush Make(Brush b) { b.Freeze(); return b; }

    public Scrubber()
    {
        Height = 24;
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, t = Thickness, th = ThumbSize;
        if (w <= 0 || h <= 0) return;
        double f = Math.Clamp(dragging ? dragFraction : Fraction, 0, 1);
        double pad = th / 2, usable = Math.Max(w - th, 1);
        double x = pad + f * usable, cy = h / 2;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-test area
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(pad, cy - t / 2, usable, t), t / 2, t / 2);
        if (x > pad) dc.DrawRoundedRectangle(FillBrush, null, new Rect(pad, cy - t / 2, x - pad, t), t / 2, t / 2);
        double r = (hover || dragging ? 1.2 : 1.0) * th / 2;
        dc.DrawEllipse(ThumbBrush, ThumbPen, new Point(x, cy), r, r);
    }

    private double FractionAt(MouseEventArgs e)
    {
        double th = ThumbSize, usable = Math.Max(ActualWidth - th, 1);
        return Math.Clamp((e.GetPosition(this).X - th / 2) / usable, 0, 1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        dragging = true;
        CaptureMouse();
        dragFraction = FractionAt(e);
        InvalidateVisual();
        Dragged?.Invoke(dragFraction);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        dragFraction = FractionAt(e);
        InvalidateVisual();
        Dragged?.Invoke(dragFraction);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        dragFraction = FractionAt(e);
        InvalidateVisual();
        Committed?.Invoke(dragFraction);
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); hover = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); hover = false; InvalidateVisual(); }
}
