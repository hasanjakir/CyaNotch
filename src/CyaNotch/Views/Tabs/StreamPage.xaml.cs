using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CyaNotch.Core;
using CyaNotch.Services;

namespace CyaNotch.Views.Tabs;

/// <summary>Stream tab: screenshots, snips, recordings, OBS status and a chat drawer.</summary>
public partial class StreamPage : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading = true;

    /// <summary>Raised after a screenshot (path, or null if it failed) so the island can confirm it.</summary>
    public event Action<string?>? ShotDone;

    public StreamPage()
    {
        InitializeComponent();
        var s = Settings.Current;
        OnShelf.IsChecked = s.CapturesOnShelf;
        ObsOn.IsChecked = s.ObsEnabled;
        ObsPort.Text = s.ObsPort.ToString();
        ObsPass.Password = s.ObsPassword;
        Twitch.IsChecked = s.ChatPlatform != "youtube";
        YouTube.IsChecked = s.ChatPlatform == "youtube";
        Channel.Text = s.ChatChannel;
        UpdateChannelHint();
        _loading = false;

        _tick.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); LoadGallery(); _tick.Start(); } else _tick.Stop(); };
        App.Obs.Changed += () => Dispatcher.BeginInvoke(Refresh);
        App.Capture.Changed += () => Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var c = App.Capture;
        RecText.Text = c.Recording ? $"Stop recording  {(DateTime.Now - c.RecordingSince):mm\\:ss}" : "Start recording";
        RecDot.RadiusX = RecDot.RadiusY = c.Recording ? 1.5 : 4.5;
        ObsStatus.Text = Settings.Current.ObsEnabled ? App.Obs.Status
            + (App.Obs.Recording ? " · recording" : "") + (App.Obs.Streaming ? " · live" : "") : "";
        ChatBtn.Content = ChatWindow.IsOpen ? "Close chat" : "Open chat";
    }

    private void Shot_Click(object sender, RoutedEventArgs e)
    {
        // Let the island close first so it isn't in the picture.
        // Keep the island itself out of the picture.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;
        Native.HideFromCapture(hwnd, true);
        var path = App.Capture.Screenshot();
        Native.HideFromCapture(hwnd, Settings.Current.HideFromCapture);
        ShotDone?.Invoke(path);
        LoadGallery();
    }

    private void Snip_Click(object sender, RoutedEventArgs e) => App.Capture.Snip();
    private void Rec_Click(object sender, RoutedEventArgs e) { App.Capture.ToggleRecording(); Refresh(); }
    private void RecFolder_Click(object sender, RoutedEventArgs e) => CaptureService.OpenRecordings();

    private void ShotsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(CaptureService.Folder);
        Process.Start(new ProcessStartInfo(CaptureService.Folder) { UseShellExecute = true });
    }

    private void OnShelf_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.CapturesOnShelf = OnShelf.IsChecked == true;
        Settings.SaveQuietly();
    }

    private void Obs_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.ObsEnabled = ObsOn.IsChecked == true;
        Settings.SaveQuietly();
        App.Obs.Start();
        Refresh();
    }

    private void ObsField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(ObsPort.Text, out var port) && port is > 0 and < 65536) Settings.Current.ObsPort = port;
        Settings.Current.ObsPassword = ObsPass.Password;
        Settings.SaveQuietly();
        App.Obs.Start();
    }

    private void Platform_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Current.ChatPlatform = YouTube.IsChecked == true ? "youtube" : "twitch";
        Settings.SaveQuietly();
        UpdateChannelHint();
    }

    private void UpdateChannelHint() =>
        Channel.Tag = YouTube.IsChecked == true ? "YouTube live link or video id" : "Twitch channel name";

    private void Channel_LostFocus(object sender, RoutedEventArgs e)
    {
        Settings.Current.ChatChannel = Channel.Text.Trim();
        Settings.SaveQuietly();
    }

    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.ChatChannel = Channel.Text.Trim();
        Settings.SaveQuietly();
        ChatWindow.Toggle();
        Refresh();
    }

    /// <summary>Opens straight on the recordings (from the record button while recording).</summary>
    public void ShowRecordings() => ViewRecs.IsChecked = true;

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (GalleryView == null) return;
        var stream = ViewStream.IsChecked == true;
        StreamView.Visibility = stream ? Visibility.Visible : Visibility.Collapsed;
        GalleryView.Visibility = stream ? Visibility.Collapsed : Visibility.Visible;
        var recs = ViewRecs.IsChecked == true;
        ShotActions.Visibility = recs ? Visibility.Collapsed : Visibility.Visible;
        OnShelf.Visibility = recs ? Visibility.Collapsed : Visibility.Visible;
        RecBtn.Visibility = recs ? Visibility.Visible : Visibility.Collapsed;
        LoadGallery();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewRecs.IsChecked == true) CaptureService.OpenRecordings();
        else ShotsFolder_Click(sender, e);
    }

    private void Gallery_Wheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        GalleryScroll.ScrollToHorizontalOffset(GalleryScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    private static readonly string[] VideoExt = { ".mp4", ".mkv", ".mov", ".webm", ".avi" };

    private void LoadGallery()
    {
        if (Gallery == null || ViewStream.IsChecked == true) return;
        var recs = ViewRecs.IsChecked == true;
        var dir = recs ? CaptureService.RecordingsFolder : CaptureService.Folder;
        var exts = recs ? VideoExt : ImageExt;
        Gallery.Children.Clear();
        FileInfo[] files;
        try
        {
            files = Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles().Where(f => exts.Contains(f.Extension.ToLowerInvariant()))
                    .OrderByDescending(f => f.LastWriteTime).Take(12).ToArray()
                : Array.Empty<FileInfo>();
        }
        catch { files = Array.Empty<FileInfo>(); }

        GalleryEmpty.Text = files.Length == 0 ? (recs ? "No recordings yet." : "No screenshots yet.") : "";
        GalleryCount.Text = files.Length == 0 ? "" : $"{files.Length} recent";
        foreach (var f in files)
        {
            var card = new Border
            {
                Width = 150, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 0),
                Background = (System.Windows.Media.Brush)FindResource("CardBrush"), ClipToBounds = true,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = f.Name,
            };
            var grid = new Grid();
            if (!recs)
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(f.FullName);
                    bmp.DecodePixelWidth = 300;
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    card.Background = new System.Windows.Media.ImageBrush(bmp) { Stretch = System.Windows.Media.Stretch.UniformToFill };
                }
                catch { }
            }
            else
            {
                grid.Children.Add(new Border
                {
                    Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0, 0, 0)),
                    Child = new TextBlock { Text = "\uE768", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.White },
                });
                grid.Children.Add(new TextBlock
                {
                    Text = f.Name, FontSize = 10, Margin = new Thickness(8, 0, 8, 6), VerticalAlignment = VerticalAlignment.Bottom,
                    TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (System.Windows.Media.Brush)FindResource("SubTextBrush"),
                });
            }
            card.Child = grid;
            var path = f.FullName;
            card.MouseLeftButtonUp += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("Open capture failed", ex); }
            };
            Gallery.Children.Add(card);
        }
    }
}
