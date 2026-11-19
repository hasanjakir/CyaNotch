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
using QRCoder;

namespace CyaNotch.Views;

public partial class IslandWindow : Window
{
    private enum Mode { Compact, Peek, Expanded, Strip, Hidden, Glance }

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
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(20) };

    private readonly Queue<(string icon, string title, string body)> _peeks = new();

    // Timer widget
    private DateTime? _timerEnd;

    // Call timer (mic in use)
    private DateTime? _callStart;

    // Media
    private object? _accentFor;   // artwork the current media accent was taken from

    // Shelf drag
    private Point _dragStart;
    private ShelfItem? _dragItem;

    // Clipboard filter
    private string _clipFilter = "All";

    private bool _lastMusic;

    // Lyrics
    private Lyrics? _lyrics;
    private string _lyricsKey = "";
    private string? _currentLyric;

    // Auto-hide, glance and window sync
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _glanceTimer = new();
    private readonly DispatcherTimer _glanceEnd = new() { Interval = TimeSpan.FromSeconds(6) };
    private DateTime _outsideSince = DateTime.MaxValue;
    private DateTime _audioSince = DateTime.MaxValue;
    private int _lastW, _lastH, _lastX, _lastY, _lastR = -1;
    private bool _lastSquare;
    private static readonly Random Rng = new();

    // Game strip
    private SystemStats? _lastStats;
    private double? _gpuTemp, _cpuTemp;

    public IslandWindow()
    {
        InitializeComponent();
        MediaProgress.SeekRequested += Progress_Seek;
        MediaProgress.Look = Settings.Current.ProgressStyle;
        MediaProgress.Touch = Themes.Current.Touch;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (_mode == Mode.Expanded && !Island.IsMouseOver && QrOverlay.Visibility != Visibility.Visible) Collapse(); };

        _expandDelay.Tick += (_, _) => { _expandDelay.Stop(); Expand(); };
        _collapseDelay.Tick += (_, _) =>
        {
            _collapseDelay.Stop();
            if (Island.IsMouseOver) return;
            if (QrOverlay.Visibility == Visibility.Visible) return; // phone is scanning the code
            if (IsKeyboardFocusWithin && IsActive) return; // typing in a box: wait for click-away
            Collapse();
        };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); EndPeek(); };
        _fast.Tick += (_, _) => FastTick();
        _slow.Tick += (_, _) => SlowTick();
        _weatherTimer.Tick += async (_, _) => await App.Weather.RefreshAsync();
        _hover.Tick += (_, _) => AutoHideTick();
        _glanceTimer.Tick += (_, _) => { _glanceTimer.Stop(); StartGlance(); ScheduleGlance(); };
        _glanceEnd.Tick += (_, _) => { _glanceEnd.Stop(); EndGlance(); };
        CompositionTarget.Rendering += (_, _) => SyncWindowToIsland();

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
        // Per-pixel transparency on a normal (non-layered) window, so the window region and blur work together.
        if (_source?.CompositionTarget != null) _source.CompositionTarget.BackgroundColor = Colors.Transparent;
        Native.PrepareGlassWindow(_hwnd);
        ApplyBackdrop();
        RegisterMicHotkey();
    }

    /// <summary>
    /// Lets the desktop show through on Glass/Acrylic themes. Over fullscreen games the island turns
    /// solid black instead, so it never reads the game's frames.
    /// </summary>
    private void ApplyBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;
        var t = Themes.Current;
        var solidForGame = _mode == Mode.Strip && t.SeeThrough;
        var tint = (uint)(0x30 << 24 | t.Surface.R << 16 | t.Surface.G << 8 | t.Surface.B);
        Native.SetBackdrop(_hwnd, solidForGame ? Backdrop.Solid : t.Backdrop, tint);
        Native.HideFromCapture(_hwnd, Settings.Current.HideFromCapture);
        if (solidForGame) Island.Background = Brushes.Black;
        else Island.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
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

        if (Settings.Current.DeckEnabled)
        {
            try { App.Deck.Start(); } catch (Exception ex) { Log.Error("Phone remote failed to start", ex); }
        }
        UpdateDeckUi();

        _fast.Start();
        _slow.Start();
        _hover.Start();
        App.Visualizer.Start(); // always listening (cheap) so music without a media session still counts
        ScheduleGlance();
        if (Settings.Current.AutoHide) { _mode = Mode.Hidden; Compact.Opacity = 0; Compact.Visibility = Visibility.Collapsed; AnimateTo(HiddenSize(), animate: false); }
        _weatherTimer.Start();
        FastTick();
        SlowTick();
        UpdateMicUi();

        await App.Media.StartAsync();
        await App.Notifications.StartAsync();
        NotesStatus.Text = App.Notifications.Status == "On" ? "Showing Windows notifications" : App.Notifications.Status;
        await App.Weather.RefreshAsync();
    }

    /// <summary>Screen layout changed: force the window to re-sync on the next frame.</summary>
    private void PositionWindow()
    {
        _lastW = _lastH = -1;
        SyncWindowToIsland();
    }

    /// <summary>
    /// The window is exactly the island's size, centred at the top of the primary screen, and clipped
    /// to its rounded shape. That way the glass blur only shows inside the island. Runs every frame but
    /// only touches the window when something changed.
    /// </summary>
    private void SyncWindowToIsland()
    {
        if (_hwnd == IntPtr.Zero) return;
        var src = PresentationSource.FromVisual(this);
        var scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        double wDip = double.IsNaN(Island.Width) ? 0 : Island.Width;
        double hDip = double.IsNaN(Island.Height) ? 0 : Island.Height;
        if (Island.Visibility != Visibility.Visible) hDip = 0;

        var scr = TargetScreen();
        var screen = scr.Bounds;
        var work = scr.WorkingArea;
        int w = (int)Math.Ceiling(wDip * scale);
        int h = (int)Math.Ceiling(hDip * scale);
        int top = work.Top > screen.Top ? work.Top : screen.Top; // below a top-docked taskbar
        if (!IsNotch) top += (int)Math.Round(Settings.Current.DistanceFromTop * scale);
        int x = (int)Math.Round(IslandCentreX(screen, scale) - w / 2.0);
        int r = (int)Math.Round(Island.CornerRadius.BottomLeft * scale);
        var square = IsNotch;

        if (w == _lastW && h == _lastH && x == _lastX && top == _lastY && r == _lastR && square == _lastSquare) return;
        _lastW = w; _lastH = h; _lastX = x; _lastY = top; _lastR = r; _lastSquare = square;

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, top, Math.Max(1, w), Math.Max(1, h), Native.SWP_NOACTIVATE);
        Native.SetRoundedRegion(_hwnd, h < 2 ? 0 : w, h < 2 ? 0 : h, r, square);
    }

    /// <summary>The screen chosen in Settings, or the main one.</summary>
    private static System.Windows.Forms.Screen TargetScreen()
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        var i = Settings.Current.Monitor;
        return i >= 0 && i < all.Length ? all[i] : System.Windows.Forms.Screen.PrimaryScreen!;
    }

    /// <summary>
    /// Horizontal centre of the island in screen pixels. Left/right placement keeps the resting pill's
    /// centre fixed, so it grows inward when it opens instead of off the screen.
    /// </summary>
    private double IslandCentreX(System.Drawing.Rectangle screen, double scale)
    {
        var margin = 24 * scale;
        var half = Math.Max(CompactSize().Width, double.IsNaN(Island.Width) ? 0 : Island.Width) * scale / 2;
        return Settings.Current.Side switch
        {
            IslandSide.Left => screen.Left + margin + half,
            IslandSide.Right => screen.Right - margin - half,
            _ => screen.Left + screen.Width / 2.0,
        };
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
            ApplyBackdrop();
            MediaProgress.Look = Settings.Current.ProgressStyle;
            MediaProgress.Touch = Themes.Current.Touch;
            RegisterMicHotkey();
            ScheduleGlance();
            ApplyShape(animate: true);
            UpdateCompactLayout();
            _lyricsKey = "";
            LoadLyrics(App.Media.State);
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
        if (MusicPlaying) w += Settings.Current.VisualizerStyle switch { VisualizerStyle.Off => 70, VisualizerStyle.Dots => 40, _ => 130 };
        if (_callStart != null) w += 40;
        if (_timerEnd != null) w += 30;
        return new Size(w, h);
    }

    private static Size PeekSize() => new(400, 64);
    /// <summary>The music tab is a compact card; the other tabs need more room.</summary>
    private Size ExpandedSize() => TabHome?.IsChecked == true ? new Size(600, 272) : new Size(700, 430);
    private static Size StripSize() => new(440, 24);
    private static Size GlanceSize() => new(470, 34);
    private Size HiddenSize() => new(Settings.Current.CollapsedWidth * 0.6, 0);

    private void ApplyShape(bool animate)
    {
        Island.Margin = new Thickness(0);
        Island.BorderThickness = IsNotch ? new Thickness(1, 0, 1, 1) : new Thickness(1);
        var size = _mode switch
        {
            Mode.Expanded => ExpandedSize(),
            Mode.Peek => PeekSize(),
            Mode.Strip => StripSize(),
            Mode.Glance => GlanceSize(),
            Mode.Hidden => HiddenSize(),
            _ => CompactSize(),
        };
        AnimateTo(size, animate);
    }

    private void AnimateTo(Size size, bool animate = true, bool bouncy = true)
    {
        var round = Math.Clamp(Settings.Current.Roundness, 0, 1);
        var radius = _mode switch
        {
            Mode.Expanded => 6 + 22 * round,
            Mode.Peek => 6 + 20 * round,
            _ => Math.Max(4, size.Height / 2 * round),
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
        if (tab == null && _mode != Mode.Expanded)
        {
            // Reopen where the user left off, or always on music.
            var name = Settings.Current.OpenOn == "Music" ? "TabHome" : Settings.Current.LastTab;
            tab = FindName(name) as RadioButton ?? TabHome;
        }
        if (tab != null) tab.IsChecked = true;
        if (_mode == Mode.Expanded) return;

        var fg = Native.GetForegroundWindow();
        if (fg != _hwnd) _prevForeground = fg;

        _peekTimer.Stop();
        if (_mode == Mode.Glance)
        {
            _glanceEnd.Stop();
            Strip.Visibility = Visibility.Collapsed;
        }
        if (_mode == Mode.Strip)
        {
            App.Game.StopTracking();
            Strip.Visibility = Visibility.Collapsed;
        }
        _mode = Mode.Expanded;
        ApplyBackdrop();
        UpdateVisibility();
        Fade(Compact, false, 90 * Speed);
        Fade(Peek, false, 90 * Speed);
        Fade(Expanded, true, 220 * Speed, 120 * Speed);
        AnimateTo(ExpandedSize());
        RefreshExpanded();
        UpdateCompactLayout();
    }

    private void Collapse()
    {
        if (_mode != Mode.Expanded) return;
        Settings.SaveQuietly(); // remembers the last tab across restarts
        _mode = Mode.Compact;
        QrOverlay.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
        Fade(Expanded, false, 90 * Speed);
        Fade(Compact, true, 200 * Speed, 160 * Speed);
        AnimateTo(CompactSize(), bouncy: false);
        UpdateVisibility();
        UpdateCompactLayout();
        if (_peeks.Count > 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(NextPeek));
    }

    private void ShowPeek(string icon, string title, string body)
    {
        _peeks.Enqueue((icon, title, body));
        if (_mode is Mode.Compact or Mode.Hidden) NextPeek();
    }

    // Notifications that arrive together stack; the mouse wheel flips through them.
    private readonly List<(string icon, string title, string body)> _peekBurst = new();
    private int _peekIndex = -1;

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode != Mode.Peek) return;
        e.Handled = true;
        if (e.Delta < 0)
        {
            if (_peekIndex < _peekBurst.Count - 1) ShowPeekAt(_peekIndex + 1);
            else if (_peeks.Count > 0) NextPeek();
        }
        else if (_peekIndex > 0)
        {
            ShowPeekAt(_peekIndex - 1);
        }
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    private void ShowPeekAt(int i)
    {
        _peekIndex = i;
        var (icon, title, body) = _peekBurst[i];
        PeekIcon.Text = icon;
        PeekTitle.Text = title;
        PeekBody.Text = body;
        PeekBody.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        var total = _peekBurst.Count + _peeks.Count;
        PeekCount.Text = total > 1 ? $"{i + 1}/{total}" : "";
        PeekCount.Visibility = total > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NextPeek()
    {
        if (_mode == Mode.Expanded || _peeks.Count == 0) return;
        var (icon, title, body) = _peeks.Dequeue();
        _peekBurst.Add((icon, title, body));
        ShowPeekAt(_peekBurst.Count - 1);
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
        _peekTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.Current.PeekSeconds, 2, 30));
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
        _peekBurst.Clear();
        _peekIndex = -1;
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
        if (_mode != Mode.Expanded && _mode != Mode.Strip)
        {
            _expandDelay.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.Current.OpenDelayMs, 0, 1500));
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
    // Auto-hide: hidden until the mouse touches the top edge
    // =====================================================================

    /// <summary>Music is audible: the media session says playing, or sound has been coming out for a moment.</summary>
    private bool MusicPlaying
    {
        get
        {
            var m = App.Media.State;
            if (m.HasSession && m.IsPlaying) return true;
            if (App.Visualizer.HasSound)
            {
                if (_audioSince == DateTime.MaxValue) _audioSince = DateTime.Now;
                return (DateTime.Now - _audioSince).TotalSeconds > 1.5;
            }
            _audioSince = DateTime.MaxValue;
            return false;
        }
    }

    private bool StickyVisible => Settings.Current.ShowWhilePlaying && MusicPlaying;

    private void AutoHideTick()
    {
        if (!Settings.Current.AutoHide)
        {
            if (_mode == Mode.Hidden) Reveal();
            return;
        }
        if (_mode is not (Mode.Compact or Mode.Hidden)) { _outsideSince = DateTime.MaxValue; return; }

        Native.GetCursorPos(out var pt);
        var screen = TargetScreen().Bounds;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var half = Math.Max(160, CompactSize().Width / 2 + 60) * scale;
        var cx = IslandCentreX(screen, scale);
        var hot = pt.Y <= screen.Top + 3 && Math.Abs(pt.X - cx) <= half;
        var near = pt.Y <= screen.Top + (Settings.Current.CollapsedHeight + 24 + (IsNotch ? 0 : Settings.Current.DistanceFromTop)) * scale
                   && Math.Abs(pt.X - cx) <= half;

        if (_mode == Mode.Hidden)
        {
            if (hot || StickyVisible) Reveal();
            return;
        }

        // Compact: stay while the mouse is on or near the pill, or music keeps it out.
        if (near || Island.IsMouseOver || StickyVisible)
        {
            _outsideSince = DateTime.MaxValue;
            return;
        }
        if (_outsideSince == DateTime.MaxValue) _outsideSince = DateTime.Now;
        if ((DateTime.Now - _outsideSince).TotalMilliseconds > 450) HideIsland();
    }

    private void HideIsland()
    {
        if (_mode != Mode.Compact) return;
        _mode = Mode.Hidden;
        _outsideSince = DateTime.MaxValue;
        Fade(Compact, false, 90 * Speed);
        AnimateTo(HiddenSize(), bouncy: false);
        UpdateCompactLayout();
    }

    private void Reveal()
    {
        if (_mode != Mode.Hidden) return;
        _mode = Mode.Compact;
        Fade(Compact, true, 200 * Speed, 120 * Speed);
        AnimateTo(CompactSize());
        UpdateCompactLayout();
        if (_peeks.Count > 0) NextPeek();
    }

    // =====================================================================
    // Stats glance: pops up now and then with time, CPU, GPU, RAM, temperature
    // =====================================================================

    private void ScheduleGlance()
    {
        _glanceTimer.Stop();
        if (!Settings.Current.StatsGlance) return;
        var lo = Math.Max(1, Settings.Current.GlanceMinMinutes);
        var hi = Math.Max(lo, Settings.Current.GlanceMaxMinutes);
        _glanceTimer.Interval = TimeSpan.FromSeconds(Rng.Next(lo * 60, hi * 60 + 1));
        _glanceTimer.Start();
    }

    private void StartGlance()
    {
        if (_mode is not (Mode.Compact or Mode.Hidden) || _hiddenByUser || _hiddenForFullscreen) return;
        _mode = Mode.Glance;
        _gpuTemp = App.Game.GpuTemp();
        _cpuTemp = App.Game.CpuTemp();
        _lastStats ??= App.Sys.Sample();
        UpdateStrip();
        Fade(Compact, false, 80 * Speed);
        Fade(Strip, true, 180 * Speed, 120 * Speed);
        AnimateTo(GlanceSize());
        UpdateCompactLayout();
        _glanceEnd.Start();
    }

    private void EndGlance()
    {
        if (_mode != Mode.Glance) return;
        _mode = Mode.Compact;
        Fade(Strip, false, 80 * Speed);
        Fade(Compact, true, 180 * Speed, 100 * Speed);
        AnimateTo(CompactSize(), bouncy: false);
        UpdateCompactLayout();
        _outsideSince = DateTime.MinValue; // let auto-hide tuck it away right after
        if (_peeks.Count > 0) NextPeek();
    }

    // =====================================================================
    // Game strip
    // =====================================================================

    private void EnterStrip()
    {
        _mode = Mode.Strip;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        App.Game.Track((int)pid);
        ApplyBackdrop();
        Fade(Compact, false, 80 * Speed);
        Fade(Strip, true, 160 * Speed, 100 * Speed);
        AnimateTo(StripSize(), bouncy: false);
        UpdateCompactLayout();
        UpdateStrip();
    }

    private void ExitStrip()
    {
        App.Game.StopTracking();
        _mode = Mode.Compact;
        ApplyBackdrop();
        Fade(Strip, false, 80 * Speed);
        Fade(Compact, true, 160 * Speed, 100 * Speed);
        AnimateTo(CompactSize(), bouncy: false);
        UpdateCompactLayout();
        if (_peeks.Count > 0) NextPeek();
    }

    private void UpdateStrip()
    {
        StripTime.Text = DateTime.Now.ToString(Settings.Current.Use24Hour ? "HH:mm" : "h:mm tt");
        var fps = App.Game.SampleFps();
        StripFps.Text = fps is { } f ? $"{f:0} FPS" : "";
        StripFps.Visibility = fps == null ? Visibility.Collapsed : Visibility.Visible;
        var st = _lastStats;
        if (st == null) return;
        StripCpu.Text = _cpuTemp is { } ct ? $"CPU {st.Cpu:0}% {ct:0}°" : $"CPU {st.Cpu:0}%";
        StripGpu.Text = st.Gpu is { } g
            ? (_gpuTemp is { } gt ? $"GPU {g:0}% {gt:0}°" : $"GPU {g:0}%")
            : (_gpuTemp is { } gt2 ? $"GPU {gt2:0}°" : "");
        StripGpu.Visibility = StripGpu.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StripRam.Text = $"RAM {st.Ram:0}%";
    }

    // =====================================================================
    // Lyrics
    // =====================================================================

    private async void LoadLyrics(MediaInfo m)
    {
        if (!Settings.Current.LyricsEnabled || !m.HasSession || string.IsNullOrEmpty(m.Title))
        {
            _lyrics = null;
            _lyricsKey = "";
            UpdateLyrics(m);
            return;
        }
        var key = $"{m.Artist}|{m.Title}";
        if (key == _lyricsKey) return;
        _lyricsKey = key;
        _lyrics = null;
        UpdateLyrics(m);
        var result = await App.Lyrics.GetAsync(m.Title, m.Artist, m.Duration);
        if (_lyricsKey != key) return; // song changed while loading
        _lyrics = result;
        UpdateLyrics(App.Media.State);
    }

    private void UpdateLyrics(MediaInfo m)
    {
        var l = _lyrics;
        if (l == null || !l.Synced || !Settings.Current.LyricsEnabled)
        {
            if (LyricsBox.Visibility == Visibility.Visible) { LyricsBox.Visibility = Visibility.Collapsed; UpdateCardViz(); }
            _currentLyric = null;
            return;
        }
        // Small lead so the line appears as it is sung, not after.
        var i = l.IndexAt(m.LivePosition + TimeSpan.FromMilliseconds(250));
        string Line(int k) => k >= 0 && k < l.Lines.Count ? l.Lines[k].Text : "";
        _currentLyric = i >= 0 ? Line(i) : null;
        LyricPrev.Text = Line(i - 1);
        LyricNow.Text = i >= 0 ? (Line(i).Length == 0 ? "♪" : Line(i)) : "♪";
        LyricNext.Text = Line(i + 1);
        if (LyricsBox.Visibility != Visibility.Visible) { LyricsBox.Visibility = Visibility.Visible; UpdateCardViz(); }
    }

    // =====================================================================
    // Phone remote
    // =====================================================================

    private void DeckToggle_Click(object sender, RoutedEventArgs e)
    {
        if (App.Deck.Active)
        {
            App.Deck.Stop();
            Settings.Current.DeckEnabled = false;
            QrOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            var url = App.Deck.Start();
            Settings.Current.DeckEnabled = true;
            ShowQr(url);
        }
        Settings.Save();
        UpdateDeckUi();
    }

    private void DeckShow_Click(object sender, RoutedEventArgs e)
    {
        if (App.Deck.Url is { } url) ShowQr(url);
    }

    private void ShowQr(string url)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(12);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            QrImage.Source = bmp;
        }
        catch (Exception ex)
        {
            Log.Error("QR code failed", ex);
        }
        QrUrl.Text = url;
        QrOverlay.Visibility = Visibility.Visible;
    }

    private void QrClose_Click(object sender, RoutedEventArgs e) => QrOverlay.Visibility = Visibility.Collapsed;

    private void UpdateDeckUi()
    {
        var on = App.Deck.Active;
        DeckToggle.Content = on ? "Turn off" : "Turn on";
        DeckShow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DeckState.Text = on
            ? $"On · {Settings.Current.ApprovedPhones.Count} approved phone(s)"
            : "Use your phone as a trackpad and remote";
    }

    // =====================================================================
    // Desktop widgets
    // =====================================================================

    private void PinWidget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is string kind) WidgetWindow.PinNew(kind);
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
        if (_mode == Mode.Expanded && m.HasSession && !MediaProgress.IsDragging && m.Duration > TimeSpan.Zero)
        {
            var pos = m.LivePosition > m.Duration ? m.Duration : m.LivePosition;
            MediaProgress.Value = pos.TotalSeconds / m.Duration.TotalSeconds;
            MediaPos.Text = Fmt(pos);
            MediaDur.Text = "-" + Fmt(m.Duration - pos);
        }
        if (_mode == Mode.Expanded && !CardVolume.IsMouseCaptureWithin)
        {
            _syncingCardVolume = true;
            CardVolume.Value = App.Audio.Volume * 100;
            _syncingCardVolume = false;
        }

        // Call timer
        if (_callStart is { } cs) CallTime.Text = Fmt(now - cs);

        if (_mode is Mode.Strip or Mode.Glance) UpdateStrip();

        // Music started or stopped without a media-session event (players that don't report to Windows).
        var music = MusicPlaying;
        if (music != _lastMusic)
        {
            _lastMusic = music;
            UpdateCompactLayout();
            AnimateIfCompact();
        }
        if (_lyrics != null) UpdateLyrics(m);

        // Compact text: song > timer > clock
        if (m.HasSession && m.IsPlaying && Settings.Current.LyricsInPill && !string.IsNullOrEmpty(_currentLyric))
            CompactText.Text = _currentLyric;
        else if (m.HasSession && m.IsPlaying && !string.IsNullOrEmpty(m.Title))
            CompactText.Text = ShortTitle(m.Title, m.Artist);
        else if (timerText != null)
            CompactText.Text = timerText;
        else
            CompactText.Text = RestingText(now, timeFmt);

                App.Reminders.Tick();
    }

    /// <summary>What the pill shows at rest, as chosen in Settings.</summary>
    private string RestingText(DateTime now, string timeFmt)
    {
        var st = _lastStats;
        switch (Settings.Current.RestingInfo)
        {
            case RestingInfo.Date:
                return now.ToString("ddd d MMM");
            case RestingInfo.Weather when App.Weather.Current is { } w:
                return $"{w.Temp:0}°  {w.Description}";
            case RestingInfo.Battery when st?.Battery is { } b:
                return st.Charging ? $"{b}% ⚡" : $"{b}%";
            case RestingInfo.Cpu when st != null:
                return $"CPU {st.Cpu:0}%";
            case RestingInfo.Temperature when (_cpuTemp ?? _gpuTemp) is { } t:
                return $"{t:0}°C";
            default:
                return now.ToString(timeFmt);
        }
    }

    private void SlowTick()
    {
        // Stats + battery
        var s = App.Sys.Sample();
        _lastStats = s;
        if (Settings.Current.RestingInfo == RestingInfo.Temperature && _mode is Mode.Compact or Mode.Hidden)
        {
            _gpuTemp = App.Game.GpuTemp();
            _cpuTemp ??= App.Game.CpuTemp();
        }
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
        _lastStats = s;
        var fs = Native.IsForegroundFullscreen(_hwnd);
        var fsMode = Settings.Current.FullscreenMode;
        var wantStrip = fs && fsMode == GameMode.Strip;
        var wantHide = fs && fsMode == GameMode.Hide;
        if (wantStrip && _mode is Mode.Compact or Mode.Hidden) EnterStrip();
        else if (!wantStrip && _mode == Mode.Strip) ExitStrip();
        if (wantHide != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = wantHide;
            UpdateVisibility();
        }
        if (!App.Visualizer.Running) App.Visualizer.Start();
        if (_mode is Mode.Strip or Mode.Glance)
        {
            _gpuTemp = App.Game.GpuTemp();
            _cpuTemp = App.Game.CpuTemp();
            UpdateStrip();
        }

        // Stay above the taskbar, other topmost windows and borderless games.
        if (!wantHide) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

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
        PageWidgets.Visibility = TabWidgets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == Mode.Expanded) AnimateTo(ExpandedSize(), bouncy: false);
        foreach (var t in new[] { TabHome, TabWidgets, TabClip, TabShelf, TabControls, TabNotes, TabReminders })
            if (t.IsChecked == true) { Settings.Current.LastTab = t.Name; break; }
        PageClip.Visibility = TabClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageShelf.Visibility = TabShelf.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageControls.Visibility = TabControls.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageNotes.Visibility = TabNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageReminders.Visibility = TabReminders.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabControls.IsChecked == true) { RefreshControls(); UpdateDeckUi(); }
        UpdateCardViz();
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
    private void CompactText_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVizGap();

    private void UpdateVizGap()
    {
        var titled = Settings.Current.VisualizerStyle == VisualizerStyle.BarsWithTitle && CompactText.Visibility == Visibility.Visible;
        Viz.GapWidth = titled && CompactText.ActualWidth > 0 ? CompactText.ActualWidth + 16 : 0;
    }

    /// <summary>
    /// "Imagine Dragons - Wrecked (Official Music Video)" becomes "Wrecked": drops video tags,
    /// and the artist prefix that video sites put in front of the song name.
    /// </summary>
    internal static string ShortTitle(string title, string artist)
    {
        var t = title;
        foreach (var marker in new[] { " (Official", " [Official", " (Lyric", " [Lyric", " (Audio", " [Audio", " (Music Video", " (Visualizer", " (Video", " | ", " ft.", " feat." })
        {
            var i = t.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i > 0) t = t[..i];
        }
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var before = Squash(t[..dash]);
            var a = Squash((artist ?? "").Replace("VEVO", "", StringComparison.OrdinalIgnoreCase).Replace("- Topic", ""));
            // Browsers report the channel as the artist; drop the prefix when it names the same act (or there's no artist).
            if (before.Length > 0 && (a.Length == 0 || a.Contains(before) || before.Contains(a)))
                t = t[(dash + 3)..];
        }
        t = t.Trim();
        return t.Length == 0 ? title : t;
    }

    /// <summary>
    /// Soft blur of the artwork behind the island while music plays. Only on dark themes,
    /// where it reads as coloured glass; light themes keep their own surface.
    /// </summary>
    private void UpdateAmbient()
    {
        var m = App.Media.State;
        var t = Themes.Current.Text;
        var darkTheme = t.R + t.G + t.B > 3 * 128;
        var show = Settings.Current.AmbientArtwork && darkTheme && MusicPlaying && m.HasSession && m.Artwork != null
                   && _mode is Mode.Compact or Mode.Expanded or Mode.Peek;
        if (show == (Ambient.Visibility == Visibility.Visible)) return;
        if (show)
        {
            Ambient.Visibility = Visibility.Visible;
            Ambient.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)));
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => { if (Ambient.Opacity < 0.01) Ambient.Visibility = Visibility.Collapsed; };
            Ambient.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>The big visualizer in the music card: shown while music plays and no lyrics take the space.</summary>
    private void UpdateCardViz()
    {
        var show = MusicPlaying && LyricsBox.Visibility != Visibility.Visible;
        CardViz.Mirrored = Settings.Current.VisualizerMirrored;
        CardViz.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CardViz.Active = show && _mode == Mode.Expanded && TabHome.IsChecked == true && Island.Visibility == Visibility.Visible;
    }

    private void UpdateCompactLayout()
    {
        var m = App.Media.State;
        var playing = MusicPlaying;
        var style = Settings.Current.VisualizerStyle;
        var dots = playing && style == VisualizerStyle.Dots;
        var showViz = playing && style is VisualizerStyle.Bars or VisualizerStyle.BarsWithTitle;
        var vizOnly = showViz && style == VisualizerStyle.Bars;
        var compactVisible = _mode == Mode.Compact && Island.Visibility == Visibility.Visible;

        Viz.Mirrored = Settings.Current.VisualizerMirrored;
        Viz.Visibility = showViz ? Visibility.Visible : Visibility.Collapsed;
        Viz.Opacity = style == VisualizerStyle.BarsWithTitle ? 0.85 : 1.0;
        // With a title, it stays small in the middle and the bars run on both sides of it.
        var titled = style == VisualizerStyle.BarsWithTitle && playing;
        CompactText.FontSize = titled ? 11.5 : 12.5;
        CompactText.MaxWidth = titled ? Math.Max(60, (CompactSize().Width - 20) * 0.42) : double.PositiveInfinity;
        UpdateVizGap();
        Viz.Active = showViz && compactVisible;
        DotViz.Visibility = dots ? Visibility.Visible : Visibility.Collapsed;
        DotViz.Active = dots && compactVisible;
        UpdateCardViz();
        UpdateAmbient();

        var art = playing && m.HasSession && m.Artwork != null && !vizOnly;
        CompactArtBox.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
        CompactIcon.Visibility = art || vizOnly ? Visibility.Collapsed : Visibility.Visible;
        // Dot style keeps the pill clean: artwork on the left, dots on the right, nothing in between.
        CompactText.Visibility = vizOnly || (dots && art) ? Visibility.Hidden : Visibility.Visible;
    }

    private void ApplyMedia(MediaInfo m)
    {
        var playing = m.HasSession && m.IsPlaying;

        // compact
        CompactArt.ImageSource = m.Artwork;
        CompactIcon.Text = playing ? "\uE8D6" : "\uE823";
        UpdateCompactLayout();

        LoadLyrics(m);

        // expanded
        MediaApp.Text = m.HasSession ? m.AppName : "Nothing playing";
        MediaTitle.Text = m.HasSession ? m.Title : "Play something in any app";
        MediaArtist.Text = m.Artist;
        MediaArt.ImageSource = m.Artwork;
        var appIcon = m.HasSession ? AppIcons.ForMediaApp(m.AppId) : null;
        AppBadge.Visibility = m.HasSession ? Visibility.Visible : Visibility.Collapsed;
        AppBadgeIcon.Source = appIcon;
        AppBadgeGlyph.Visibility = appIcon == null ? Visibility.Visible : Visibility.Collapsed;
        AppBadge.Background = appIcon == null ? (Brush)FindResource("MediaAccentBrush") : new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x12));
        LyricsToggle.IsChecked = Settings.Current.LyricsEnabled;
        ArtPlaceholder.Visibility = m.Artwork == null ? Visibility.Visible : Visibility.Collapsed;
        PlayPause.Content = playing ? "" : "";
        MediaProgress.Playing = playing;
        if (m.Duration <= TimeSpan.Zero)
        {
            MediaProgress.Value = 0;
            MediaPos.Text = MediaDur.Text = "";
        }

        // Colours from the artwork
        AmbientArt.ImageSource = m.Artwork;
        if (!ReferenceEquals(_accentFor, m.Artwork))
        {
            _accentFor = m.Artwork;
            Themes.ApplyMediaAccent(m.HasSession ? ArtColors.Accent(m.Artwork) : null);
        }

        if (_mode == Mode.Compact) AnimateTo(CompactSize());
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await App.Media.PlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await App.Media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await App.Media.PreviousAsync();


    private void LyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        // Turning lyrics on here is the opt-in: songs are looked up on LRCLIB only after this.
        Settings.Current.LyricsEnabled = LyricsToggle.IsChecked == true;
        Settings.Save();
    }

    private async void Progress_Seek(double fraction)
    {
        var m = App.Media.State;
        if (m.Duration > TimeSpan.Zero) await App.Media.SeekAsync(TimeSpan.FromSeconds(fraction * m.Duration.TotalSeconds));
    }

    private bool _syncingCardVolume;

    private void CardVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingCardVolume || !IsLoaded) return;
        App.Audio.Volume = e.NewValue / 100;
    }

    // =====================================================================
    // Weather
    // =====================================================================

    private void ApplyWeather(Weather w)
    {
        var unit = Settings.Current.UseFahrenheit ? "°F" : "°C";
        WeatherIcon.Text = w.Glyph;
        WeatherTemp.Text = $"{w.Temp:0}{unit}  {w.Description}";
        WeatherDesc.Text = $"H {w.High:0}°  ·  L {w.Low:0}°";
        WeatherPlace.Text = w.Place;
        WeatherPlace.Visibility = Visibility.Visible;
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
