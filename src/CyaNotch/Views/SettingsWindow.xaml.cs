using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CyaNotch.Core;
using CyaNotch.Services;

namespace CyaNotch.Views;

public partial class SettingsWindow : Window
{
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        Load();
        _loading = false;
    }

    private void Load()
    {
        var s = Settings.Current;

        ThemePanel.Children.Clear();
        foreach (var t in Themes.All)
        {
            var rb = new RadioButton
            {
                GroupName = "Theme",
                IsChecked = t.Name == s.Theme,
                Tag = t.Name,
                Margin = new Thickness(0, 0, 10, 8),
                Content = ThemeSwatch(t),
            };
            rb.Checked += (_, _) => { if (!_loading) { Settings.Current.Theme = t.Name; Settings.Save(); } };
            ThemePanel.Children.Add(rb);
        }

        ShapeFloating.IsChecked = s.Shape == IslandShape.Floating;
        ShapeNotch.IsChecked = s.Shape == IslandShape.Notch;
        VizBars.IsChecked = s.VisualizerStyle == VisualizerStyle.Bars;
        VizTitle.IsChecked = s.VisualizerStyle == VisualizerStyle.BarsWithTitle;
        VizOff.IsChecked = s.VisualizerStyle == VisualizerStyle.Off;
        VizMirror.IsChecked = s.VisualizerMirrored;
        Distance.Value = s.DistanceFromTop;
        IslandWidth.Value = s.CollapsedWidth;
        Speed.Value = s.AnimationSpeed;
        StartWithWindows.IsChecked = s.StartWithWindows;
        HideFullscreen.IsChecked = s.HideInFullscreen;
        MicHotkey.IsChecked = s.MicMuteHotkey;
        ShowNotes.IsChecked = s.ShowNotifications;
        Clock24.IsChecked = s.Use24Hour;
        Fahrenheit.IsChecked = s.UseFahrenheit;
        ClipLimit.SelectedIndex = s.ClipboardLimit switch { <= 50 => 0, <= 100 => 1, <= 200 => 2, _ => 3 };
        City.Text = s.WeatherCity;
        if (!string.IsNullOrEmpty(s.WeatherCity)) CityStatus.Text = $"Showing weather for {s.WeatherCity}.";

        ClockZone.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        RefreshClocks();
        UpdateSliderLabels();
    }

    private static FrameworkElement ThemeSwatch(ThemeDef t)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var pill = new Border
        {
            Width = 46,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(255, t.Surface.R, t.Surface.G, t.Surface.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 0, 6, 0),
            Child = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(t.Accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 5, 0),
            },
        };
        panel.Children.Add(pill);
        panel.Children.Add(new TextBlock { Text = t.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.WhiteSmoke });
        return panel;
    }

    private void RefreshClocks()
    {
        Clocks.ItemsSource = null;
        Clocks.ItemsSource = Settings.Current.WorldClocks.ToList();
    }

    private void UpdateSliderLabels()
    {
        if (DistanceText == null || WidthText == null || SpeedText == null) return;
        DistanceText.Text = $"{Distance.Value:0} px";
        WidthText.Text = $"{IslandWidth.Value:0} px";
        SpeedText.Text = Speed.Value switch
        {
            < 0.75 => "Fast",
            > 1.3 => "Relaxed",
            _ => "Normal",
        };
    }

    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
        Changed(sender, e);
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Settings.Current;
        s.Shape = ShapeNotch.IsChecked == true ? IslandShape.Notch : IslandShape.Floating;
        s.VisualizerStyle = VizTitle.IsChecked == true ? VisualizerStyle.BarsWithTitle
                          : VizOff.IsChecked == true ? VisualizerStyle.Off : VisualizerStyle.Bars;
        s.VisualizerMirrored = VizMirror.IsChecked == true;
        s.DistanceFromTop = Distance.Value;
        s.CollapsedWidth = IslandWidth.Value;
        s.AnimationSpeed = Speed.Value;
        s.HideInFullscreen = HideFullscreen.IsChecked == true;
        s.MicMuteHotkey = MicHotkey.IsChecked == true;
        s.ShowNotifications = ShowNotes.IsChecked == true;
        s.Use24Hour = Clock24.IsChecked == true;
        s.UseFahrenheit = Fahrenheit.IsChecked == true;
        s.ClipboardLimit = ClipLimit.SelectedIndex switch { 0 => 50, 1 => 100, 2 => 200, _ => 500 };

        var startup = StartWithWindows.IsChecked == true;
        if (startup != s.StartWithWindows)
        {
            s.StartWithWindows = startup;
            App.ApplyStartup(startup);
        }
        Settings.Save();
    }

    private async void SetCity_Click(object sender, RoutedEventArgs e)
    {
        var city = City.Text.Trim();
        if (city.Length == 0)
        {
            Settings.Current.WeatherCity = "";
            Settings.Current.WeatherLat = Settings.Current.WeatherLon = null;
            Settings.Save();
            CityStatus.Text = "Weather is off.";
            return;
        }
        CityStatus.Text = "Looking up…";
        var name = await WeatherService.GeocodeAsync(city);
        if (name == null)
        {
            CityStatus.Text = $"Couldn't find \"{city}\". Try a bigger nearby city.";
            return;
        }
        City.Text = name;
        CityStatus.Text = $"Showing weather for {name}.";
        Settings.Save();
    }

    private void AddClock_Click(object sender, RoutedEventArgs e)
    {
        if (ClockZone.SelectedItem is not TimeZoneInfo tz) return;
        var label = string.IsNullOrWhiteSpace(ClockLabel.Text) ? tz.StandardName : ClockLabel.Text.Trim();
        Settings.Current.WorldClocks.Add(new WorldClock { Label = label, TimeZoneId = tz.Id });
        ClockLabel.Clear();
        Settings.Save();
        RefreshClocks();
    }

    private void RemoveClock_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorldClock c) return;
        Settings.Current.WorldClocks.Remove(c);
        Settings.Save();
        RefreshClocks();
    }
}
