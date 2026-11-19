using System;
using System.Windows;
using System.Windows.Media;

namespace CyaNotch.Views;

/// <summary>
/// Draws the music bars. Bars fill the element's full width; heights come from <see cref="Services.VisualizerService"/>.
/// Rendering runs only while <see cref="Active"/> is true, once per screen refresh.
/// </summary>
public sealed class VisualizerBars : FrameworkElement
{
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(VisualizerBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MirroredProperty = DependencyProperty.Register(
        nameof(Mirrored), typeof(bool), typeof(VisualizerBars),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    /// <summary>True: bars grow up and down from the middle line. False: bars grow up from the bottom.</summary>
    public bool Mirrored { get => (bool)GetValue(MirroredProperty); set => SetValue(MirroredProperty, value); }

    /// <summary>Target width of one bar plus gap, in DIPs. The bar count follows the element's width.</summary>
    public double Pitch { get; set; } = 5.0;

    private float[] _levels = Array.Empty<float>();
    private bool _active;
    private TimeSpan _lastRender;

    public int DesiredBands => Math.Clamp((int)(ActualWidth / Pitch), 8, 128);

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (value) CompositionTarget.Rendering += OnRendering;
            else
            {
                CompositionTarget.Rendering -= OnRendering;
                _levels = Array.Empty<float>();
                InvalidateVisual();
            }
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastRender) return; // Rendering can fire twice per frame
        var dt = _lastRender == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (t - _lastRender).TotalSeconds);
        _lastRender = t;

        var viz = App.Visualizer;
        if (!viz.Running) viz.Start();
        _levels = Resample(viz.Read(dt), DesiredBands);
        InvalidateVisual();
    }

    private float[] _resampled = Array.Empty<float>();

    /// <summary>Stretches the service's bands to this view's bar count (linear interpolation).</summary>
    private float[] Resample(float[] src, int n)
    {
        if (_resampled.Length != n) _resampled = new float[n];
        if (src.Length == 0) { Array.Clear(_resampled); return _resampled; }
        for (int i = 0; i < n; i++)
        {
            var pos = n == 1 ? 0 : (double)i / (n - 1) * (src.Length - 1);
            var a = (int)pos;
            var b = Math.Min(src.Length - 1, a + 1);
            var f = (float)(pos - a);
            _resampled[i] = src[a] * (1 - f) + src[b] * f;
        }
        return _resampled;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var levels = _levels;
        var w = ActualWidth;
        var h = ActualHeight;
        if (levels.Length == 0 || w <= 0 || h <= 0) return;

        var n = levels.Length;
        var slot = w / n;
        var barW = Math.Max(1.5, slot * 0.58);
        var radius = barW / 2;
        var minH = Math.Max(2, barW);
        var brush = BarBrush;

        for (int i = 0; i < n; i++)
        {
            // Lows in the centre spreading outward reads nicely across a wide pill.
            var bandIndex = MapCentreOut(i, n);
            var v = levels[bandIndex];
            var bh = Math.Max(minH, v * h);
            var x = i * slot + (slot - barW) / 2;
            var y = Mirrored ? (h - bh) / 2 : h - bh;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barW, bh), radius, radius);
        }
    }

    /// <summary>Position i (left→right) to band index, with bass in the middle and treble at both ends.</summary>
    private static int MapCentreOut(int i, int n)
    {
        var mid = (n - 1) / 2.0;
        var d = Math.Abs(i - mid) / Math.Max(1, mid); // 0 at centre, 1 at edges
        return Math.Clamp((int)Math.Round(d * (n - 1)), 0, n - 1);
    }
}
