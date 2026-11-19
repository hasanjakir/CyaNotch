using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CyaNotch.Core;
using CyaNotch.Services;

namespace CyaNotch.Views;

/// <summary>
/// CyaNotch's taskbar: a notch along the bottom of the main screen with Start, pinned and open apps,
/// what's playing, the tray, quick settings and the clock. Windows' own Start, Quick Settings and
/// notification panels open from it. Steps aside for fullscreen games.
/// </summary>
public sealed class TaskbarWindow : Window
{
    private const double BarHeight = 50;
    private const double Gap = 6; // space under the notch's edge is reserved too, so windows don't touch it

    private readonly StackPanel _apps = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _media = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _date = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Border _bar;
    private IntPtr _hwnd;
    private string _appsKey = "";
    private string _mediaKey = "";
    private DispatcherTimer? _trayPeek;

    private static TaskbarWindow? _current;

    /// <summary>Turns the bar on or off to match Settings. Off always gives Windows' taskbar back.</summary>
    public static void Apply()
    {
        if (Settings.Current.TaskbarEnabled)
        {
            if (_current == null) { _current = new TaskbarWindow(); _current.Show(); }
            if (Settings.Current.TaskbarHideWindows) WindowsTaskbar.HideWindows(); else WindowsTaskbar.RestoreWindows();
        }
        else
        {
            _current?.Close();
            _current = null;
            WindowsTaskbar.RestoreWindows();
        }
    }

    private TaskbarWindow()
    {
        Title = "CyaNotch taskbar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        TextElement.SetFontFamily(this, (FontFamily)FindResource("UiFont"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 14, 0) };
        row.Children.Add(StartButton());
        row.Children.Add(Divider());
        row.Children.Add(_apps);
        row.Children.Add(_media);
        row.Children.Add(Divider());
        row.Children.Add(Glyph("", "Show hidden icons", TrayPeek));
        row.Children.Add(QuickSettings());
        var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        clock.Children.Add(_time);
        clock.Children.Add(_date);
        _date.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        row.Children.Add(Clickable(clock, "Notifications and calendar", () => Native.SendChord(Native.VK_LWIN, 0x4E /* N */)));

        _bar = new Border
        {
            Height = BarHeight,
            CornerRadius = new CornerRadius(18, 18, 0, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = row,
        };
        ApplyTheme();
        Content = _bar;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            WindowsTaskbar.Register(_hwnd);
            Place();
        };
        Settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            Settings.Changed -= OnSettingsChanged;
            _tick.Stop();
            if (_hwnd != IntPtr.Zero) WindowsTaskbar.Unregister(_hwnd);
        };
        _tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _tick.Start(); };
        SystemEvents_DisplayChanged();
    }

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(ApplyTheme);

    private void ApplyTheme()
    {
        var t = Themes.Current;
        // The bar isn't blurred, so see-through themes get a nearly solid version of their colour.
        var c = t.SeeThrough ? Color.FromArgb(0xEE, t.Surface.R, t.Surface.G, t.Surface.B) : t.Surface;
        _bar.Background = new SolidColorBrush(c);
        _bar.SetResourceReference(Border.BorderBrushProperty, "SurfaceEdgeBrush");
    }

    private void SystemEvents_DisplayChanged() =>
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Place);

    /// <summary>Covers the bottom of the main screen and reserves that space for the bar.</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var h = (int)Math.Round((BarHeight + Gap) * scale);
        var rc = WindowsTaskbar.Reserve(_hwnd,
            new Native.RECT { Left = screen.Left, Top = screen.Top, Right = screen.Right, Bottom = screen.Bottom }, h);
        // Window spans the full width (transparent sides); the notch sits centred at the bottom.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rc.Left, rc.Top, rc.Width, rc.Height, Native.SWP_NOACTIVATE);
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString(Settings.Current.Use24Hour ? "HH:mm" : "h:mm tt");
        _date.Text = now.ToString("ddd d MMM");

        // Step aside for fullscreen games and videos; come back when Start or another app is in front.
        var full = Native.IsForegroundFullscreen(_hwnd);
        var want = full ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != want) Visibility = want;
        if (full) return;

        RefreshApps();
        RefreshMedia();
    }

    // ---------------------------------------------------------------- apps

    private sealed record AppEntry(string Key, string Title, string? Exe, IntPtr Window, string? Shortcut, ImageSource? Icon);

    private void RefreshApps()
    {
        var windows = OpenWindows();
        var pinned = PinnedShortcuts();
        var entries = new List<AppEntry>();
        var used = new HashSet<IntPtr>();
        foreach (var (lnk, target) in pinned)
        {
            var w = windows.FirstOrDefault(x => x.exe != null && target != null && string.Equals(x.exe, target, StringComparison.OrdinalIgnoreCase));
            if (w.hwnd != IntPtr.Zero) used.Add(w.hwnd);
            entries.Add(new AppEntry("p:" + lnk + "|" + w.hwnd, System.IO.Path.GetFileNameWithoutExtension(lnk), target, w.hwnd, lnk, null));
        }
        foreach (var w in windows.Where(x => !used.Contains(x.hwnd)))
            entries.Add(new AppEntry("w:" + w.hwnd + "|" + w.title, w.title, w.exe, w.hwnd, null, null));

        var fg = Native.GetForegroundWindow();
        var key = string.Join(";", entries.Select(e => e.Key)) + "#" + fg;
        if (key == _appsKey) return;
        _appsKey = key;

        _apps.Children.Clear();
        foreach (var e in entries)
        {
            var icon = IconFor(e);
            var running = e.Window != IntPtr.Zero;
            var active = running && e.Window == fg;
            var stack = new Grid { Width = 40, Height = 40 };
            stack.Children.Add(icon != null
                ? new Image { Source = icon, Width = 24, Height = 24 }
                : new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var dot = new Border
            {
                Width = active ? 14 : 5, Height = 3, CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2),
                Visibility = running ? Visibility.Visible : Visibility.Hidden,
            };
            dot.SetResourceReference(Border.BackgroundProperty, active ? "AccentBrush" : "SubTextBrush");
            stack.Children.Add(dot);
            var btn = Clickable(stack, e.Title, () => Activate(e));
            if (active) btn.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            _apps.Children.Add(btn);
        }
    }

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? IconFor(AppEntry e)
    {
        var path = e.Exe ?? e.Shortcut;
        if (path == null) return WindowIcon(e.Window);
        if (IconCache.TryGetValue(path, out var hit)) return hit;
        ImageSource? icon = null;
        try { icon = AppIcons.FromFile(e.Shortcut ?? path); } catch { }
        icon ??= WindowIcon(e.Window);
        IconCache[path] = icon;
        return icon;
    }

    private static void Activate(AppEntry e)
    {
        if (e.Window == IntPtr.Zero)
        {
            try { Process.Start(new ProcessStartInfo(e.Shortcut ?? e.Exe!) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Taskbar launch failed", ex); }
            return;
        }
        if (Native.GetForegroundWindow() == e.Window) { ShowWindow(e.Window, 6 /* SW_MINIMIZE */); return; }
        if (IsIconic(e.Window)) ShowWindow(e.Window, 9 /* SW_RESTORE */);
        Native.SetForegroundWindow(e.Window);
    }

    // ---------------------------------------------------------------- media + tray

    private void RefreshMedia()
    {
        var m = App.Media.State;
        var key = m.HasSession && m.IsPlaying ? m.Title + "|" + m.Artist : "";
        if (key == _mediaKey) return;
        _mediaKey = key;
        _media.Children.Clear();
        if (key.Length == 0) return;
        _media.Children.Add(Divider());
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (m.Artwork != null)
            row.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = new ImageBrush(m.Artwork) { Stretch = Stretch.UniformToFill } });
        row.Children.Add(new TextBlock
        {
            Text = IslandWindow.ShortTitle(m.Title, m.Artist),
            MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0), FontSize = 12.5,
        });
        _media.Children.Add(Clickable(row, "Play / pause", () => _ = App.Media.PlayPauseAsync()));
    }

    /// <summary>Shows Windows' own tray for a few seconds (its icons can't be moved into another bar).</summary>
    private void TrayPeek()
    {
        WindowsTaskbar.PeekWindows(true);
        Native.SendChord(Native.VK_LWIN, 0x42 /* B: focus the tray */);
        _trayPeek?.Stop();
        _trayPeek = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _trayPeek.Tick += (_, _) =>
        {
            _trayPeek!.Stop();
            if (Settings.Current.TaskbarEnabled && Settings.Current.TaskbarHideWindows) WindowsTaskbar.PeekWindows(false);
        };
        _trayPeek.Start();
    }

    // ---------------------------------------------------------------- pieces

    private UIElement StartButton()
    {
        var logo = new UniformGrid { Rows = 2, Columns = 2, Width = 18, Height = 18 };
        for (int i = 0; i < 4; i++)
        {
            var sq = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(1) };
            sq.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            logo.Children.Add(sq);
        }
        var g = new Grid { Width = 40, Height = 40 };
        g.Children.Add(logo);
        return Clickable(g, "Start", () => Native.Tap(Native.VK_LWIN));
    }

    private UIElement QuickSettings()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
        foreach (var gl in new[] { "", "", "" })
            row.Children.Add(new TextBlock { Text = gl, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        return Clickable(new Grid { Height = 40, Children = { row } }, "Quick settings", () => Native.SendChord(Native.VK_LWIN, 0x41 /* A */));
    }

    private UIElement Glyph(string glyph, string tip, Action click) =>
        Clickable(new Grid { Width = 32, Height = 40, Children = { new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } } }, tip, click);

    private static UIElement Divider()
    {
        var r = new Rectangle { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(Shape.FillProperty, "TrackBrush");
        return r;
    }

    private static Border Clickable(FrameworkElement content, string tip, Action click)
    {
        var b = new Border { Child = content, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    // ---------------------------------------------------------------- Win32

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    /// <summary>Top-level app windows, like the ones Windows' taskbar shows.</summary>
    private List<(IntPtr hwnd, string title, string? exe)> OpenWindows()
    {
        var list = new List<(IntPtr, string, string?)>();
        var myPid = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            var ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var sb = new StringBuilder(256);
            if (GetWindowText(h, sb, 256) == 0) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == myPid) return true;
            list.Add((h, sb.ToString(), ExePath(pid)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string? ExePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private static ImageSource? WindowIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var h = SendMessage(hwnd, 0x7F /* WM_GETICON */, (IntPtr)1 /* ICON_BIG */, IntPtr.Zero);
        if (h == IntPtr.Zero) h = SendMessage(hwnd, 0x7F, (IntPtr)2 /* ICON_SMALL2 */, IntPtr.Zero);
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        if (h == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private static DateTime _pinnedRead;
    private static List<(string lnk, string? target)> _pinned = new();

    /// <summary>The apps pinned to Windows' taskbar (their shortcuts), refreshed every 30 s.</summary>
    private static List<(string lnk, string? target)> PinnedShortcuts()
    {
        if (DateTime.Now - _pinnedRead < TimeSpan.FromSeconds(30)) return _pinned;
        _pinnedRead = DateTime.Now;
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        var list = new List<(string, string?)>();
        if (Directory.Exists(dir))
        {
            foreach (var lnk in Directory.GetFiles(dir, "*.lnk").OrderBy(f => File.GetCreationTime(f)))
                list.Add((lnk, ShortcutTarget(lnk)));
        }
        _pinned = list;
        return list;
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            var sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }
}
