using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyaNotch.Core;

public enum IslandShape { Floating, Notch }

public enum GameMode { Strip, Hide, Off }

/// <summary>Bars: visualizer fills the pill. BarsWithTitle: title over dimmed bars. Off: artwork + title only.</summary>
/// <summary>Look of the music progress line.</summary>
public enum ProgressStyle { Wave, Line, Bar, Dots, Glow }

/// <summary>What the resting pill shows when no music is playing.</summary>
public enum RestingInfo { Time, Date, Weather, Battery, Cpu, Temperature }
public enum IslandSide { Centre, Left, Right }
/// <summary>Which apps can take over the music card.</summary>
public enum MediaFilter { All, MusicApps, AllExcept }

public enum VisualizerStyle { Bars, BarsWithTitle, Off, Dots }

public sealed class WebSite
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class DeckApp
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class WidgetPin
{
    public string Kind { get; set; } = "Clock";
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 120;
}

public sealed class WorldClock
{
    public string Label { get; set; } = "";
    public string TimeZoneId { get; set; } = "";
}

/// <summary>User settings, saved as JSON in %AppData%\CyaNotch\settings.json.</summary>
public sealed class Settings
{
    public IslandShape Shape { get; set; } = IslandShape.Floating;
    public string Theme { get; set; } = "Glass";
    /// <summary>Accent name from <see cref="Themes.Accents"/>, or "Theme" for the theme's own colour.</summary>
    public string Accent { get; set; } = "Theme";
    /// <summary>0..1: how solid see-through surfaces (Glass, Acrylic, Candy glass) are.</summary>
    public double GlassTint { get; set; } = 0.44;
    /// <summary>Keep the island out of screenshots, screen recordings and screen sharing.</summary>
    public bool HideFromCapture { get; set; }
    /// <summary>Tab the island reopens on: "Last" (the one used last) or "Music".</summary>
    public string OpenOn { get; set; } = "Last";
    public string LastTab { get; set; } = "TabHome";
    public ProgressStyle ProgressStyle { get; set; } = ProgressStyle.Wave;
    public RestingInfo RestingInfo { get; set; } = RestingInfo.Time;
    public IslandSide Side { get; set; } = IslandSide.Centre;
    /// <summary>Index into Windows' screen list; -1 = main screen.</summary>
    public int Monitor { get; set; } = -1;
    /// <summary>Delay before the island opens under the pointer, in milliseconds.</summary>
    public int OpenDelayMs { get; set; } = 140;
    /// <summary>0..1: square to fully round corners (island, tiles and buttons follow).</summary>
    public double Roundness { get; set; } = 1.0;
    /// <summary>How long a notification stays on the closed island, in seconds.</summary>
    public int PeekSeconds { get; set; } = 4;
    public MediaFilter MediaFilter { get; set; } = MediaFilter.All;
    /// <summary>App names skipped when <see cref="MediaFilter"/> is AllExcept (e.g. "Chrome").</summary>
    public List<string> MediaIgnoredApps { get; set; } = new();
    /// <summary>Apps on the Launcher tab. Ctrl+Alt+1..9 open the first nine.</summary>
    public List<DeckApp> LauncherApps { get; set; } = new();
    public bool LauncherHotkeys { get; set; } = true;
    /// <summary>Sites pinned in the web drawer.</summary>
    public List<WebSite> WebSites { get; set; } = new()
    {
        new WebSite { Name = "YouTube", Url = "https://www.youtube.com/" },
        new WebSite { Name = "Wikipedia", Url = "https://en.wikipedia.org/" },
    };
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    /// <summary>Hold back notification pop-ups during a focus session.</summary>
    public bool FocusSilence { get; set; } = true;
    /// <summary>Show a REC / LIVE pill while OBS records or streams (needs OBS's WebSocket server on).</summary>
    public bool ObsEnabled { get; set; }
    public int ObsPort { get; set; } = 4455;
    /// <summary>OBS WebSocket password. Stored protected for this Windows account.</summary>
    [JsonIgnore] public string ObsPassword
    {
        get => Secret.Unprotect(ObsPasswordProtected);
        set => ObsPasswordProtected = Secret.Protect(value);
    }
    public string ObsPasswordProtected { get; set; } = "";
    /// <summary>"twitch" or "youtube".</summary>
    public string ChatPlatform { get; set; } = "twitch";
    /// <summary>Twitch channel name, or YouTube live video id / link.</summary>
    public string ChatChannel { get; set; } = "";
    public double ChatLeft { get; set; } = double.NaN;
    public double ChatTop { get; set; } = double.NaN;
    public double ChatWidth { get; set; } = 360;
    public double ChatHeight { get; set; } = 560;
    /// <summary>Put new screenshots on the file shelf too.</summary>
    public bool CapturesOnShelf { get; set; } = true;
    /// <summary>Watch Claude Code / Codex sessions and accept agent reports on this PC.</summary>
    public bool AgentsEnabled { get; set; } = true;
    /// <summary>Replace Windows' taskbar with CyaNotch's bar along the bottom (main screen).</summary>
    public bool TaskbarEnabled { get; set; }
    /// <summary>Show every tray icon's app, or keep them behind the arrow.</summary>
    public bool TaskbarHideWindows { get; set; } = true;
    // Bookkeeping so Windows' taskbar is always restored, even after a crash.
    public bool TaskbarHiddenByUs { get; set; }
    public bool TaskbarWasAutoHide { get; set; }
    public double DistanceFromTop { get; set; } = 8;
    /// <summary>1.0 = normal. Lower is faster.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    public double CollapsedWidth { get; set; } = 200;
    public double CollapsedHeight { get; set; } = 34;
    /// <summary>What the island does over fullscreen games and videos.</summary>
    public GameMode FullscreenMode { get; set; } = GameMode.Strip;
    public VisualizerStyle VisualizerStyle { get; set; } = VisualizerStyle.Bars;
    /// <summary>Tint the visualizer and progress line with a colour from the song's artwork.</summary>
    public bool ArtworkColors { get; set; } = true;
    /// <summary>Show a soft blur of the artwork behind the pill and the music card.</summary>
    public bool AmbientArtwork { get; set; } = true;
    public bool VisualizerMirrored { get; set; } = true;
    /// <summary>Hide the island until the mouse touches the top edge.</summary>
    public bool AutoHide { get; set; } = true;
    /// <summary>With auto-hide on, keep the pill out while music is playing (for the visualizer).</summary>
    public bool ShowWhilePlaying { get; set; } = true;
    /// <summary>Pop up a quick time / CPU / GPU / RAM / temperature glance every few minutes.</summary>
    public bool StatsGlance { get; set; } = true;
    public int GlanceMinMinutes { get; set; } = 3;
    public int GlanceMaxMinutes { get; set; } = 8;
    public bool LyricsEnabled { get; set; } = false;
    public bool LyricsInPill { get; set; } = false;
    public bool DeckEnabled { get; set; } = false;
    public string? DeckKey { get; set; }
    public List<string> ApprovedPhones { get; set; } = new();
    public List<DeckApp> DeckApps { get; set; } = new()
    {
        new DeckApp { Name = "File Explorer", Path = "explorer.exe" },
        new DeckApp { Name = "Calculator", Path = "calc.exe" },
        new DeckApp { Name = "Task Manager", Path = "taskmgr.exe" },
    };
    public List<WidgetPin> PinnedWidgets { get; set; } = new();
    public bool StartWithWindows { get; set; } = false;
    public bool MicMuteHotkey { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public int ClipboardLimit { get; set; } = 200;
    public string WeatherCity { get; set; } = "";
    public double? WeatherLat { get; set; }
    public double? WeatherLon { get; set; }
    public bool UseFahrenheit { get; set; } = false;
    public bool Use24Hour { get; set; } = false;
    public List<WorldClock> WorldClocks { get; set; } = new()
    {
        new WorldClock { Label = "London", TimeZoneId = "GMT Standard Time" },
        new WorldClock { Label = "New York", TimeZoneId = "Eastern Standard Time" },
        new WorldClock { Label = "Tokyo", TimeZoneId = "Tokyo Standard Time" },
    };

    [JsonIgnore] public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyaNotch");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Load()
    {
        Current = Storage.Load<Settings>("settings.json") ?? new Settings();
    }

    public static void Save()
    {
        Storage.Save("settings.json", Current);
        Changed?.Invoke();
    }

    /// <summary>Saves without telling the app anything changed (for small bookkeeping like the last tab).</summary>
    public static void SaveQuietly() => Storage.Save("settings.json", Current);
}

/// <summary>Encrypts small secrets for the current Windows account (DPAPI).</summary>
public static class Secret
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            var data = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(data);
        }
        catch { return ""; }
    }
}

/// <summary>Tiny JSON file store under %AppData%\CyaNotch.</summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Load<T>(string name) where T : class
    {
        try
        {
            var path = Path.Combine(Settings.Folder, name);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {name}", ex);
            return null;
        }
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            var path = Path.Combine(Settings.Folder, name);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save {name}", ex);
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                var path = Path.Combine(Settings.Folder, "cyanotch.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
