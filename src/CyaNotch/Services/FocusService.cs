using System;
using CyaNotch.Core;

namespace CyaNotch.Services;

/// <summary>
/// Focus sessions: work for a set time, then take a short break. While a session runs the island
/// shows the time left and (if Settings say so) holds back notification pop-ups.
/// </summary>
public sealed class FocusService
{
    public enum Phase { Idle, Focus, Break }

    public Phase Current { get; private set; } = Phase.Idle;
    public DateTime EndsAt { get; private set; }
    public TimeSpan Length { get; private set; }
    public int SessionsToday => _day == DateTime.Today ? _sessions : 0;

    private int _sessions;
    private DateTime _day = DateTime.Today;

    public event Action? Changed;

    public TimeSpan Left => Current == Phase.Idle ? TimeSpan.Zero : (EndsAt - DateTime.Now) < TimeSpan.Zero ? TimeSpan.Zero : EndsAt - DateTime.Now;

    /// <summary>0..1 of the current phase done.</summary>
    public double Progress => Current == Phase.Idle || Length <= TimeSpan.Zero ? 0 : 1 - Left.TotalSeconds / Length.TotalSeconds;

    /// <summary>True while notifications should wait.</summary>
    public bool Silencing => Current == Phase.Focus && Settings.Current.FocusSilence;

    public void Start(int minutes) => Begin(Phase.Focus, minutes);
    public void StartBreak(int minutes) => Begin(Phase.Break, minutes);

    public void Stop()
    {
        Current = Phase.Idle;
        Changed?.Invoke();
    }

    private void Begin(Phase phase, int minutes)
    {
        Current = phase;
        Length = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 600));
        EndsAt = DateTime.Now + Length;
        Changed?.Invoke();
    }

    /// <summary>Call about once a second. Returns the phase that just finished, if any.</summary>
    public Phase? Tick()
    {
        if (Current == Phase.Idle || DateTime.Now < EndsAt) return null;
        var done = Current;
        if (done == Phase.Focus)
        {
            if (_day != DateTime.Today) { _day = DateTime.Today; _sessions = 0; }
            _sessions++;
        }
        Current = Phase.Idle;
        Changed?.Invoke();
        return done;
    }
}
