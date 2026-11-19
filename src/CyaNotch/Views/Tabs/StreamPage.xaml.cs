using System;
using System.Diagnostics;
using System.IO;
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
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); _tick.Start(); } else _tick.Stop(); };
        App.Obs.Changed += () => Dispatcher.BeginInvoke(Refresh);
        App.Capture.Changed += () => Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var c = App.Capture;
        RecText.Text = c.Recording ? $"Stop {(DateTime.Now - c.RecordingSince):mm\\:ss}" : "Record";
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
}
