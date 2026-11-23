using System.Globalization;
using System.Text.Json;

namespace TaskHubGreeter.Client.Extras;

public sealed record GeoLocation(string Name, double Latitude, double Longitude, string? Country);

public sealed record WeatherReport(double Current, int Code, double High, double Low, int? RainChance, string Unit)
{
    public string Description => WeatherCodes.Describe(Code);

    /// <summary>Segoe Fluent Icons glyph for the condition.</summary>
    public string Glyph => WeatherCodes.Glyph(Code);

    /// <summary>"28° Partly cloudy · H 31° L 24° · 20% rain".</summary>
    public string Line
    {
        get
        {
            var line = $"{Round(Current)}° {Description} · H {Round(High)}° L {Round(Low)}°";
            if (RainChance is { } r && r >= 10) line += $" · {r}% rain";
            return line;
        }
    }

    private static string Round(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Today's weather from Open-Meteo (free, no API key). The city is turned into coordinates once
/// with Open-Meteo's geocoder.
/// </summary>
public sealed class WeatherClient(HttpClient http)
{
    public async Task<GeoLocation?> GeocodeAsync(string city, CancellationToken ct = default)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city.Trim())}&count=1&language=en&format=json";
        var json = await http.GetStringAsync(new Uri(url), ct).ConfigureAwait(false);
        return ParseGeocode(json);
    }

    public async Task<WeatherReport?> GetTodayAsync(double latitude, double longitude, bool fahrenheit, CancellationToken ct = default)
    {
        var lat = latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.####", CultureInfo.InvariantCulture);
        var url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat + "&longitude=" + lon
                  + "&current=temperature_2m,weather_code"
                  + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
                  + "&timezone=auto&forecast_days=1"
                  + (fahrenheit ? "&temperature_unit=fahrenheit" : "");
        var json = await http.GetStringAsync(new Uri(url), ct).ConfigureAwait(false);
        return ParseForecast(json, fahrenheit ? "F" : "C");
    }

    internal static GeoLocation? ParseGeocode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return null;
        var r = results[0];
        return new GeoLocation(
            r.GetProperty("name").GetString() ?? "",
            r.GetProperty("latitude").GetDouble(),
            r.GetProperty("longitude").GetDouble(),
            r.TryGetProperty("country", out var c) ? c.GetString() : null);
    }

    internal static WeatherReport? ParseForecast(string json, string unit)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("current", out var current) || !root.TryGetProperty("daily", out var daily)) return null;

        static double First(JsonElement daily, string name) =>
            daily.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 && a[0].ValueKind == JsonValueKind.Number
                ? a[0].GetDouble()
                : double.NaN;

        var temp = current.GetProperty("temperature_2m").GetDouble();
        var code = current.TryGetProperty("weather_code", out var wc) && wc.ValueKind == JsonValueKind.Number ? wc.GetInt32() : (int)First(daily, "weather_code");
        var rain = First(daily, "precipitation_probability_max");
        var high = First(daily, "temperature_2m_max");
        var low = First(daily, "temperature_2m_min");
        return new WeatherReport(temp, code, double.IsNaN(high) ? temp : high, double.IsNaN(low) ? temp : low,
            double.IsNaN(rain) ? null : (int)Math.Round(rain), unit);
    }
}

/// <summary>WMO weather interpretation codes (as used by Open-Meteo).</summary>
public static class WeatherCodes
{
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "—",
    };

    /// <summary>Segoe Fluent Icons / MDL2 weather glyphs.</summary>
    public static string Glyph(int code) => code switch
    {
        0 or 1 => "",                 // Sunny
        2 => "",                      // PartlyCloudyDay
        3 or 45 or 48 => "",          // Cloudy
        >= 51 and <= 67 or >= 80 and <= 82 => "", // Rain
        >= 71 and <= 77 or 85 or 86 => "",        // Snow
        >= 95 => "",                  // Thunderstorm
        _ => "",
    };
}
