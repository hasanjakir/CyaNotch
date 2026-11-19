using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CyaNotch.Core;

namespace CyaNotch.Views;

/// <summary>
/// Song progress drawn as a gently moving wave up to the play position, then a flat line for what's left.
/// Click or drag anywhere on it to seek.
/// </summary>
public sealed class WavyProgress : FrameworkElement
{
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(WavyProgress),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(WavyProgress),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    /// <summary>0..1 play position.</summary>
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 1); InvalidateVisual(); }
    }

    /// <summary>While true the wave moves; when paused it settles into a flat line.</summary>
    public bool Playing
    {
        get => _playing;
        set
        {
            if (_playing == value) return;
            _playing = value;
            UpdateAnimation();
        }
    }

    /// <summary>Raised when the user lets go after clicking or dragging, with the new 0..1 position.</summary>
    public event Action<double>? SeekRequested;

    public bool IsDragging { get; private set; }

    /// <summary>Wave, Line, Bar, Dots or Glow.</summary>
    public ProgressStyle Look
    {
        get => _look;
        set { _look = value; UpdateAnimation(); InvalidateVisual(); }
    }

    /// <summary>Soft themes swap the thumb for a heart, sparkle or petal.</summary>
    public ThemeTouch Touch
    {
        get => _touch;
        set { _touch = value; InvalidateVisual(); }
    }

    private ProgressStyle _look = ProgressStyle.Wave;
    private ThemeTouch _touch;

    private double _value;
    private bool _playing;
    private double _phase;
    private double _amp;          // eases between 0 (flat) and 1 (full wave)
    private TimeSpan _last;
    private bool _hooked;

    public WavyProgress()
    {
        Height = 18;
        Cursor = Cursors.Hand;
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        var want = IsVisible && _look == ProgressStyle.Wave && (_playing || _amp > 0.001);
        if (want == _hooked) return;
        _hooked = want;
        _last = TimeSpan.Zero;
        if (want) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _last) return;
        var dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (t - _last).TotalSeconds);
        _last = t;
        _phase += dt * 5.5;
        var target = _playing ? 1.0 : 0.0;
        _amp += (target - _amp) * Math.Min(1, dt * 6);
        if (!_playing && _amp < 0.001) { _amp = 0; UpdateAnimation(); }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var mid = h / 2;
        var x = w * _value;

        // Hit area for the whole strip.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        switch (_look)
        {
            case ProgressStyle.Dots:
                RenderDots(dc, w, mid, x);
                break;
            case ProgressStyle.Bar:
                dc.DrawRoundedRectangle(Track, null, new Rect(0, mid - 3, w, 6), 3, 3);
                if (x > 0) dc.DrawRoundedRectangle(Brush, null, new Rect(0, mid - 3, Math.Max(6, x), 6), 3, 3);
                break;
            case ProgressStyle.Glow:
                Line(dc, Track, 3, 1.5, Math.Max(x, 1.5), w - 1.5, mid);
                if (x > 1)
                {
                    var glow = Brush.CloneCurrentValue();
                    glow.Opacity = 0.25;
                    Line(dc, glow, 9, 4.5, x, -1, mid);
                    Line(dc, Brush, 3, 1.5, x, -1, mid);
                }
                break;
            case ProgressStyle.Line:
                Line(dc, Track, 3, 1.5, Math.Max(x, 1.5), w - 1.5, mid);
                if (x > 1) Line(dc, Brush, 3, 1.5, x, -1, mid);
                break;
            default:
                RenderWave(dc, w, mid, x);
                break;
        }

        if (_look != ProgressStyle.Bar && _look != ProgressStyle.Dots) DrawThumb(dc, Math.Clamp(x, 0, w), mid);
    }

    private static void Line(DrawingContext dc, Brush b, double thickness, double from, double to, double unused, double y)
    {
        if (to <= from) return;
        var pen = new Pen(b, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(pen, new Point(from, y), new Point(to, y));
    }

    private void RenderWave(DrawingContext dc, double w, double mid, double x)
    {
        var track = new Pen(Track, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (x < w - 1) dc.DrawLine(track, new Point(Math.Max(x + 5, 1.5), mid), new Point(w - 1.5, mid));
        if (x <= 1) return;

        const double wavelength = 22;
        var amp = 3.2 * _amp;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(1.5, mid + Math.Sin(_phase) * amp), false, false);
            for (double px = 3; px <= x; px += 2)
                g.LineTo(new Point(px, mid + Math.Sin(px / wavelength * Math.PI * 2 - _phase) * amp), true, true);
        }
        geo.Freeze();
        var pen = new Pen(Brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geo);
    }

    private void RenderDots(DrawingContext dc, double w, double mid, double x)
    {
        const double pitch = 8, r = 2.2;
        var n = Math.Max(2, (int)(w / pitch));
        var step = (w - 2 * r) / (n - 1);
        for (int i = 0; i < n; i++)
        {
            var cx = r + i * step;
            var on = cx <= x;
            dc.DrawEllipse(on ? Brush : Track, null, new Point(cx, mid), on ? r + 0.4 : r, on ? r + 0.4 : r);
        }
    }

    private void DrawThumb(DrawingContext dc, double x, double mid)
    {
        var glyph = _touch switch { ThemeTouch.Hearts => "\u2665", ThemeTouch.Sparkles => "\u2726", ThemeTouch.Petals => "\u2740", _ => null };
        if (glyph == null)
        {
            // Upright thumb at the play position.
            dc.DrawRoundedRectangle(Brush, null, new Rect(x - 2, mid - 7, 4, 14), 2, 2);
            return;
        }
        var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Symbol"), 15, Brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(x - ft.Width / 2, mid - ft.Height / 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        IsDragging = true;
        CaptureMouse();
        Value = e.GetPosition(this).X / Math.Max(1, ActualWidth);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsDragging) Value = e.GetPosition(this).X / Math.Max(1, ActualWidth);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsDragging) return;
        IsDragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
        SeekRequested?.Invoke(Value);
    }
}
