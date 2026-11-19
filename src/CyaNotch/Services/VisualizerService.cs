using System;
using CyaNotch.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CyaNotch.Services;

/// <summary>
/// Listens to what the PC is playing (WASAPI loopback of the default speakers, nothing is recorded or saved)
/// and turns it into bar heights for the visualizer: an FFT split into log-spaced frequency bands,
/// with fast attack and slow fall-off so the bars bounce with the beat.
/// </summary>
public sealed class VisualizerService : IDisposable
{
    public const int FftSize = 2048;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private long _samplesSinceAnalysis;
    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;
    private float[] _levels = Array.Empty<float>();
    private float[] _peaks = Array.Empty<float>();
    private float _gain = 1f;
    private DateTime _lastAudio = DateTime.MinValue;

    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private static readonly float[] Window = BuildHann();

    public int Bands { get; } = 64;
    public bool Running => _capture != null;

    /// <summary>True when sound has come through in the last half second.</summary>
    public bool HasSound => (DateTime.UtcNow - _lastAudio).TotalMilliseconds < 500;

    /// <summary>Starts listening. The band count is fixed (64); each bar view resamples it to its own width.</summary>
    public void Start(int bands = 64)
    {
        lock (_gate)
        {
            if (_levels.Length != Bands)
            {
                _levels = new float[Bands];
                _peaks = new float[Bands];
            }
        }
        if (_capture != null) return;
        try
        {
            _capture = new WasapiLoopbackCapture();
            _sampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Error("Visualizer capture stopped", e.Exception);
                // Output device changed or went away; the next Start() picks up the new default.
                Cleanup();
            };
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Error("Visualizer capture failed", ex);
            Cleanup();
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
        Cleanup();
    }

    private void Cleanup()
    {
        var c = _capture;
        _capture = null;
        if (c != null)
        {
            c.DataAvailable -= OnData;
            try { c.Dispose(); } catch { }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture?.WaveFormat;
        if (fmt == null || e.BytesRecorded == 0) return;

        int channels = fmt.Channels;
        int frames;
        bool loud = false;

        lock (_gate)
        {
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32))
            {
                frames = e.BytesRecorded / (4 * channels);
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += BitConverter.ToSingle(e.Buffer, (f * channels + c) * 4);
                    var v = sum / channels;
                    if (v > 0.0005f || v < -0.0005f) loud = true;
                    _ring[_ringPos] = v;
                    _ringPos = (_ringPos + 1) % FftSize;
                }
            }
            else if (fmt.BitsPerSample == 16)
            {
                frames = e.BytesRecorded / (2 * channels);
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(e.Buffer, (f * channels + c) * 2) / 32768f;
                    var v = sum / channels;
                    if (v > 0.0005f || v < -0.0005f) loud = true;
                    _ring[_ringPos] = v;
                    _ringPos = (_ringPos + 1) % FftSize;
                }
            }
            else
            {
                return;
            }
            _samplesSinceAnalysis += frames;
        }
        if (loud) _lastAudio = DateTime.UtcNow;
    }

    /// <summary>
    /// Call once per rendered frame. Returns smoothed band levels 0..1 (the array is reused).
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastReadMs = -1000;

    public float[] Read(double dtSeconds)
    {
        // Several bar views may read in the same frame; only advance the smoothing once.
        var nowMs = _clock.Elapsed.TotalMilliseconds;
        if (nowMs - _lastReadMs < 5) return _levels;
        dtSeconds = Math.Clamp((nowMs - _lastReadMs) / 1000.0, 0.001, 0.1);
        _lastReadMs = nowMs;

        lock (_gate)
        {
            if (_levels.Length != Bands) { _levels = new float[Bands]; _peaks = new float[Bands]; }

            // Silence (or not capturing): let the bars fall.
            if (!HasSound || _capture == null)
            {
                for (int i = 0; i < _levels.Length; i++) _levels[i] = Math.Max(0, _levels[i] - (float)(dtSeconds * 2.2));
                return _levels;
            }

            // Analyse at most ~90 times a second.
            if (_samplesSinceAnalysis < _sampleRate / 90) return Decay(dtSeconds);
            _samplesSinceAnalysis = 0;

            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = _ring[(_ringPos + i) % FftSize] * Window[i];
                _im[i] = 0;
            }
        }

        Fft(_re, _im);

        var binHz = (double)_sampleRate / FftSize;
        const double fMin = 35, fMax = 16000;
        var target = new float[Bands];
        float loudest = 0;

        for (int b = 0; b < Bands; b++)
        {
            var lo = fMin * Math.Pow(fMax / fMin, (double)b / Bands);
            var hi = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / Bands);
            int i0 = Math.Max(1, (int)(lo / binHz));
            int i1 = Math.Max(i0, Math.Min(FftSize / 2 - 1, (int)(hi / binHz)));
            double sum = 0;
            for (int i = i0; i <= i1; i++) sum = Math.Max(sum, Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]));
            // Tilt: music has far less energy up high, so lift treble ~4.5 dB per octave.
            var tilt = Math.Pow((lo + hi) / 2 / 200.0, 0.75);
            var mag = sum * tilt / (FftSize / 4.0);
            var db = 20 * Math.Log10(mag + 1e-9);
            var v = (float)Math.Clamp((db + 62) / 52, 0, 1);
            target[b] = v;
            loudest = Math.Max(loudest, v);
        }

        lock (_gate)
        {
            // Gentle auto-gain so quiet tracks still move and loud ones don't pin at the top.
            var desired = loudest > 0.05f ? Math.Clamp(0.9f / loudest, 0.7f, 2.2f) : _gain;
            _gain += (desired - _gain) * (float)Math.Min(1, dtSeconds * 0.8);

            for (int b = 0; b < Bands; b++)
            {
                var t = Math.Min(1f, target[b] * _gain);
                t = t * t * (3 - 2 * t); // smoothstep for punchier motion
                if (t > _levels[b]) _levels[b] += (t - _levels[b]) * (float)Math.Min(1, dtSeconds * 28); // attack
                else _levels[b] -= (_levels[b] - t) * (float)Math.Min(1, dtSeconds * 7);             // release
            }
            return _levels;
        }
    }

    private float[] Decay(double dt)
    {
        // Between analyses keep easing toward the last target (already applied); nothing to do.
        return _levels;
    }

    private static float[] BuildHann()
    {
        var w = new float[FftSize];
        for (int i = 0; i < FftSize; i++) w[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
        return w;
    }

    /// <summary>In-place radix-2 Cooley–Tukey FFT.</summary>
    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = re[b] * cr - im[b] * ci;
                    float ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
