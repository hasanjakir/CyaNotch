using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CyaNotch.Core;

namespace CyaNotch.Services;

public sealed record Weather(double Temp, double High, double Low, int Code, string Place)
{
    public string Description => Code switch
    {
        0 => "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        >= 95 => "Thunderstorm",
        _ => "",
    };

    /// <summary>Segoe Fluent / MDL2 glyph.</summary>
    public string Glyph => Code switch
    {
        0 => "",
        1 or 2 => "",
        3 or 45 or 48 => "",
        >= 71 and <= 77 or 85 or 86 => "",
        >= 95 => "",
        _ => "",
    };
}

/// <summary>
/// Weather from Open-Meteo (free, no API key). Only contacts the network once a city is set in Settings.
/// </summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public Weather? Current { get; private set; }
    public event Action<Weather>? Updated;

    static WeatherService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("CyaNotch-desktop/0.1");
    }

    /// <summary>Looks up a city name and stores its coordinates in settings.</summary>
    public static async Task<string?> GeocodeAsync(string city)
    {
        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var r = results[0];
            Settings.Current.WeatherLat = r.GetProperty("latitude").GetDouble();
            Settings.Current.WeatherLon = r.GetProperty("longitude").GetDouble();
            var name = r.GetProperty("name").GetString() ?? city;
            if (r.TryGetProperty("country_code", out var cc)) name += $", {cc.GetString()}";
            Settings.Current.WeatherCity = name;
            return name;
        }
        catch (Exception ex)
        {
            Log.Error("Geocoding failed", ex);
            return null;
        }
    }

    public async Task RefreshAsync()
    {
        var s = Settings.Current;
        if (s.WeatherLat == null || s.WeatherLon == null) return;
        try
        {
            var unit = s.UseFahrenheit ? "&temperature_unit=fahrenheit" : "";
            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto{2}",
                s.WeatherLat, s.WeatherLon, unit);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            Current = new Weather(
                cur.GetProperty("temperature_2m").GetDouble(),
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                cur.GetProperty("weather_code").GetInt32(),
                s.WeatherCity);
            Updated?.Invoke(Current);
        }
        catch (Exception ex)
        {
            Log.Error("Weather refresh failed", ex);
        }
    }
}
