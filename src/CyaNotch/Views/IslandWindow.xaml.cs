using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CyaNotch.Core;
using CyaNotch.Services;
using Windows.Devices.Radios;

namespace CyaNotch.Views;

public partial class IslandWindow : Window
{
    private enum Mode { Compact, Peek, Expanded }

    private const int HotkeyMicId = 0xB001;

    private Mode _mode = Mode.Compact;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private IntPtr _prevForeground;
    private bool _hiddenByUser;
    private bool _hiddenForFullscreen;

    private readonly DispatcherTimer _expandDelay = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(380) };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(20) };

    private readonly Queue<(string icon, string title, string body)> _peeks = new();

    // Timer widget
    private DateTime? _timerEnd;

    // Call timer (mic in use)
    private DateTime? _callStart;

    // Media
    private bool _seeking;

    // Shelf drag
    private Point _dragStart;
    private ShelfItem? _dragItem;

    // Clipboard filter
    private string _clipFilter = "All";

    public IslandWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (_mode == Mode.Expanded && !Island.IsMouseOver) Collapse(); };

        _expandDelay.Tick += (_, _) => { _expandDelay.Stop(); Expand(); };
        _collapseDelay.Tick += (_, _) =>
        {
            _collapseDelay.Stop();
            if (Island.IsMouseOver) return;
            if (IsKeyboardFocusWithin && IsActive) return; // typing in a box: wait for click-away
            Collapse();
        };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); EndPeek(); };
        _fast.Tick += (_, _) => FastTick();
        _slow.Tick += (_, _) => SlowTick();
        _weatherTimer.Tick += async (_, _) => await App.Weather.RefreshAsync();

        Settings.Changed += OnSettingsChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PositionWindow);
    }

    // =====================================================================
    // Window plumbing
    // =====================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        // Hide from Alt+Tab and the taskbar.
        var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);

        Native.AddClipboardFormatListener(_hwnd);
        RegisterMicHotkey();
    }

    private void RegisterMicHotkey()
    {
        Native.UnregisterHotKey(_hwnd, HotkeyMicId);
        if (!Settings.Current.MicMuteHotkey) return;
        if (!Native.RegisterHotKey(_hwnd, HotkeyMicId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x4D /* M */))
            Log.Info("Ctrl+Alt+M is already used by another app");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_CLIPBOARDUPDATE:
                App.Clipboard.Capture();
                UpdateClipEmpty();
                break;
            case Native.WM_HOTKEY when wParam.ToInt32() == HotkeyMicId:
                ToggleMic();
                handled = true;
                break;
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                Dispatcher.BeginInvoke(PositionWindow);
                break;
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        ApplyShape(animate: false);

        ClipList.ItemsSource = App.Clipboard.Items;
        CollectionViewSource.GetDefaultView(App.Clipboard.Items).Filter = ClipFilterPredicate;
        UpdateClipEmpty();

        FolderList.ItemsSource = App.Shelf.Folders;
        FolderList.SelectedIndex = 0;
        App.Shelf.Items.CollectionChanged += (_, _) => RefreshShelf();
        RefreshShelf();

        NotesList.ItemsSource = App.Notifications.Items;
        App.Notifications.Items.CollectionChanged += (_, _) => NotesEmpty.Visibility = App.Notifications.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotesEmpty.Visibility = App.Notifications.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ReminderList.ItemsSource = App.Reminders.Reminders;
        TodoList.ItemsSource = App.Reminders.Todos;
        App.Reminders.Due += r =>
        {
            ShowPeek("", r.Text, "Reminder · snooze or mark done in the island");
            System.Media.SystemSounds.Asterisk.Play();
        };

        App.Media.Changed += s => Dispatcher.BeginInvoke(() => ApplyMedia(s));
        App.Notifications.Arrived += n => Dispatcher.BeginInvoke(() =>
        {
            App.Notifications.Add(n);
            if (Settings.Current.ShowNotifications) ShowPeek("", n.Title, string.IsNullOrEmpty(n.Body) ? n.App : n.Body);
        });
        App.Audio.MicMuteChanged += () => Dispatcher.BeginInvoke(UpdateMicUi);
        App.Weather.Updated += w => Dispatcher.BeginInvoke(() => ApplyWeather(w));

        _fast.Start();
        _slow.Start();
        _weatherTimer.Start();
        FastTick();
        SlowTick();
        UpdateMicUi();

        await App.Media.StartAsync();
        await App.Notifications.StartAsync();
        NotesStatus.Text = App.Notifications.Status == "On" ? "Showing Windows notifications" : App.Notifications.Status;
        await App.Weather.RefreshAsync();
    }

    /// <summary>Top centre of the primary screen.</summary>
    private void PositionWindow()
    {
        var area = SystemParameters.WorkArea;
        var screenW = SystemParameters.PrimaryScreenWidth;
        Left = (screenW - Width) / 2;
        Top = 0;
        // If the taskbar is docked at the top, sit just below it.
        if (area.Top > 0) Top = area.Top;
    }

    public void ToggleHidden()
    {
        _hiddenByUser = !_hiddenByUser;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var hide = _hiddenByUser || (_hiddenForFullscreen && _mode != Mode.Expanded);
        Island.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
        UpdateCompactLayout();
    }

    public void Shutdown()
    {
        try
        {
            Native.UnregisterHotKey(_hwnd, HotkeyMicId);
            Native.RemoveClipboardFormatListener(_hwnd);
        }
        catch { }
        Close();
    }

    private void OnSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Themes.Apply(Settings.Current.Theme);
            RegisterMicHotkey();
            ApplyShape(animate: true);
            UpdateCompactLayout();
            WorldList.ItemsSource = null;
            SlowTick();
            _ = App.Weather.RefreshAsync();
        });
    }

    // =====================================================================
    // Sizes, shape and animation
    // =====================================================================

    private double Speed => Math.Clamp(Settings.Current.AnimationSpeed, 0.3, 3);
    private bool IsNotch => Settings.Current.Shape == IslandShape.Notch;

    private Size CompactSize()
    {
        var w = Settings.Current.CollapsedWidth;
        var h = Settings.Current.CollapsedHeight;
        var m = App.Media.State;
        if (m.HasSession && m.IsPlaying) w += Settings.Current.VisualizerStyle == VisualizerStyle.Off ? 70 : 130;
        if (_callStart != null) w += 40;
        if (_timerEnd != null) w += 30;
        return new Size(w, h);
    }

    private static Size PeekSize() => new(400, 64);
    private static Size ExpandedSize() => new(700, 430);

    private void ApplyShape(bool animate)
    {
        Island.Margin = new Thickness(0, IsNotch ? 0 : Settings.Current.DistanceFromTop, 0, 0);
        Island.BorderThickness = IsNotch ? new Thickness(1, 0, 1, 1) : new Thickness(1);
        var size = _mode switch
        {
            Mode.Expanded => ExpandedSize(),
            Mode.Peek => PeekSize(),
            _ => CompactSize(),
        };
        AnimateTo(size, animate);
    }

    private void AnimateTo(Size size, bool animate = true, bool bouncy = true)
    {
        var radius = _mode switch
        {
            Mode.Expanded => 28.0,
            Mode.Peek => 26.0,
            _ => size.Height / 2,
        };
        var cr = IsNotch ? new CornerRadius(0, 0, radius, radius) : new CornerRadius(radius);

        if (!animate)
        {
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            Island.Width = size.Width;
            Island.Height = size.Height;
            Island.CornerRadius = cr;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(460 * Speed);
        IEasingFunction ease = bouncy
            ? new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        var wa = new DoubleAnimation(size.Width, duration) { EasingFunction = ease };
        var ha = new DoubleAnimation(size.Height, duration) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(wa, 120);
        Timeline.SetDesiredFrameRate(ha, 120);
        Island.BeginAnimation(WidthProperty, wa, HandoffBehavior.SnapshotAndReplace);
        Island.BeginAnimation(HeightProperty, ha, HandoffBehavior.SnapshotAndReplace);
        Island.CornerRadius = cr;
    }

    private static void Fade(UIElement el, bool show, double ms, double delayMs = 0)
    {
        if (show) el.Visibility = Visibility.Visible;
        var a = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        if (!show) a.Completed += (_, _) => { if (el.Opacity == 0) el.Visibility = Visibility.Collapsed; };
        el.BeginAnimation(OpacityProperty, a);
    }

    // =====================================================================
    // Mode changes
    // =====================================================================

    private void Expand(RadioButton? tab = null)
    {
        if (_hiddenByUser) return;
        if (tab != null) tab.IsChecked = true;
        if (_mode == Mode.Expanded) return;

        var fg = Native.GetForegroundWindow();
        if (fg != _hwnd) _prevForeground = fg;

        _peekTimer.Stop();
        _mode = Mode.Expanded;
        UpdateVisibility();
        Fade(Compact, false, 90 * Speed);
        Fade(Peek, false, 90 * Speed);
        Fade(Expanded, true, 220 * Speed, 120 * Speed);
        AnimateTo(ExpandedSize());
        RefreshExpanded();
    }

    private void Collapse()
    {
        if (_mode != Mode.Expanded) return;
        _mode = Mode.Compact;
        Keyboard.ClearFocus();
        Fade(Expanded, false, 90 * Speed);
        Fade(Compact, true, 200 * Speed, 160 * Speed);
        AnimateTo(CompactSize(), bouncy: false);
        UpdateVisibility();
        if (_peeks.Count > 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(NextPeek));
    }

    private void ShowPeek(string icon, string title, string body)
    {
        _peeks.Enqueue((icon, title, body));
        if (_mode == Mode.Compact) NextPeek();
    }

    private void NextPeek()
    {
        if (_mode == Mode.Expanded || _peeks.Count == 0) return;
        var (icon, title, body) = _peeks.Dequeue();
        PeekIcon.Text = icon;
        PeekTitle.Text = title;
        PeekBody.Text = body;
        PeekBody.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;

        if (_mode != Mode.Peek)
        {
            _mode = Mode.Peek;
            Fade(Compact, false, 80 * Speed);
            Fade(Peek, true, 200 * Speed, 120 * Speed);
            AnimateTo(PeekSize());
        }
        // Peeks show even over fullscreen apps for a moment.
        Island.Visibility = _hiddenByUser ? Visibility.Hidden : Visibility.Visible;
        UpdateCompactLayout();
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    private void EndPeek()
    {
        if (_mode != Mode.Peek) return;
        if (_peeks.Count > 0)
        {
            NextPeek();
            return;
        }
        _mode = Mode.Compact;
        Fade(Peek, false, 80 * Speed);
        Fade(Compact, true, 200 * Speed, 140 * Speed);
        AnimateTo(CompactSize(), bouncy: false);
        UpdateVisibility();
    }

    // =====================================================================
    // Hover, click and drag onto the island
    // =====================================================================

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseDelay.Stop();
        if (_mode != Mode.Expanded)
        {
            _expandDelay.Interval = TimeSpan.FromMilliseconds(140 * Speed);
            _expandDelay.Start();
        }
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandDelay.Stop();
        if (_mode == Mode.Expanded) _collapseDelay.Start();
    }

    private void Island_Click(object sender, MouseButtonEventArgs e)
    {
        if (_mode != Mode.Expanded)
        {
            _expandDelay.Stop();
            Expand();
        }
    }

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _collapseDelay.Stop();
        Expand(TabShelf);
        ShelfDrop.BorderBrush = (Brush)FindResource("AccentBrush");
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Island_DragLeave(object sender, DragEventArgs e)
    {
        ShelfDrop.BorderBrush = (Brush)FindResource("TrackBrush");
        _collapseDelay.Start();
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        ShelfDrop.BorderBrush = (Brush)FindResource("TrackBrush");
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            // Dropping one of our own tiles back on the shelf does nothing.
            if (_dragItem != null && files.Length == 1 && files[0] == _dragItem.Path) return;
            App.Shelf.Add(files, CurrentFolder);
            TabShelf.IsChecked = true;
        }
        e.Handled = true;
    }

    // =====================================================================
    // Ticks
    // =====================================================================

    private void FastTick()
    {
        var now = DateTime.Now;
        var timeFmt = Settings.Current.Use24Hour ? "HH:mm" : "h:mm";
        HeaderClock.Text = now.ToString("ddd d MMM  " + timeFmt + (Settings.Current.Use24Hour ? "" : " tt"));

        // Timer
        string? timerText = null;
        if (_timerEnd is { } end)
        {
            var left = end - now;
            if (left <= TimeSpan.Zero)
            {
                _timerEnd = null;
                TimerText.Text = "00:00";
                ShowPeek("", "Time's up", "Your timer finished");
                System.Media.SystemSounds.Exclamation.Play();
                AnimateIfCompact();
            }
            else
            {
                timerText = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
                TimerText.Text = timerText;
            }
        }

        // Media progress
        var m = App.Media.State;
        if (_mode == Mode.Expanded && m.HasSession && !_seeking && m.Duration > TimeSpan.Zero)
        {
            var pos = m.LivePosition;
            MediaProgress.Maximum = m.Duration.TotalSeconds;
            MediaProgress.Value = Math.Min(pos.TotalSeconds, m.Duration.TotalSeconds);
            MediaPos.Text = Fmt(pos);
            MediaDur.Text = Fmt(m.Duration);
        }

        // Call timer
        if (_callStart is { } cs) CallTime.Text = Fmt(now - cs);

        // Compact text: song > timer > clock
        if (m.HasSession && m.IsPlaying && !string.IsNullOrEmpty(m.Title))
            CompactText.Text = m.Title;
        else if (timerText != null)
            CompactText.Text = timerText;
        else
            CompactText.Text = now.ToString(timeFmt);

                App.Reminders.Tick();
    }

    private void SlowTick()
    {
        // Stats + battery
        var s = App.Sys.Sample();
        CpuBar.Value = s.Cpu; CpuText.Text = $"{s.Cpu:0}%";
        RamBar.Value = s.Ram; RamText.Text = $"{s.Ram:0}%";
        RamBar.ToolTip = $"{s.RamUsedGb:0.0} of {s.RamTotalGb:0.0} GB";
        if (s.Gpu is { } g) { GpuBar.Value = g; GpuText.Text = $"{g:0}%"; }
        else GpuText.Text = "n/a";
        if (s.Battery is { } b)
        {
            BatteryBar.Value = b;
            BatteryText.Text = $"{b}%";
            BatteryIcon.Text = s.Charging ? "" : "";
        }
        else
        {
            BatteryText.Text = s.Charging ? "AC" : "";
            BatteryIcon.Text = "";
        }

        // Clocks
        var now = DateTime.Now;
        BigClock.Text = now.ToString(Settings.Current.Use24Hour ? "HH:mm" : "h:mm tt");
        BigDate.Text = now.ToString("dddd, d MMMM");
        WorldList.ItemsSource = Settings.Current.WorldClocks.Select(w => new
        {
            w.Label,
            Time = WorldTime(w.TimeZoneId),
        }).ToList();

        // Call detection
        var inUse = App.Audio.IsMicInUse();
        if (inUse && _callStart == null) { _callStart = now; CallPill.Visibility = Visibility.Visible; AnimateIfCompact(); }
        else if (!inUse && _callStart != null) { _callStart = null; CallPill.Visibility = Visibility.Collapsed; AnimateIfCompact(); }

        // Fullscreen apps: get out of the way.
        var fs = Settings.Current.HideInFullscreen && Native.IsForegroundFullscreen(_hwnd);
        if (fs != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = fs;
            UpdateVisibility();
        }

        // Stay above the taskbar and other topmost windows.
        if (!fs) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        if (_mode == Mode.Expanded && TabControls.IsChecked == true) RefreshControlsQuiet();
    }

    private string WorldTime(string tzId)
    {
        try
        {
            var t = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(tzId));
            var diff = (t.Date - DateTime.Today).Days;
            var suffix = diff > 0 ? " +1" : diff < 0 ? " -1" : "";
            return t.ToString(Settings.Current.Use24Hour ? "HH:mm" : "h:mm tt") + suffix;
        }
        catch
        {
            return "?";
        }
    }

    private void AnimateIfCompact()
    {
        if (_mode == Mode.Compact) AnimateTo(CompactSize());
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void RefreshExpanded()
    {
        if (TabControls.IsChecked == true) RefreshControls();
        ApplyMedia(App.Media.State);
        FastTick();
    }

    // =====================================================================
    // Tabs
    // =====================================================================

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHome == null) return; // during InitializeComponent
        PageHome.Visibility = TabHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageClip.Visibility = TabClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageShelf.Visibility = TabShelf.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageControls.Visibility = TabControls.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageNotes.Visibility = TabNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageReminders.Visibility = TabReminders.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabControls.IsChecked == true) RefreshControls();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        App.Current.ShowSettings();
    }

    // =====================================================================
    // Media
    // =====================================================================

    /// <summary>
    /// Decides what the collapsed pill shows. While music plays the visualizer spans the whole pill;
    /// depending on the setting the title sits on top of it or is hidden.
    /// </summary>
    private void UpdateCompactLayout()
    {
        var m = App.Media.State;
        var playing = m.HasSession && m.IsPlaying;
        var style = Settings.Current.VisualizerStyle;
        var showViz = playing && style != VisualizerStyle.Off;
        var vizOnly = showViz && style == VisualizerStyle.Bars;

        Viz.Mirrored = Settings.Current.VisualizerMirrored;
        Viz.Visibility = showViz ? Visibility.Visible : Visibility.Collapsed;
        Viz.Opacity = style == VisualizerStyle.BarsWithTitle ? 0.45 : 1.0;
        Viz.Active = showViz && _mode == Mode.Compact && Island.Visibility == Visibility.Visible;
        if (!Viz.Active && App.Visualizer.Running) App.Visualizer.Stop();

        var art = playing && m.Artwork != null && !vizOnly;
        CompactArtBox.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
        CompactIcon.Visibility = art || vizOnly ? Visibility.Collapsed : Visibility.Visible;
        CompactText.Visibility = vizOnly ? Visibility.Hidden : Visibility.Visible;
    }

    private void ApplyMedia(MediaInfo m)
    {
        var playing = m.HasSession && m.IsPlaying;

        // compact
        CompactArt.ImageSource = m.Artwork;
        CompactIcon.Text = playing ? "\uE8D6" : "\uE823";
        UpdateCompactLayout();

        // expanded
        MediaApp.Text = m.HasSession ? m.AppName : "Nothing playing";
        MediaTitle.Text = m.HasSession ? m.Title : "Play something in any app";
        MediaArtist.Text = m.Artist;
        MediaArt.ImageSource = m.Artwork;
        ArtPlaceholder.Visibility = m.Artwork == null ? Visibility.Visible : Visibility.Collapsed;
        PlayPause.Content = playing ? "" : "";
        if (m.Duration <= TimeSpan.Zero)
        {
            MediaProgress.Value = 0;
            MediaPos.Text = MediaDur.Text = "";
        }

        if (_mode == Mode.Compact) AnimateTo(CompactSize());
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await App.Media.PlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await App.Media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await App.Media.PreviousAsync();

    private void Progress_Down(object sender, MouseButtonEventArgs e) => _seeking = true;

    private async void Progress_Up(object sender, MouseButtonEventArgs e)
    {
        // Let the slider move to the click point first.
        await Task.Delay(30);
        await App.Media.SeekAsync(TimeSpan.FromSeconds(MediaProgress.Value));
        _seeking = false;
    }

    // =====================================================================
    // Weather
    // =====================================================================

    private void ApplyWeather(Weather w)
    {
        var unit = Settings.Current.UseFahrenheit ? "°F" : "°C";
        WeatherIcon.Text = w.Glyph;
        WeatherTemp.Text = $"{w.Temp:0}{unit}  {w.Description}";
        WeatherDesc.Text = $"{w.Place} · H {w.High:0}° L {w.Low:0}°";
    }

    private void Weather_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (Settings.Current.WeatherLat == null) Settings_Click(sender, e);
        else _ = App.Weather.RefreshAsync();
    }

    // =====================================================================
    // Timer
    // =====================================================================

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        var min = int.Parse((string)((Button)sender).Tag);
        _timerEnd = (_timerEnd ?? DateTime.Now) + TimeSpan.FromMinutes(min);
        FastTick();
    }

    private void TimerStop_Click(object sender, RoutedEventArgs e)
    {
        _timerEnd = null;
        TimerText.Text = "00:00";
        FastTick();
    }

    // =====================================================================
    // Clipboard
    // =====================================================================

    private bool ClipFilterPredicate(object o)
    {
        if (o is not ClipItem c) return false;
        var ok = _clipFilter switch
        {
            "Pinned" => c.Pinned,
            "Text" => c.Kind == ClipKind.Text,
            "Link" => c.Kind == ClipKind.Link,
            "Image" => c.Kind == ClipKind.Image,
            "Files" => c.Kind == ClipKind.Files,
            _ => true,
        };
        var q = ClipSearch?.Text;
        if (ok && !string.IsNullOrWhiteSpace(q)) ok = c.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
        return ok;
    }

    private void RefreshClip()
    {
        CollectionViewSource.GetDefaultView(App.Clipboard.Items).Refresh();
        UpdateClipEmpty();
    }

    private void UpdateClipEmpty()
    {
        if (ClipEmpty != null) ClipEmpty.Visibility = ClipList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClipFilter_Checked(object sender, RoutedEventArgs e)
    {
        _clipFilter = (string)((RadioButton)sender).Tag;
        if (ClipList != null) RefreshClip();
    }

    private void ClipSearch_Changed(object sender, TextChangedEventArgs e) => RefreshClip();

    private async void Clip_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipItem item) return;
        e.Handled = true;
        App.Clipboard.Restore(item);
        Collapse();

        // Paste into the app that was in front before the island opened.
        if (_prevForeground != IntPtr.Zero)
        {
            Native.SetForegroundWindow(_prevForeground);
            await Task.Delay(120);
            Native.SendChord(Native.VK_CONTROL, Native.VK_V);
        }
    }

    private void ClipPin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) App.Clipboard.TogglePin(item);
        RefreshClip();
    }

    private void ClipDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) App.Clipboard.Delete(item);
        UpdateClipEmpty();
    }

    private void ClipClear_Click(object sender, RoutedEventArgs e)
    {
        App.Clipboard.ClearUnpinned();
        UpdateClipEmpty();
    }

    // =====================================================================
    // Shelf
    // =====================================================================

    private string CurrentFolder => FolderList.SelectedItem as string ?? ShelfService.DefaultFolder;

    private void RefreshShelf()
    {
        var items = App.Shelf.Items.Where(i => i.Folder == CurrentFolder).ToList();
        ShelfItems.ItemsSource = items;
        ShelfEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Folder_Changed(object sender, SelectionChangedEventArgs e) => RefreshShelf();

    private void Folder_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string f) FolderList.SelectedItem = f;
    }

    private void Folder_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string folder) return;
        if (_dragItem != null)
        {
            App.Shelf.Move(_dragItem, folder);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            App.Shelf.Add(files, folder);
        }
        FolderList.SelectedItem = folder;
        e.Handled = true;
    }

    private void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string f) App.Shelf.DeleteFolder(f);
        if (FolderList.SelectedItem == null) FolderList.SelectedIndex = 0;
        RefreshShelf();
    }

    private void ShelfNewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = App.Shelf.AddFolder($"Folder {App.Shelf.Folders.Count}");
        FolderList.SelectedItem = name;
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e) => App.Shelf.ClearFolder(CurrentFolder);

    private void Shelf_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragItem = null;
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is ShelfItem item) Open(item.Path);
    }

    private void Shelf_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item || !item.Exists) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragItem = item;
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _dragItem = null;
        }
    }

    private void Shelf_MouseUp(object sender, MouseButtonEventArgs e) => _dragItem = null;

    private static ShelfItem? MenuItemTarget(object sender) =>
        (sender as FrameworkElement)?.DataContext as ShelfItem;

    private void ShelfMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not ShelfItem item) return;
        var moveTo = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == "Move to");
        if (moveTo == null) return;
        moveTo.Items.Clear();
        foreach (var f in App.Shelf.Folders.Where(f => f != item.Folder))
        {
            var mi = new MenuItem { Header = f };
            mi.Click += (_, _) => App.Shelf.Move(item, f);
            moveTo.Items.Add(mi);
        }
        moveTo.IsEnabled = moveTo.Items.Count > 0;
    }

    private void ShelfOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) Open(item.Path);
    }

    private void ShelfReveal_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item)
        {
            try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch { }
        }
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) App.Shelf.Remove(item);
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open failed", ex); }
    }

    // =====================================================================
    // Quick controls
    // =====================================================================

    private bool _refreshingControls;

    private async void RefreshControls()
    {
        _refreshingControls = true;
        try
        {
            RefreshControlsQuiet();
            var b = App.Sys.GetBrightness();
            BrightnessRow.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
            if (b != null) { BrightnessSlider.Value = b.Value; BrightnessText.Text = $"{b}%"; }

            var p = App.Sys.GetPowerMode();
            PowerEff.IsChecked = p == SystemService.PowerBattery;
            PowerPerf.IsChecked = p == SystemService.PowerPerformance;
            PowerBal.IsChecked = PowerEff.IsChecked != true && PowerPerf.IsChecked != true;

            var wifi = await App.Sys.GetRadioAsync(RadioKind.WiFi);
            WifiTile.IsEnabled = wifi != null;
            WifiTile.IsChecked = wifi == true;
            WifiState.Text = wifi == null ? "Not found" : wifi == true ? "On" : "Off";

            var bt = await App.Sys.GetRadioAsync(RadioKind.Bluetooth);
            BtTile.IsEnabled = bt != null;
            BtTile.IsChecked = bt == true;
            BtState.Text = bt == null ? "Not found" : bt == true ? "On" : "Off";
        }
        finally
        {
            _refreshingControls = false;
        }
    }

    private void RefreshControlsQuiet()
    {
        var was = _refreshingControls;
        _refreshingControls = true;
        if (!VolumeSlider.IsMouseCaptureWithin)
        {
            var v = App.Audio.Volume * 100;
            VolumeSlider.Value = v;
            VolumeText.Text = $"{v:0}%";
        }
        SpeakerBtn.Content = App.Audio.SpeakerMuted ? "" : "";
        UpdateMicUi();
        _refreshingControls = was;
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText == null) return;
        VolumeText.Text = $"{e.NewValue:0}%";
        if (!_refreshingControls) App.Audio.Volume = e.NewValue / 100;
    }

    private void SpeakerMute_Click(object sender, RoutedEventArgs e)
    {
        App.Audio.SpeakerMuted = !App.Audio.SpeakerMuted;
        RefreshControlsQuiet();
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText != null) BrightnessText.Text = $"{e.NewValue:0}%";
    }

    private void Brightness_Commit(object sender, MouseButtonEventArgs e)
    {
        var v = (int)BrightnessSlider.Value;
        Task.Run(() => App.Sys.SetBrightness(v));
    }

    private async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        var on = WifiTile.IsChecked == true;
        if (!await App.Sys.SetRadioAsync(RadioKind.WiFi, on)) SystemService.OpenSettings("network-wifi");
        RefreshControls();
    }

    private async void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        var on = BtTile.IsChecked == true;
        if (!await App.Sys.SetRadioAsync(RadioKind.Bluetooth, on)) SystemService.OpenSettings("bluetooth");
        RefreshControls();
    }

    private void MicTile_Click(object sender, RoutedEventArgs e) => ToggleMic();

    private void NightLight_Click(object sender, RoutedEventArgs e)
    {
        ((ToggleButton)sender).IsChecked = false;
        Collapse();
        SystemService.OpenNightLight();
    }

    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        ((ToggleButton)sender).IsChecked = false;
        Collapse();
        SystemService.OpenFocus();
    }

    private void DisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        ((ToggleButton)sender).IsChecked = false;
        Collapse();
        SystemService.OpenSettings("display");
    }

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (sender == PowerEff) App.Sys.SetPowerMode(SystemService.PowerBattery);
        else if (sender == PowerPerf) App.Sys.SetPowerMode(SystemService.PowerPerformance);
        else App.Sys.SetPowerMode(SystemService.PowerBalanced);
    }

    // =====================================================================
    // Mic mute
    // =====================================================================

    private void ToggleMic()
    {
        if (!App.Audio.HasMic)
        {
            ShowPeek("", "No microphone found", "");
            return;
        }
        App.Audio.ToggleMic();
        var muted = App.Audio.MicMuted;
        if (_mode != Mode.Expanded)
            ShowPeek(muted ? "" : "", muted ? "Microphone muted" : "Microphone on", "In every app · Ctrl+Alt+M");
    }

    private void UpdateMicUi()
    {
        var muted = App.Audio.MicMuted;
        MicMutedIcon.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        MicTile.IsChecked = muted;
        MicTileIcon.Text = muted ? "" : "";
    }

    // =====================================================================
    // Notifications
    // =====================================================================

    private void NoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoteItem n) App.Notifications.Remove(n);
    }

    private void NotesClear_Click(object sender, RoutedEventArgs e) => App.Notifications.Clear();

    // =====================================================================
    // Reminders + to-do
    // =====================================================================

    private void ReminderInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ReminderAdd_Click(sender, e);
    }

    private void ReminderAdd_Click(object sender, RoutedEventArgs e)
    {
        var text = ReminderText.Text.Trim();
        if (text.Length == 0) { ReminderText.Focus(); return; }
        var when = ReminderService.ParseWhen(ReminderWhen.Text);
        if (when == null)
        {
            ReminderHint.Text = string.IsNullOrWhiteSpace(ReminderWhen.Text)
                ? "When? Try: in 20m, at 6pm, tomorrow 9am, 17:30"
                : $"Couldn't read \"{ReminderWhen.Text}\". Try: in 20m, at 6pm, tomorrow 9am";
            ReminderWhen.Focus();
            return;
        }
        var repeat = (Repeat)ReminderRepeat.SelectedIndex;
        App.Reminders.Add(text, when.Value, repeat);
        ReminderText.Clear();
        ReminderWhen.Clear();
        ReminderRepeat.SelectedIndex = 0;
        ReminderHint.Text = $"Reminder set for {when.Value:ddd h:mm tt}";
    }

    private void ReminderSnooze_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Reminder r) App.Reminders.Snooze(r, TimeSpan.FromMinutes(10));
    }

    private void ReminderDone_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Reminder r) App.Reminders.Complete(r);
    }

    private void ReminderDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Reminder r) App.Reminders.Delete(r);
    }

    private void TodoInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        App.Reminders.AddTodo(TodoInput.Text);
        TodoInput.Clear();
    }

    private void Todo_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TodoItem t) App.Reminders.ToggleTodo(t);
        e.Handled = true;
    }

    private void TodoDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TodoItem t) App.Reminders.DeleteTodo(t);
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Settings.Changed -= OnSettingsChanged;
        base.OnClosing(e);
    }
}
