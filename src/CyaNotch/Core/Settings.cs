using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyaNotch.Core;

public enum IslandShape { Floating, Notch }

public enum GameMode { Strip, Hide, Off }

/// <summary>Bars: visualizer fills the pill. BarsWithTitle: title over dimmed bars. Off: artwork + title only.</summary>
public enum VisualizerStyle { Bars, BarsWithTitle, Off }

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
    public double DistanceFromTop { get; set; } = 8;
    /// <summary>1.0 = normal. Lower is faster.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    public double CollapsedWidth { get; set; } = 200;
    public double CollapsedHeight { get; set; } = 34;
    /// <summary>What the island does over fullscreen games and videos.</summary>
    public GameMode FullscreenMode { get; set; } = GameMode.Strip;
    public VisualizerStyle VisualizerStyle { get; set; } = VisualizerStyle.Bars;
    public bool VisualizerMirrored { get; set; } = true;
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
}

/// <summary>Tiny JSON file store under %AppData%\CyaNotch.</summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
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
