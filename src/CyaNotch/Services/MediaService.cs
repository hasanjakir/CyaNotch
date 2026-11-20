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
    /// <summary>Read from the player's window title (no Windows media session): no seeking.</summary>
    public bool FromWindowTitle { get; init; }

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
    private System.Threading.Timer? _poll;
    private WindowTrack? _window;
    private DateTime _lastSound = DateTime.MinValue;
    private string _extraKey = "";
    private TrackExtra? _extra;
    private bool _usingWindow;

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
        // Players like PotPlayer never tell Windows what they play; read their window title instead.
        _poll = new System.Threading.Timer(_ => PollWindows(), null, 1000, 1500);
    }

    private void PollWindows()
    {
        try
        {
            if (App.Visualizer.HasSound) _lastSound = DateTime.Now;
            var w = TrackFinder.FromWindows();
            var changed = w != _window;
            var wasPlaying = _usingWindow && State.IsPlaying;
            _window = w;
            var nowPlaying = w != null && WindowPlaying;
            if (changed || (_usingWindow || w != null) && nowPlaying != wasPlaying) _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Player window check failed", ex);
        }
    }

    /// <summary>A window-title player counts as playing while sound has been coming out lately.</summary>
    private bool WindowPlaying => (DateTime.Now - _lastSound).TotalSeconds < 3;

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
        catch (Exception ex)
        {
            Log.Info("Artwork could not be read: " + ex.Message);
            return null;
        }
    }

    private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Attach();
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var s = _session;
        var w = _window;
        var sessionPlaying = false;
        try { sessionPlaying = s?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { }
        // A real media session wins while it plays; otherwise a player we can read from its title.
        if (w != null && !sessionPlaying && (s == null || WindowPlaying))
        {
            _usingWindow = true;
            PublishWindow(w);
            return;
        }
        _usingWindow = false;
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
            // Browsers often send the title first and the artwork a moment later, so keep trying
            // while a song has no artwork yet, not just when the song changes.
            if (key != _artKey || (_art == null && props?.Thumbnail != null))
            {
                _artKey = key;
                _art = await LoadArtAsync(props?.Thumbnail);
                if (_art == null && props?.Thumbnail != null) _artRetries++;
                else _artRetries = 0;
                if (_art == null && _artRetries is > 0 and < 4) _ = RetryArtSoon();
            }
            // No picture from the app (or it never loads): look the song up.
            if (_art == null && (props?.Thumbnail == null || _artRetries >= 4) && !string.IsNullOrWhiteSpace(props?.Title))
                FindExtra(key, props!.Title, props.Artist ?? "", null);
            var extra = _extraKey == key ? _extra : null;
            var artist = props?.Artist ?? props?.AlbumArtist ?? "";
            if (string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(extra?.Artist)) artist = extra!.Artist!;

            Publish(new MediaInfo
            {
                HasSession = true,
                IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Title = props?.Title ?? "",
                Artist = artist,
                AppName = FriendlyApp(s.SourceAppUserModelId),
                AppId = s.SourceAppUserModelId ?? "",
                Artwork = _art ?? extra?.Art,
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

    private int _artRetries;

    private void PublishWindow(WindowTrack w)
    {
        var key = $"window|{w.App}|{w.RawName}";
        FindExtra(key, w.Title, w.Artist, w);
        var x = _extraKey == key ? _extra : null;
        Publish(new MediaInfo
        {
            HasSession = true,
            IsPlaying = WindowPlaying,
            // Tags in the file beat the file name; the catalogue only fills what's missing.
            Title = x?.Source == "file" && !string.IsNullOrEmpty(x.Title) ? x.Title : w.Title,
            Artist = x?.Source == "file" && !string.IsNullOrEmpty(x.Artist) ? x.Artist
                   : w.Artist.Length > 0 ? w.Artist : x?.Artist ?? "",
            AppName = w.App,
            AppId = w.App,
            Artwork = x?.Art,
            LastUpdated = DateTimeOffset.Now,
            FromWindowTitle = true,
        });
    }

    /// <summary>Looks for the cover once per song: the file's own picture, then the online catalogues.</summary>
    private void FindExtra(string key, string title, string artist, WindowTrack? w)
    {
        if (key == _extraKey) return;
        _extraKey = key;
        _extra = null;
        _ = Task.Run(async () =>
        {
            TrackExtra? x = null;
            try
            {
                if (w != null) x = await TrackFinder.FromFileAsync(w);
                if (x?.Art == null && Settings.Current.OnlineArtwork)
                {
                    var o = await TrackFinder.OnlineAsync(x?.Title ?? title, x?.Artist ?? artist);
                    if (o != null)
                        x = x == null ? o : new TrackExtra { Art = o.Art, Title = x.Title, Artist = x.Artist ?? o.Artist, Source = x.Source };
                }
            }
            catch (Exception ex)
            {
                Log.Error("Cover lookup failed", ex);
            }
            if (key != _extraKey) return;
            _extra = x;
            if (x != null) await RefreshAsync();
        });
    }

    // Media keys for players without a media session; most players listen for them.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private static void MediaKey(byte vk)
    {
        keybd_event(vk, 0, 1, UIntPtr.Zero);
        keybd_event(vk, 0, 3, UIntPtr.Zero);
    }

    private async Task RetryArtSoon()
    {
        await Task.Delay(700);
        await RefreshAsync();
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

    public async Task PlayPauseAsync()
    {
        if (_usingWindow || _session == null) { MediaKey(0xB3); return; }
        await _session.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_usingWindow || _session == null) { MediaKey(0xB0); return; }
        await _session.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_usingWindow || _session == null) { MediaKey(0xB1); return; }
        await _session.TrySkipPreviousAsync();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_session == null || _usingWindow) return;
        await _session.TryChangePlaybackPositionAsync(position.Ticks);
    }
}
