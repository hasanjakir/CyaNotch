using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CyaNotch.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CyaNotch.Services;

public sealed class MediaInfo
{
    public bool HasSession { get; init; }
    public bool IsPlaying { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AppName { get; init; } = "";
    /// <summary>Windows' id for the playing app (e.g. "Spotify.exe", "chrome").</summary>
    public string AppId { get; init; } = "";
    public BitmapImage? Artwork { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset LastUpdated { get; init; }

    /// <summary>Position extrapolated from the last report while playing.</summary>
    public TimeSpan LivePosition
    {
        get
        {
            if (!IsPlaying) return Position;
            var p = Position + (DateTimeOffset.Now - LastUpdated);
            return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }

    public static readonly MediaInfo Empty = new();
}

/// <summary>
/// Reads "now playing" from Windows (any app that reports to the system media controls:
/// Spotify, browsers, VLC, foobar2000, Apple Music, ...) and sends play/pause/skip/seek back.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _artKey = "";
    private BitmapImage? _art;

    public MediaInfo State { get; private set; } = MediaInfo.Empty;

    /// <summary>Raised on a background thread; marshal to the UI.</summary>
    public event Action<MediaInfo>? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Attach();
            _manager.SessionsChanged += (_, _) => Attach();
            Attach();
        }
        catch (Exception ex)
        {
            Log.Error("Media sessions unavailable", ex);
        }
    }

    private void Attach()
    {
        if (_manager == null) return;
        var next = PickSession();
        if (!ReferenceEquals(next, _session))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.PlaybackInfoChanged -= OnPlayback;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = next;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnProps;
                _session.PlaybackInfoChanged += OnPlayback;
                _session.TimelinePropertiesChanged += OnTimeline;
            }
        }
        _ = RefreshAsync();
    }

    private static readonly string[] MusicApps =
    {
        "spotify", "applemusic", "itunes", "foobar", "vlc", "potplayer", "aimp", "musicbee", "winamp", "tidal",
        "deezer", "amazonmusic", "zunemusic", "mediaplayer", "youtubemusic", "cider", "qobuz", "soundcloud", "mpv",
    };

    /// <summary>Whether Settings lets this app show in the music card.</summary>
    private static bool Allowed(GlobalSystemMediaTransportControlsSession s)
    {
        var id = (s.SourceAppUserModelId ?? "").ToLowerInvariant().Replace(" ", "");
        var name = FriendlyApp(s.SourceAppUserModelId ?? "");
        return Settings.Current.MediaFilter switch
        {
            MediaFilter.MusicApps => MusicApps.Any(id.Contains),
            MediaFilter.AllExcept => !Settings.Current.MediaIgnoredApps.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)),
            _ => true,
        };
    }

    /// <summary>Names of apps that currently report media, for the Settings list.</summary>
    public string[] KnownApps()
    {
        try { return _manager?.GetSessions().Select(x => FriendlyApp(x.SourceAppUserModelId ?? "")).Distinct().ToArray() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public void Reselect() => Attach();

    /// <summary>Prefer whatever is actually playing; fall back to Windows' current session.</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        try
        {
            var sessions = _manager!.GetSessions().Where(Allowed).ToList();
            var playing = sessions.FirstOrDefault(s =>
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            var current = _manager.GetCurrentSession();
            return playing ?? (current != null && Allowed(current) ? current : null) ?? sessions.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Attach();
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var s = _session;
        if (s == null)
        {
            Publish(MediaInfo.Empty);
            return;
        }

        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var playback = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();

            var key = $"{props?.Title}|{props?.Artist}|{s.SourceAppUserModelId}";
            if (key != _artKey)
            {
                _artKey = key;
                _art = await LoadArtAsync(props?.Thumbnail);
            }

            Publish(new MediaInfo
            {
                HasSession = true,
                IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Title = props?.Title ?? "",
                Artist = props?.Artist ?? props?.AlbumArtist ?? "",
                AppName = FriendlyApp(s.SourceAppUserModelId),
                AppId = s.SourceAppUserModelId ?? "",
                Artwork = _art,
                Position = timeline?.Position ?? TimeSpan.Zero,
                Duration = timeline == null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime,
                LastUpdated = timeline?.LastUpdatedTime ?? DateTimeOffset.Now,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Media refresh failed", ex);
        }
    }

    private void Publish(MediaInfo state)
    {
        State = state;
        Changed?.Invoke(state);
    }

    private static async Task<BitmapImage?> LoadArtAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb == null) return null;
        try
        {
            using var ras = await thumb.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static string FriendlyApp(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("msedge")) return "Edge";
        if (a.Contains("firefox")) return "Firefox";
        if (a.Contains("vlc")) return "VLC";
        if (a.Contains("applemusic") || a.Contains("apple")) return "Apple Music";
        if (a.Contains("foobar")) return "foobar2000";
        if (a.Contains("zune") || a.Contains("media")) return "Media Player";
        var name = Path.GetFileNameWithoutExtension(aumid.Split('!')[0]);
        return string.IsNullOrEmpty(name) ? "Media" : name;
    }

    public async Task PlayPauseAsync() { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
    public async Task NextAsync() { if (_session != null) await _session.TrySkipNextAsync(); }
    public async Task PreviousAsync() { if (_session != null) await _session.TrySkipPreviousAsync(); }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_session == null) return;
        await _session.TryChangePlaybackPositionAsync(position.Ticks);
    }
}
