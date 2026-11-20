using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CyaNotch.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace CyaNotch.Services;

/// <summary>A song read from a player's window title, for players that don't report to Windows.</summary>
public sealed record WindowTrack(string App, int ProcessId, string RawName, string Title, string Artist);

/// <summary>What we found out about a song: its cover and, when known, its proper title and artist.</summary>
public sealed class TrackExtra
{
    public BitmapImage? Art { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string Source { get; init; } = "";
    public bool IsVideo { get; init; }
}

/// <summary>
/// Fills in what a player leaves out. Order: the cover inside the music file itself (Windows'
/// own thumbnail for it), a cover image in the song's folder, then the iTunes and Deezer catalogues.
/// </summary>
public static partial class TrackFinder
{
    // ------------------------------------------------------------------
    // Players that show the song in their window title
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, (string Name, string[] Suffixes)> Players = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PotPlayerMini64"] = ("PotPlayer", new[] { " - PotPlayer" }),
        ["PotPlayerMini"] = ("PotPlayer", new[] { " - PotPlayer" }),
        ["PotPlayer64"] = ("PotPlayer", new[] { " - PotPlayer" }),
        ["PotPlayer"] = ("PotPlayer", new[] { " - PotPlayer" }),
        ["vlc"] = ("VLC", new[] { " - VLC media player" }),
        ["Spotify"] = ("Spotify", Array.Empty<string>()),
        ["foobar2000"] = ("foobar2000", new[] { " [foobar2000]", " - foobar2000" }),
        ["AIMP"] = ("AIMP", Array.Empty<string>()),
        ["MusicBee"] = ("MusicBee", new[] { " - MusicBee" }),
        ["winamp"] = ("Winamp", new[] { " - Winamp" }),
        ["mpc-hc64"] = ("MPC-HC", Array.Empty<string>()),
        ["mpc-hc"] = ("MPC-HC", Array.Empty<string>()),
        ["mpc-be64"] = ("MPC-BE", Array.Empty<string>()),
        ["KMPlayer"] = ("KMPlayer", new[] { " - KMPlayer" }),
        ["KMPlayer64X"] = ("KMPlayer", new[] { " - KMPlayer" }),
        ["wmplayer"] = ("Windows Media Player", new[] { " - Windows Media Player" }),
        ["GOM"] = ("GOM Player", new[] { " - GOM Player" }),
        ["Dopamine"] = ("Dopamine", Array.Empty<string>()),
    };

    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma", ".alac", ".ape", ".aiff", ".aif",
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".m4v", ".ts",
    };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".m4v", ".ts",
    };

    [GeneratedRegex(@"^\s*\d{1,3}\s*[.\-_)]\s*")]
    private static partial Regex TrackNumber();

    /// <summary>The first known player whose window names a song, or null.</summary>
    public static WindowTrack? FromWindows()
    {
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return null; }
        WindowTrack? found = null;
        foreach (var p in procs)
        {
            try
            {
                if (found == null && Players.TryGetValue(p.ProcessName, out var player))
                {
                    var t = p.MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(t)) found = Parse(player.Name, player.Suffixes, p.Id, t);
                }
            }
            catch { /* process exited or access denied */ }
            finally { p.Dispose(); }
        }
        return found;
    }

    private static WindowTrack? Parse(string app, string[] suffixes, int pid, string title)
    {
        var t = title.Trim();
        foreach (var s in suffixes)
            if (t.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { t = t[..^s.Length].Trim(); break; }
        // An idle player shows just its own name ("PotPlayer", "Spotify Premium", "foobar2000 v2.1").
        if (t.Length == 0 || t.StartsWith(app, StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("VLC media player", StringComparison.OrdinalIgnoreCase)) return null;

        var raw = t;
        var ext = Path.GetExtension(t);
        if (ext.Length > 1 && MediaExts.Contains(ext)) t = t[..^ext.Length];
        t = TrackNumber().Replace(t, "").Replace('_', ' ').Trim();
        var artist = "";
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            artist = t[..dash].Trim();
            t = t[(dash + 3)..].Trim();
        }
        return t.Length == 0 ? null : new WindowTrack(app, pid, raw, t, artist);
    }

    // ------------------------------------------------------------------
    // The music file itself
    // ------------------------------------------------------------------

    private static readonly Dictionary<int, string[]> CmdDirs = new();
    private static readonly Dictionary<string, string?> FileCache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _lastDir;

    /// <summary>Finds the playing file on disk and reads its cover and tags.</summary>
    public static async Task<TrackExtra?> FromFileAsync(WindowTrack w)
    {
        var path = FindFile(w);
        if (path == null) return null;
        _lastDir = Path.GetDirectoryName(path);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            string? title = null, artist = null;
            try
            {
                var music = await file.Properties.GetMusicPropertiesAsync();
                if (!string.IsNullOrWhiteSpace(music.Title)) title = music.Title.Trim();
                if (!string.IsNullOrWhiteSpace(music.Artist)) artist = music.Artist.Trim();
                else if (!string.IsNullOrWhiteSpace(music.AlbumArtist)) artist = music.AlbumArtist.Trim();
            }
            catch { /* no tags */ }

            BitmapImage? art = null;
            // Videos (not music) use a frame from the video as their cover.
            var video = VideoExts.Contains(Path.GetExtension(path));
            using (var thumb = await file.GetThumbnailAsync(video ? ThumbnailMode.VideosView : ThumbnailMode.MusicView, 300, ThumbnailOptions.UseCurrentScale))
            {
                // Type "Icon" is Windows' generic note: the file has no picture of its own.
                if (thumb != null && thumb.Type == ThumbnailType.Image)
                {
                    using var s = thumb.AsStreamForRead();
                    art = Decode(s);
                }
            }
            art ??= FolderCover(Path.GetDirectoryName(path));
            return new TrackExtra { Art = art, Title = title, Artist = artist, Source = "file", IsVideo = video };
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static BitmapImage? FolderCover(string? dir)
    {
        if (dir == null) return null;
        try
        {
            foreach (var name in new[] { "cover", "folder", "front", "album", "albumart" })
                foreach (var ext in new[] { ".jpg", ".jpeg", ".png" })
                {
                    var p = Path.Combine(dir, name + ext);
                    if (File.Exists(p)) { using var fs = File.OpenRead(p); return Decode(fs); }
                }
        }
        catch { }
        return null;
    }

    private static string? FindFile(WindowTrack w)
    {
        var name = w.RawName;
        var hasExt = MediaExts.Contains(Path.GetExtension(name));
        lock (FileCache) if (FileCache.TryGetValue(name, out var hit) && (hit == null || File.Exists(hit))) return hit;

        bool Match(string file) => hasExt
            ? string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)
            : MediaExts.Contains(Path.GetExtension(file)) && string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase);

        var dirs = new List<string>();
        foreach (var arg in CommandLineFiles(w.ProcessId))
        {
            if (File.Exists(arg) && Match(arg)) return Remember(name, arg);
            var d = Directory.Exists(arg) ? arg : Path.GetDirectoryName(arg);
            if (d != null) dirs.Add(d);
        }
        if (_lastDir != null) dirs.Add(_lastDir);
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, "Downloads"));
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        dirs.Add(Path.Combine(home, "OneDrive", "Music"));

        var opts = new System.IO.EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 5 };
        foreach (var d in dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var n = 0;
                foreach (var f in Directory.EnumerateFiles(d, "*", opts))
                {
                    if (Match(f)) return Remember(name, f);
                    if (++n > 40000) break;
                }
            }
            catch { }
        }
        return Remember(name, null);
    }

    private static string? Remember(string name, string? path)
    {
        lock (FileCache) FileCache[name] = path;
        return path;
    }

    /// <summary>Files and folders on the player's command line (what was double-clicked to open it).</summary>
    private static IEnumerable<string> CommandLineFiles(int pid)
    {
        lock (CmdDirs) if (CmdDirs.TryGetValue(pid, out var c)) return c;
        var result = new List<string>();
        try
        {
            using var q = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject o in q.Get())
            {
                var cmd = o["CommandLine"] as string ?? "";
                foreach (Match m in Regex.Matches(cmd, "\"([^\"]+)\"|(\\S+)"))
                {
                    var s = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || s.StartsWith('/') || s.StartsWith('-')) continue;
                    if (File.Exists(s) || Directory.Exists(s)) result.Add(s);
                }
            }
        }
        catch { }
        var arr = result.ToArray();
        lock (CmdDirs) CmdDirs[pid] = arr;
        return arr;
    }

    // ------------------------------------------------------------------
    // Public catalogues (no account): iTunes Search, then Deezer
    // ------------------------------------------------------------------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Dictionary<string, TrackExtra?> OnlineCache = new();

    static TrackFinder()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("CyaNotch-desktop/0.3");
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(official|lyric|audio|video|visuali[sz]er|hd|hq|4k|remaster|live|mv|m/v)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex VideoTags();

    [GeneratedRegex(@"\s+(ft\.?|feat\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    public static string CleanTitle(string s)
    {
        s = VideoTags().Replace(s, "");
        s = Featuring().Replace(s, "");
        var bar = s.IndexOf(" | ", StringComparison.Ordinal);
        if (bar > 0) s = s[..bar];
        return s.Trim();
    }

    private static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static async Task<TrackExtra?> OnlineAsync(string title, string artist)
    {
        var t = CleanTitle(title);
        var a = artist.Replace("VEVO", "", StringComparison.OrdinalIgnoreCase).Replace("- Topic", "").Trim();
        // "Artist - Song" in the title (video sites): split it when we have no artist.
        if (a.Length == 0 && t.IndexOf(" - ", StringComparison.Ordinal) is var d and > 0)
        {
            a = t[..d].Trim();
            t = t[(d + 3)..].Trim();
        }
        if (t.Length == 0) return null;
        var key = (a + "|" + t).ToLowerInvariant();
        lock (OnlineCache) if (OnlineCache.TryGetValue(key, out var hit)) return hit;

        TrackExtra? result = null;
        try
        {
            result = await SpotifyAsync(t, a) ?? await ITunesAsync(t, a) ?? await DeezerAsync(t, a);
        }
        catch (Exception ex)
        {
            Log.Info("Online cover lookup failed: " + ex.Message);
            return null; // don't cache network trouble
        }
        lock (OnlineCache) OnlineCache[key] = result;
        Log.Info(result == null ? $"No cover found online for \"{a} - {t}\"" : $"Cover for \"{a} - {t}\" from {result.Source}");
        return result;
    }

    /// <summary>The result that best matches the song name (and artist, when we know it).</summary>
    private static int Score(string wantTitle, string wantArtist, string title, string artist)
    {
        var wt = Squash(wantTitle); var tt = Squash(title);
        var wa = Squash(wantArtist); var ta = Squash(artist);
        var score = 0;
        if (tt == wt) score += 4; else if (tt.Contains(wt) || wt.Contains(tt)) score += 2;
        if (wa.Length > 0 && ta.Length > 0)
        {
            if (ta == wa) score += 3; else if (ta.Contains(wa) || wa.Contains(ta)) score += 2; else score -= 2;
        }
        return score;
    }

    // Spotify needs an app key: the person's own free one from developer.spotify.com (client credentials).
    private static string _spotifyToken = "";
    private static DateTime _spotifyExpires;
    private static string _spotifyFor = "";

    private static async Task<string?> SpotifyTokenAsync()
    {
        var id = Settings.Current.SpotifyClientId.Trim();
        var secret = Settings.Current.SpotifySecret.Trim();
        if (id.Length == 0 || secret.Length == 0) return null;
        if (_spotifyFor == id && DateTime.UtcNow < _spotifyExpires) return _spotifyToken;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(id + ":" + secret)));
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Info($"Spotify sign-in failed ({(int)resp.StatusCode}). Check the client ID and secret in Settings.");
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        _spotifyToken = Str(doc.RootElement, "access_token");
        _spotifyExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        _spotifyFor = id;
        return _spotifyToken;
    }

    /// <summary>Checks the Spotify keys from Settings; null when they work, otherwise what's wrong.</summary>
    public static async Task<string?> TestSpotifyAsync()
    {
        _spotifyFor = "";
        lock (OnlineCache) OnlineCache.Clear(); // songs that found nothing before get another try
        try { return await SpotifyTokenAsync() == null ? "Spotify didn't accept these keys." : null; }
        catch (Exception ex) { return "Couldn't reach Spotify: " + ex.Message; }
    }

    private static async Task<TrackExtra?> SpotifyAsync(string title, string artist)
    {
        var token = await SpotifyTokenAsync();
        if (token == null) return null;
        var q = artist.Length > 0 ? $"track:{title} artist:{artist}" : title;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/search?type=track&limit=8&q=" + Uri.EscapeDataString(q));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("tracks", out var tracks) || !tracks.TryGetProperty("items", out var items)) return null;
        static string FirstArtist(JsonElement t) =>
            t.TryGetProperty("artists", out var a) && a.GetArrayLength() > 0 ? Str(a[0], "name") : "";
        var best = items.EnumerateArray()
            .Select(t => (t, s: Score(title, artist, Str(t, "name"), FirstArtist(t))))
            .Where(x => x.s >= 2).OrderByDescending(x => x.s).Select(x => (JsonElement?)x.t).FirstOrDefault();
        if (best is not { } b || !b.TryGetProperty("album", out var album) || !album.TryGetProperty("images", out var imgs) || imgs.GetArrayLength() == 0)
            return null;
        var art = await DownloadAsync(Str(imgs[0], "url")); // largest first (640 px)
        return art == null ? null : new TrackExtra { Art = art, Title = Str(b, "name"), Artist = FirstArtist(b), Source = "Spotify" };
    }

    private static async Task<TrackExtra?> ITunesAsync(string title, string artist)
    {
        var term = Uri.EscapeDataString((artist + " " + title).Trim());
        var json = await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=8");
        using var doc = JsonDocument.Parse(json);
        var best = doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => (r, s: Score(title, artist, Str(r, "trackName"), Str(r, "artistName"))))
            .Where(x => x.s >= 2 && Str(x.r, "artworkUrl100").Length > 0)
            .OrderByDescending(x => x.s).Select(x => (JsonElement?)x.r).FirstOrDefault();
        if (best is not { } b) return null;
        var url = Str(b, "artworkUrl100").Replace("100x100bb", "600x600bb");
        var art = await DownloadAsync(url);
        return art == null ? null : new TrackExtra { Art = art, Title = Str(b, "trackName"), Artist = Str(b, "artistName"), Source = "iTunes" };
    }

    private static async Task<TrackExtra?> DeezerAsync(string title, string artist)
    {
        var q = artist.Length > 0 ? $"artist:\"{artist}\" track:\"{title}\"" : title;
        var json = await Http.GetStringAsync("https://api.deezer.com/search?limit=8&q=" + Uri.EscapeDataString(q));
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        var best = data.EnumerateArray()
            .Select(r => (r, s: Score(title, artist, Str(r, "title"),
                r.TryGetProperty("artist", out var ar) ? Str(ar, "name") : "")))
            .Where(x => x.s >= 2).OrderByDescending(x => x.s).Select(x => (JsonElement?)x.r).FirstOrDefault();
        if (best is not { } b || !b.TryGetProperty("album", out var album)) return null;
        var url = Str(album, "cover_xl");
        if (url.Length == 0) url = Str(album, "cover_big");
        var art = url.Length == 0 ? null : await DownloadAsync(url);
        return art == null ? null : new TrackExtra
        {
            Art = art, Title = Str(b, "title"),
            Artist = b.TryGetProperty("artist", out var a2) ? Str(a2, "name") : null, Source = "Deezer",
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<BitmapImage?> DownloadAsync(string url)
    {
        var bytes = await Http.GetByteArrayAsync(url);
        using var ms = new MemoryStream(bytes);
        return Decode(ms);
    }

    private static BitmapImage? Decode(Stream s)
    {
        try
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
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
}
