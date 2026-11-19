using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CyaNotch.Core;

namespace CyaNotch.Views;

/// <summary>
/// A widget pinned to the desktop: a small rounded card that sits behind other windows.
/// Drag it anywhere; right-click to unpin. Kinds: Clock, Weather, Stats, World, Todo.
/// </summary>
public sealed class WidgetWindow : Window
{
    public WidgetPin Pin { get; }
    private readonly StackPanel _body = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IntPtr _hwnd;

    public static readonly string[] Kinds = { "Clock", "Weather", "Stats", "World", "Todo" };

    public WidgetWindow(WidgetPin pin)
    {
        Pin = pin;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        Left = pin.Left;
        Top = pin.Top;
        Title = $"CyaNotch {pin.Kind}";

        var card = new Border
        {
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(16, 14, 16, 14),
            MinWidth = 190,
            Child = _body,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceEdgeBrush");
        card.BorderThickness = new Thickness(1);
        Content = card;
        SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        TextElement.SetFontFamily(this, (FontFamily)FindResource("UiFont"));

        var menu = new ContextMenu();
        var unpin = new MenuItem { Header = "Unpin from desktop" };
        unpin.Click += (_, _) => Unpin();
        menu.Items.Add(unpin);
        ContextMenu = menu;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
            Pin.Left = Left;
            Pin.Top = Top;
            Settings.Save();
            SendToBack();
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            SendToBack();
        };
        Activated += (_, _) => SendToBack();

        _timer.Tick += (_, _) => Refresh();
        _timer.Interval = pin.Kind == "Clock" || pin.Kind == "World" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2);
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void SendToBack()
    {
        if (_hwnd != IntPtr.Zero)
            Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void Unpin()
    {
        Settings.Current.PinnedWidgets.Remove(Pin);
        Settings.Save();
        Close();
    }

    private static TextBlock T(string text, double size = 13, bool sub = false, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        if (sub) t.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        return t;
    }

    private void Refresh()
    {
        _body.Children.Clear();
        var s = Settings.Current;
        var now = DateTime.Now;
        switch (Pin.Kind)
        {
            case "Clock":
                _body.Children.Add(T(now.ToString(s.Use24Hour ? "HH:mm" : "h:mm tt"), 40, weight: FontWeights.Light));
                _body.Children.Add(T(now.ToString("dddd, d MMMM"), 13, sub: true));
                break;

            case "Weather":
                var w = App.Weather.Current;
                _body.Children.Add(T("Weather", 12, sub: true));
                if (w == null)
                {
                    _body.Children.Add(T(s.WeatherLat == null ? "Set your city in Settings" : "Loading…", 14));
                }
                else
                {
                    var unit = s.UseFahrenheit ? "°F" : "°C";
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                    var icon = T(w.Glyph, 30);
                    icon.FontFamily = (FontFamily)FindResource("IconFont");
                    icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                    row.Children.Add(icon);
                    row.Children.Add(new TextBlock { Text = $"  {w.Temp:0}{unit}", FontSize = 30, FontWeight = FontWeights.Light });
                    _body.Children.Add(row);
                    _body.Children.Add(T($"{w.Description} · H {w.High:0}° L {w.Low:0}°", 12.5));
                    _body.Children.Add(T(w.Place, 12, sub: true));
                }
                break;

            case "Stats":
                var st = App.Sys.Sample();
                _body.Children.Add(T("PC", 12, sub: true));
                _body.Children.Add(Bar("CPU", st.Cpu, $"{st.Cpu:0}%"));
                _body.Children.Add(Bar("RAM", st.Ram, $"{st.Ram:0}%"));
                if (st.Gpu is { } g) _body.Children.Add(Bar("GPU", g, $"{g:0}%"));
                if (st.Battery is { } b) _body.Children.Add(Bar(st.Charging ? "BAT ⚡" : "BAT", b, $"{b}%"));
                break;

            case "World":
                _body.Children.Add(T("World", 12, sub: true));
                foreach (var c in s.WorldClocks)
                {
                    string time;
                    try { time = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(c.TimeZoneId)).ToString(s.Use24Hour ? "HH:mm" : "h:mm tt"); }
                    catch { time = "?"; }
                    var g2 = new Grid { Margin = new Thickness(0, 3, 0, 0), MinWidth = 180 };
                    g2.Children.Add(T(c.Label, 13));
                    var tt = T(time, 13, weight: FontWeights.SemiBold);
                    tt.HorizontalAlignment = HorizontalAlignment.Right;
                    g2.Children.Add(tt);
                    _body.Children.Add(g2);
                }
                break;

            case "Todo":
                _body.Children.Add(T("To-do", 12, sub: true));
                var open = App.Reminders.Todos.Where(t => !t.Done).Take(8).ToList();
                if (open.Count == 0) _body.Children.Add(T("All done", 13));
                foreach (var t in open) _body.Children.Add(T("○  " + t.Text, 13));
                break;
        }
    }

    private static FrameworkElement Bar(string label, double value, string text)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0), Width = 190 };
        g.Children.Add(T(label, 12));
        var track = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(44, 0, 40, 0), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        var fill = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Clamp(value, 0, 100) / 100 * 106 };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        track.Child = fill;
        g.Children.Add(track);
        var v = T(text, 12);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(v);
        return g;
    }

    /// <summary>Opens every widget saved in settings (called at startup).</summary>
    public static void RestoreAll()
    {
        foreach (var p in Settings.Current.PinnedWidgets.ToList()) new WidgetWindow(p).Show();
    }

    public static void PinNew(string kind)
    {
        var area = SystemParameters.WorkArea;
        var n = Settings.Current.PinnedWidgets.Count;
        var pin = new WidgetPin { Kind = kind, Left = area.Left + 40 + n * 24, Top = area.Top + 80 + n * 24 };
        Settings.Current.PinnedWidgets.Add(pin);
        Settings.Save();
        new WidgetWindow(pin).Show();
    }
}
