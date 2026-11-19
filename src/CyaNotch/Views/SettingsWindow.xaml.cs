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

        AccentPanel.Children.Clear();
        AccentPanel.Children.Add(AccentChoice("Theme", null, s.Accent));
        foreach (var a in Themes.Accents) AccentPanel.Children.Add(AccentChoice(a.Name, a.Color, s.Accent));
        GlassTint.Value = Math.Round(s.GlassTint * 100);
        HideCapture.IsChecked = s.HideFromCapture;

        ShapeFloating.IsChecked = s.Shape == IslandShape.Floating;
        ShapeNotch.IsChecked = s.Shape == IslandShape.Notch;
        VizBars.IsChecked = s.VisualizerStyle == VisualizerStyle.Bars;
        VizTitle.IsChecked = s.VisualizerStyle == VisualizerStyle.BarsWithTitle;
        VizOff.IsChecked = s.VisualizerStyle == VisualizerStyle.Off;
        VizDots.IsChecked = s.VisualizerStyle == VisualizerStyle.Dots;
        ArtColorsBox.IsChecked = s.ArtworkColors;
        AmbientBox.IsChecked = s.AmbientArtwork;
        VizMirror.IsChecked = s.VisualizerMirrored;
        Distance.Value = s.DistanceFromTop;
        IslandWidth.Value = s.CollapsedWidth;
        Speed.Value = s.AnimationSpeed;
        StartWithWindows.IsChecked = s.StartWithWindows;
        AutoHide.IsChecked = s.AutoHide;
        ShowPlaying.IsChecked = s.ShowWhilePlaying;
        Glance.IsChecked = s.StatsGlance;
        FsStrip.IsChecked = s.FullscreenMode == GameMode.Strip;
        FsHide.IsChecked = s.FullscreenMode == GameMode.Hide;
        FsOff.IsChecked = s.FullscreenMode == GameMode.Off;
        LyricsOn.IsChecked = s.LyricsEnabled;
        LyricsPill.IsChecked = s.LyricsInPill;
        RefreshDeckApps();
        WidgetButtons.Children.Clear();
        foreach (var k in WidgetWindow.Kinds)
        {
            var b = new Button { Content = $"Pin {k.ToLowerInvariant()}", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => WidgetWindow.PinNew(k);
            WidgetButtons.Children.Add(b);
        }
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

    /// <summary>A round colour button; "Theme" (no colour) keeps each theme's own accent.</summary>
    private RadioButton AccentChoice(string name, Color? color, string current)
    {
        var dot = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1),
            Background = color is { } c ? new SolidColorBrush(c)
                : new LinearGradientBrush(Color.FromRgb(0x40, 0xE0, 0xE8), Color.FromRgb(0xFF, 0x8A, 0xC8), 45),
        };
        var rb = new RadioButton
        {
            GroupName = "Accent",
            IsChecked = name == current,
            ToolTip = name == "Theme" ? "Theme's own colour" : name,
            Margin = new Thickness(0, 0, 6, 4),
            Content = dot,
        };
        rb.Checked += (_, _) => { if (!_loading) { Settings.Current.Accent = name; Settings.Save(); } };
        return rb;
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

    private void RefreshDeckApps()
    {
        DeckApps.ItemsSource = null;
        DeckApps.ItemsSource = Settings.Current.DeckApps.ToList();
        PhonesText.Text = $"{Settings.Current.ApprovedPhones.Count} phone(s) approved.";
    }

    private void AddDeckApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an app for the phone remote",
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        if (dlg.ShowDialog(this) != true) return;
        Settings.Current.DeckApps.Add(new DeckApp
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
            Path = dlg.FileName,
        });
        Settings.Save();
        RefreshDeckApps();
    }

    private void RemoveDeckApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeckApp a) return;
        Settings.Current.DeckApps.Remove(a);
        Settings.Save();
        RefreshDeckApps();
    }

    private void ResetPhones_Click(object sender, RoutedEventArgs e)
    {
        App.Deck.ResetKey();
        RefreshDeckApps();
        PhonesText.Text = "All phones forgotten. Scan the new code to connect again.";
    }

    private void RefreshClocks()
    {
        Clocks.ItemsSource = null;
        Clocks.ItemsSource = Settings.Current.WorldClocks.ToList();
    }

    private void UpdateSliderLabels()
    {
        if (DistanceText == null || WidthText == null || SpeedText == null || GlassTintText == null) return;
        GlassTintText.Text = $"{GlassTint.Value:0}%";
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
                          : VizOff.IsChecked == true ? VisualizerStyle.Off
                          : VizDots.IsChecked == true ? VisualizerStyle.Dots : VisualizerStyle.Bars;
        s.ArtworkColors = ArtColorsBox.IsChecked == true;
        s.AmbientArtwork = AmbientBox.IsChecked == true;
        s.VisualizerMirrored = VizMirror.IsChecked == true;
        s.GlassTint = GlassTint.Value / 100;
        s.HideFromCapture = HideCapture.IsChecked == true;
        s.DistanceFromTop = Distance.Value;
        s.CollapsedWidth = IslandWidth.Value;
        s.AnimationSpeed = Speed.Value;
        s.FullscreenMode = FsHide.IsChecked == true ? GameMode.Hide : FsOff.IsChecked == true ? GameMode.Off : GameMode.Strip;
        s.LyricsEnabled = LyricsOn.IsChecked == true;
        s.LyricsInPill = LyricsPill.IsChecked == true;
        s.MicMuteHotkey = MicHotkey.IsChecked == true;
        s.AutoHide = AutoHide.IsChecked == true;
        s.ShowWhilePlaying = ShowPlaying.IsChecked == true;
        s.StatsGlance = Glance.IsChecked == true;
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
