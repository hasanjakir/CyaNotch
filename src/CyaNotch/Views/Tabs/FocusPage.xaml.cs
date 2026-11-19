using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CyaNotch.Core;
using CyaNotch.Services;

namespace CyaNotch.Views.Tabs;

/// <summary>Focus timer: pick a length, start, and the island counts down.</summary>
public partial class FocusPage : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public FocusPage()
    {
        InitializeComponent();
        foreach (var m in new[] { 15, 25, 45, 60, 90 }) Presets.Children.Add(Chip(m, "Focus"));
        foreach (var m in new[] { 5, 10, 15 }) BreakPresets.Children.Add(Chip(m, "Break"));
        _tick.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); _tick.Start(); } else _tick.Stop(); };
        App.Focus.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Silence.IsChecked = Settings.Current.FocusSilence;
    }

    private RadioButton Chip(int minutes, string group)
    {
        var current = group == "Focus" ? Settings.Current.FocusMinutes : Settings.Current.BreakMinutes;
        var rb = new RadioButton
        {
            Style = (Style)FindResource("Chip"),
            GroupName = group,
            Content = $"{minutes} min",
            IsChecked = minutes == current,
            Margin = new Thickness(0, 0, 6, 6),
        };
        rb.Checked += (_, _) =>
        {
            if (group == "Focus") Settings.Current.FocusMinutes = minutes; else Settings.Current.BreakMinutes = minutes;
            Settings.SaveQuietly();
            Refresh();
        };
        return rb;
    }

    private void Refresh()
    {
        var f = App.Focus;
        var idle = f.Current == FocusService.Phase.Idle;
        var left = idle ? TimeSpan.FromMinutes(Settings.Current.FocusMinutes) : f.Left;
        Clock.Text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
        Phase.Text = f.Current switch
        {
            FocusService.Phase.Focus => "Focusing",
            FocusService.Phase.Break => "On a break",
            _ => "Ready to focus",
        };
        Today.Text = f.SessionsToday == 0 ? "" : f.SessionsToday == 1 ? "1 session today" : $"{f.SessionsToday} sessions today";
        StartBtn.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        BreakBtn.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        Arc.Data = ArcGeometry(idle ? 0 : f.Progress, 95);
    }

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="fraction"/> of a circle.</summary>
    private static Geometry ArcGeometry(double fraction, double r)
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        if (fraction <= 0) return Geometry.Empty;
        const double c = 100;
        var angle = fraction * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => App.Focus.Start(Settings.Current.FocusMinutes);
    private void Break_Click(object sender, RoutedEventArgs e) => App.Focus.StartBreak(Settings.Current.BreakMinutes);
    private void Stop_Click(object sender, RoutedEventArgs e) => App.Focus.Stop();

    private void Silence_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.FocusSilence = Silence.IsChecked == true;
        Settings.SaveQuietly();
    }
}
