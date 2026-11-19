using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CyaNotch.Core;

namespace CyaNotch.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed class Lyrics
{
    public List<LyricLine> Lines { get; } = new();
    public bool Synced { get; init; }

    /// <summary>Index of the line being sung at this position, or -1 before the first line.</summary>
    public int IndexAt(TimeSpan position)
    {
        if (!Synced) return -1;
        int lo = 0, hi = Lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines[mid].Time <= position) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }
}

/// <summary>
/// Song lyrics from LRCLIB (free, open, no account). Only used when lyrics are switched on in Settings.
/// </summary>
public sealed partial class LyricsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Dictionary<string, Lyrics?> _cache = new();

    static LyricsService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("CyaNotch-desktop/0.2");
    }

    [GeneratedRegex(@"^\[(\d+):(\d+(?:[.:]\d+)?)\](.*)$")]
    private static partial Regex LrcLine();

    public async Task<Lyrics?> GetAsync(string title, string artist, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var key = $"{artist}|{title}".ToLowerInvariant();
        lock (_cache) if (_cache.TryGetValue(key, out var hit)) return hit;

        Lyrics? result = null;
        try
        {
            var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(Clean(title)) +
                      "&artist_name=" + Uri.EscapeDataString(artist);
            if (duration > TimeSpan.Zero) url += "&duration=" + (int)duration.TotalSeconds;

            using var resp = await Http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // Retry without duration: browsers often report a slightly different length.
                resp.Dispose();
                using var retry = await Http.GetAsync(url.Split("&duration=")[0]);
                if (retry.IsSuccessStatusCode) result = Parse(await retry.Content.ReadAsStringAsync());
            }
            else if (resp.IsSuccessStatusCode)
            {
                result = Parse(await resp.Content.ReadAsStringAsync());
            }
        }
        catch (Exception ex)
        {
            Log.Error("Lyrics lookup failed", ex);
            return null; // don't cache network failures
        }

        lock (_cache) _cache[key] = result;
        return result;
    }

    /// <summary>"Song (Official Video)" → "Song"</summary>
    private static string Clean(string title)
    {
        foreach (var marker in new[] { " (Official", " [Official", " (Lyric", " [Lyric", " (Audio", " | " })
        {
            var i = title.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i > 0) title = title[..i];
        }
        return title.Trim();
    }

    private static Lyrics? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String)
        {
            var lyrics = new Lyrics { Synced = true };
            foreach (var raw in synced.GetString()!.Split('\n'))
            {
                var m = LrcLine().Match(raw.Trim());
                if (!m.Success) continue;
                var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lyrics.Lines.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec), m.Groups[3].Value.Trim()));
            }
            if (lyrics.Lines.Count > 0) return lyrics;
        }
        if (root.TryGetProperty("plainLyrics", out var plain) && plain.ValueKind == JsonValueKind.String)
        {
            var lyrics = new Lyrics { Synced = false };
            foreach (var l in plain.GetString()!.Split('\n')) lyrics.Lines.Add(new LyricLine(TimeSpan.Zero, l.Trim()));
            return lyrics;
        }
        return null;
    }
}
