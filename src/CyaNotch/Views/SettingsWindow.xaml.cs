using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        Closed += (_, _) => PvViz.Active = false;
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
                Style = (Style)FindResource("Swatch"),
                Margin = new Thickness(0, 0, 6, 6),
                Content = ThemeSwatch(t),
            };
            rb.Checked += (_, _) => { if (!_loading) { Settings.Current.Theme = t.Name; Settings.Save(); UpdatePreview(); } };
            ThemePanel.Children.Add(rb);
        }

        AccentPanel.Children.Clear();
        AccentPanel.Children.Add(AccentChoice("Theme", null, s.Accent));
        foreach (var a in Themes.Accents) AccentPanel.Children.Add(AccentChoice(a.Name, a.Color, s.Accent));
        GlassTint.Value = Math.Round(s.GlassTint * 100);
        Round.Value = Math.Round(s.Roundness * 100);
        Resting.SelectedIndex = (int)s.RestingInfo;
        SideCentre.IsChecked = s.Side == IslandSide.Centre;
        SideLeft.IsChecked = s.Side == IslandSide.Left;
        SideRight.IsChecked = s.Side == IslandSide.Right;
        MonitorBox.Items.Clear();
        MonitorBox.Items.Add("Main screen");
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
            MonitorBox.Items.Add($"Screen {i + 1} ({screens[i].Bounds.Width}×{screens[i].Bounds.Height}){(screens[i].Primary ? ", main" : "")}");
        MonitorBox.SelectedIndex = s.Monitor >= 0 && s.Monitor < screens.Length ? s.Monitor + 1 : 0;
        Delay.Value = s.OpenDelayMs;
        PeekSecs.Value = s.PeekSeconds;
        MediaAll.IsChecked = s.MediaFilter == MediaFilter.All;
        MediaMusic.IsChecked = s.MediaFilter == MediaFilter.MusicApps;
        MediaExcept.IsChecked = s.MediaFilter == MediaFilter.AllExcept;
        MediaIgnore.Text = string.Join(", ", s.MediaIgnoredApps);
        var seen = App.Media.KnownApps();
        MediaSeen.Text = seen.Length == 0 ? "" : "Apps playing media right now: " + string.Join(", ", seen);
        HideCapture.IsChecked = s.HideFromCapture;
        TaskbarOn.IsChecked = s.TaskbarEnabled;
        TaskbarHideWin.IsChecked = s.TaskbarHideWindows;

        ShapeFloating.IsChecked = s.Shape == IslandShape.Floating;
        ShapeNotch.IsChecked = s.Shape == IslandShape.Notch;
        VizBars.IsChecked = s.VisualizerStyle == VisualizerStyle.Bars;
        VizTitle.IsChecked = s.VisualizerStyle == VisualizerStyle.BarsWithTitle;
        VizOff.IsChecked = s.VisualizerStyle == VisualizerStyle.Off;
        VizDots.IsChecked = s.VisualizerStyle == VisualizerStyle.Dots;
        ArtColorsBox.IsChecked = s.ArtworkColors;
        AmbientBox.IsChecked = s.AmbientArtwork;
        VizMirror.IsChecked = s.VisualizerMirrored;
        ProgWave.IsChecked = s.ProgressStyle == ProgressStyle.Wave;
        ProgLine.IsChecked = s.ProgressStyle == ProgressStyle.Line;
        ProgBar.IsChecked = s.ProgressStyle == ProgressStyle.Bar;
        ProgDots.IsChecked = s.ProgressStyle == ProgressStyle.Dots;
        ProgGlow.IsChecked = s.ProgressStyle == ProgressStyle.Glow;
        OpenMusic.IsChecked = s.OpenOn == "Music";
        OpenLast.IsChecked = s.OpenOn != "Music";
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
            var b = new Button { Content = $"Pin {k.ToLowerInvariant()}", Style = (Style)FindResource("PillButton"), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => WidgetWindow.PinNew(k);
            WidgetButtons.Children.Add(b);
        }
        MicHotkey.IsChecked = s.MicMuteHotkey;
        LauncherKeys.IsChecked = s.LauncherHotkeys;
        AgentsOn.IsChecked = s.AgentsEnabled;
        var ver = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = ver == null ? "" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        ShowNotes.IsChecked = s.ShowNotifications;
        Clock24.IsChecked = s.Use24Hour;
        Fahrenheit.IsChecked = s.UseFahrenheit;
        ClipLimit.SelectedIndex = s.ClipboardLimit switch { <= 50 => 0, <= 100 => 1, <= 200 => 2, _ => 3 };
        City.Text = s.WeatherCity;
        if (!string.IsNullOrEmpty(s.WeatherCity)) CityStatus.Text = $"Showing weather for {s.WeatherCity}.";

        ClockZone.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        RefreshClocks();
        UpdateSliderLabels();
        UpdatePreview();
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
            Style = (Style)FindResource("Swatch"),
            Margin = new Thickness(0, 0, 4, 4),
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
        if (DistanceText == null || WidthText == null || SpeedText == null || GlassTintText == null
            || RoundText == null || DelayText == null || PeekText == null) return;
        RoundText.Text = Round.Value switch { 0 => "Square", 100 => "Round", _ => $"{Round.Value:0}%" };
        DelayText.Text = Delay.Value == 0 ? "Instant" : $"{Delay.Value:0} ms";
        PeekText.Text = $"{PeekSecs.Value:0} s";
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
        s.ProgressStyle = ProgLine.IsChecked == true ? ProgressStyle.Line
                        : ProgBar.IsChecked == true ? ProgressStyle.Bar
                        : ProgDots.IsChecked == true ? ProgressStyle.Dots
                        : ProgGlow.IsChecked == true ? ProgressStyle.Glow : ProgressStyle.Wave;
        s.OpenOn = OpenMusic.IsChecked == true ? "Music" : "Last";
        s.GlassTint = GlassTint.Value / 100;
        s.Roundness = Round.Value / 100;
        if (Resting.SelectedIndex >= 0) s.RestingInfo = (RestingInfo)Resting.SelectedIndex;
        s.Side = SideLeft.IsChecked == true ? IslandSide.Left : SideRight.IsChecked == true ? IslandSide.Right : IslandSide.Centre;
        s.Monitor = MonitorBox.SelectedIndex - 1;
        s.OpenDelayMs = (int)Delay.Value;
        s.PeekSeconds = (int)PeekSecs.Value;
        s.MediaFilter = MediaMusic.IsChecked == true ? MediaFilter.MusicApps : MediaExcept.IsChecked == true ? MediaFilter.AllExcept : MediaFilter.All;
        s.MediaIgnoredApps = MediaIgnore.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        s.HideFromCapture = HideCapture.IsChecked == true;
        s.TaskbarEnabled = TaskbarOn.IsChecked == true;
        s.TaskbarHideWindows = TaskbarHideWin.IsChecked == true;
        s.DistanceFromTop = Distance.Value;
        s.CollapsedWidth = IslandWidth.Value;
        s.AnimationSpeed = Speed.Value;
        s.FullscreenMode = FsHide.IsChecked == true ? GameMode.Hide : FsOff.IsChecked == true ? GameMode.Off : GameMode.Strip;
        s.LyricsEnabled = LyricsOn.IsChecked == true;
        s.LyricsInPill = LyricsPill.IsChecked == true;
        s.MicMuteHotkey = MicHotkey.IsChecked == true;
        s.LauncherHotkeys = LauncherKeys.IsChecked == true;
        s.AgentsEnabled = AgentsOn.IsChecked == true;
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
        App.Media.Reselect();
        Settings.Save();
        UpdatePreview();
    }

    // ---- window chrome, sidebar, search and live preview ----

    private bool _skipScrollSync;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key || FindName(key) is not FrameworkElement sec) return;
        if (sec.Visibility != Visibility.Visible) return;
        _skipScrollSync = true;
        var y = sec.TranslatePoint(new Point(0, 0), Scroller).Y;
        Scroller.ScrollToVerticalOffset(Math.Max(0, Scroller.VerticalOffset + y - 4));
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Sections == null || e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
        if (_skipScrollSync) { _skipScrollSync = false; return; }
        FrameworkElement? current = null;
        foreach (var sec in SectionPanels())
        {
            if (sec.Visibility != Visibility.Visible) continue;
            current ??= sec;
            if (sec.TranslatePoint(new Point(0, 0), Scroller).Y <= 60) current = sec;
        }
        // at the very bottom, the last section counts even if it never reaches the top
        if (Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1)
            current = SectionPanels().LastOrDefault(x => x.Visibility == Visibility.Visible) ?? current;
        if (current != null && FindName("Nav" + current.Name[3..]) is RadioButton nav) nav.IsChecked = true;
    }

    private System.Collections.Generic.IEnumerable<StackPanel> SectionPanels() =>
        Sections.Children.OfType<StackPanel>().Where(p => p.Name.StartsWith("Sec"));

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (Sections == null) return;
        var q = Search.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rowStyle = (Style)FindResource("Row");
        var any = false;
        foreach (var sec in SectionPanels())
        {
            var hits = 0;
            foreach (UIElement child in sec.Children)
            {
                if (child is not FrameworkElement el) continue;
                var isRow = el is CheckBox || el is Border b && b.Style == rowStyle;
                if (q.Length == 0) { el.Visibility = Visibility.Visible; continue; }
                if (isRow)
                {
                    var match = TextOf(el).Contains(q, StringComparison.OrdinalIgnoreCase)
                             || ((string)sec.Tag).Contains(q, StringComparison.OrdinalIgnoreCase);
                    el.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (match) hits++;
                }
                else
                {
                    // the kicker and section title stay; group headers and loose extras hide while searching
                    var keep = el is TextBlock tb && (tb.Style == (Style)FindResource("SecTitle") || tb.Style == (Style)FindResource("Kicker"));
                    el.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            var show = q.Length == 0 || hits > 0;
            sec.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            any |= show;
        }
        NoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Scroller.ScrollToTop();
    }

    /// <summary>All the words a row shows, for search: labels, descriptions and option names.</summary>
    private static string TextOf(DependencyObject root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(object o)
        {
            switch (o)
            {
                case TextBlock tb: sb.Append(tb.Text).Append(' '); break;
                case string str: sb.Append(str).Append(' '); return;
            }
            if (o is FrameworkElement fe && fe.Tag is string tag) sb.Append(tag).Append(' ');
            if (o is ContentControl cc && cc.Content is string cs) sb.Append(cs).Append(' ');
            if (o is ComboBox cb) foreach (var it in cb.Items) if (it is ComboBoxItem ci && ci.Content is string s2) sb.Append(s2).Append(' ');
            if (o is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(root);
        return sb.ToString();
    }

    private void PvMode_Checked(object sender, RoutedEventArgs e) => UpdatePreview();

    private void PvIsland_Click(object sender, MouseButtonEventArgs e)
    {
        if (PvOpenBtn.IsChecked == true) PvRestBtn.IsChecked = true; else PvOpenBtn.IsChecked = true;
    }

    /// <summary>Draws the mock island the way the real one will look with the current settings.</summary>
    private void UpdatePreview()
    {
        if (PvIsland == null || PvOpen == null || PvResting == null || PvOpenBtn == null) return;
        var s = Settings.Current;
        var open = PvOpenBtn.IsChecked == true;
        var notch = ShapeNotch?.IsChecked == true;
        var round = (Round?.Value ?? s.Roundness * 100) / 100;
        PvResting.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        PvOpen.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        var restW = Math.Clamp((IslandWidth?.Value ?? s.CollapsedWidth) * 0.55, 110, 190);
        PvIsland.Width = open ? 320 : restW;
        PvIsland.Height = open ? double.NaN : 30;
        var r = open ? 6 + 18 * round : 15 * Math.Max(round, 0.2);
        PvIsland.CornerRadius = notch ? new CornerRadius(0, 0, r, r) : new CornerRadius(r);
        PvIsland.Margin = new Thickness(0, notch ? 0 : 10, 0, 0);
        PvIsland.BorderThickness = notch ? new Thickness(1, 0, 1, 1) : new Thickness(1);

        PvProgress.Look = ProgLine?.IsChecked == true ? ProgressStyle.Line
                        : ProgBar?.IsChecked == true ? ProgressStyle.Bar
                        : ProgDots?.IsChecked == true ? ProgressStyle.Dots
                        : ProgGlow?.IsChecked == true ? ProgressStyle.Glow
                        : ProgWave?.IsChecked == true ? ProgressStyle.Wave : s.ProgressStyle;
        PvViz.Visibility = VizOff?.IsChecked == true ? Visibility.Hidden : Visibility.Visible;
        PvViz.Mirrored = VizMirror?.IsChecked == true;
        PvViz.Active = !open;
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
