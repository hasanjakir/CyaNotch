using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CyaNotch.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CyaNotch.Views.Tabs;

/// <summary>Calls: one big mute button for every app, plus quick mic and speaker tests.</summary>
public partial class CallsPage : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private WasapiCapture? _capture;
    private DateTime _testUntil;
    private float _peak;

    public CallsPage()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); _tick.Start(); }
            else { _tick.Stop(); StopMicTest(); }
        };
        App.Audio.MicMuteChanged += () => Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var muted = App.Audio.MicMuted;
        MicGlyph.Text = muted ? "" : "";
        MicBtn.Background = muted ? (Brush)FindResource("DangerBrush") : new SolidColorBrush(Color.FromArgb(0x26, 0x3D, 0xDC, 0x84));
        MicGlyph.Foreground = muted ? Brushes.White : (Brush)FindResource("TextBrush");
        MicState.Text = muted ? "Mic muted\nClick to unmute" : "Mic on\nClick to mute";
        InUse.Text = App.Audio.IsMicInUse() ? "An app is using your mic" : "";
        MicName.Text = App.Audio.MicName;
        SpeakerName.Text = App.Audio.SpeakerName;
    }

    private void Tick()
    {
        if (_capture != null && DateTime.Now > _testUntil) StopMicTest();
        var p = _peak;
        _peak *= 0.8f; // fall back smoothly between buffers
        var track = (Level.Parent as FrameworkElement)?.ActualWidth ?? 0;
        Level.Width = Math.Clamp(Math.Sqrt(p), 0, 1) * track;
    }

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        App.Audio.ToggleMic();
        Refresh();
    }

    private void MicTest_Click(object sender, RoutedEventArgs e)
    {
        if (_capture != null) { StopMicTest(); return; }
        var dev = App.Audio.DefaultMic();
        if (dev == null) return;
        try
        {
            _capture = new WasapiCapture(dev);
            _capture.DataAvailable += (_, a) => _peak = Math.Max(_peak, Peak(a.Buffer, a.BytesRecorded, _capture.WaveFormat));
            _capture.StartRecording();
            _testUntil = DateTime.Now.AddSeconds(15);
            MicTestBtn.Content = "Stop";
        }
        catch (Exception ex)
        {
            Log.Error("Mic test failed", ex);
            StopMicTest();
        }
    }

    private void StopMicTest()
    {
        try { _capture?.StopRecording(); _capture?.Dispose(); } catch { }
        _capture = null;
        _peak = 0;
        MicTestBtn.Content = "Test";
    }

    private static float Peak(byte[] buf, int count, WaveFormat fmt)
    {
        float max = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32))
        {
            for (int i = 0; i + 4 <= count; i += 4) max = Math.Max(max, Math.Abs(BitConverter.ToSingle(buf, i)));
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= count; i += 2) max = Math.Max(max, Math.Abs(BitConverter.ToInt16(buf, i) / 32768f));
        }
        return Math.Min(1, max);
    }

    private void SpeakerTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tone = new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 523.25, Gain = 0.12 }
                .Take(TimeSpan.FromSeconds(0.9));
            var output = new WasapiOut(AudioClientShareMode.Shared, 80);
            output.Init(tone);
            output.PlaybackStopped += (_, _) => output.Dispose();
            output.Play();
        }
        catch (Exception ex)
        {
            Log.Error("Speaker test failed", ex);
        }
    }

    private void CheckCamera_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Camera app failed", ex); }
    }

    private void SoundSettings_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Services.SystemService.OpenSettings("sound");
}
