using System;
using CyaNotch.Core;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace CyaNotch.Services;

/// <summary>Speaker volume, system-wide microphone mute, and "mic in use" detection for the call timer.</summary>
public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public event Action? MicMuteChanged;

    private MMDevice? Speaker()
    {
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return null; }
    }

    private MMDevice? Mic(Role role)
    {
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role); }
        catch { return null; }
    }

    /// <summary>0..1</summary>
    public double Volume
    {
        get => Speaker()?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0;
        set
        {
            var s = Speaker();
            if (s == null) return;
            s.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(value, 0, 1);
            if (value > 0 && s.AudioEndpointVolume.Mute) s.AudioEndpointVolume.Mute = false;
        }
    }

    public bool SpeakerMuted
    {
        get => Speaker()?.AudioEndpointVolume.Mute ?? false;
        set { var s = Speaker(); if (s != null) s.AudioEndpointVolume.Mute = value; }
    }

    public bool HasMic => Mic(Role.Communications) != null || Mic(Role.Console) != null;

    /// <summary>Muted if the default mic (and the default communications mic) are muted.</summary>
    public bool MicMuted
    {
        get
        {
            var m = Mic(Role.Communications) ?? Mic(Role.Console);
            return m?.AudioEndpointVolume.Mute ?? false;
        }
        set
        {
            // Mute every active capture device so every app goes silent, not just one.
            try
            {
                foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    d.AudioEndpointVolume.Mute = value;
            }
            catch (Exception ex)
            {
                Log.Error("Mic mute failed", ex);
            }
            MicMuteChanged?.Invoke();
        }
    }

    public void ToggleMic() => MicMuted = !MicMuted;

    /// <summary>
    /// True when any app is recording from the microphone right now. Windows records this in the
    /// privacy "consent store": an entry with LastUsedTimeStop == 0 is in use.
    /// </summary>
    public bool IsMicInUse()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(root);
            if (key == null) return false;
            return AnyInUse(key);
        }
        catch
        {
            return false;
        }
    }

    private static bool AnyInUse(RegistryKey key)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub == null) continue;
            if (name == "NonPackaged")
            {
                if (AnyInUse(sub)) return true;
                continue;
            }
            if (sub.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
                sub.GetValue("LastUsedTimeStart") is long start && start > 0)
                return true;
        }
        return false;
    }

    public void Dispose() => _enumerator.Dispose();
}
