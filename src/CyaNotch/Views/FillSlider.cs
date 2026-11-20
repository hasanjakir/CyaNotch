using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CyaNotch.Views;

/// <summary>
/// A tall rounded slider that fills from the bottom, with its percentage on top and an icon at the
/// bottom, like the brightness and volume sliders in the quick controls. Drag, click or scroll it.
/// </summary>
public sealed class FillSlider : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(FillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged, Coerce));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(FillSlider),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>Raised for every change, including ones set from code.</summary>
    public event RoutedPropertyChangedEventHandler<double>? ValueChanged;

    /// <summary>Raised when the user lets go (or scrolls), for settings that are slow to apply.</summary>
    public event EventHandler? Committed;

    public bool IsDragging { get; private set; }

    public FillSlider()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        MinWidth = 40;
    }

    private static object Coerce(DependencyObject d, object v) => Math.Clamp((double)v, 0, 100);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (FillSlider)d;
        s.ValueChanged?.Invoke(s, new RoutedPropertyChangedEventArgs<double>((double)e.OldValue, (double)e.NewValue));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        IsDragging = true;
        CaptureMouse();
        SetFrom(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsDragging) SetFrom(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsDragging) return;
        IsDragging = false;
        ReleaseMouseCapture();
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value = Math.Round(Value + (e.Delta > 0 ? 5 : -5));
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void SetFrom(Point p)
    {
        if (ActualHeight <= 0) return;
        Value = Math.Round((1 - p.Y / ActualHeight) * 100);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = Math.Min(16, w / 2.4);
        var box = new Rect(0, 0, w, h);
        var clip = new RectangleGeometry(box, r, r);

        dc.PushClip(clip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), null, box);
        var fillH = h * Value / 100;
        var fillTop = h - fillH;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), null, new Rect(0, fillTop, w, fillH));
        dc.Pop();

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ui = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var pct = new FormattedText($"{Value:0}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, ui, 11.5,
            fillTop < 22 ? Brushes.Black : Brushes.White, dpi);
        dc.DrawText(pct, new Point((w - pct.Width) / 2, 9));

        if (!string.IsNullOrEmpty(Glyph))
        {
            var icon = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var gy = h - 26;
            var g = new FormattedText(Glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, icon, 15,
                fillTop < gy + 10 ? Brushes.Black : Brushes.White, dpi);
            dc.DrawText(g, new Point((w - g.Width) / 2, gy));
        }
    }
}
