using System.Net.Http;
using TaskHubGreeter.Client.Extras;

namespace TaskHubGreeter.App;

/// <summary>
/// Weather and calendar for the window: fetched over the internet (system proxy), cached
/// (weather 30 min, calendar 10 min), and never allowed to slow down or break the task list.
/// </summary>
public sealed class ExtrasService : IDisposable
{
    private readonly HttpClient _http;
    private readonly WeatherClient _weather;
    private readonly CalendarFeed _calendar;
    private (string Key, GeoLocation? Location) _geo;
    private (string Key, DateTime At, WeatherReport? Report) _weatherCache;
    private (string Key, DateOnly Day, DateTime At, IReadOnlyList<CalendarItem> Items) _calendarCache;

    public ExtrasService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskHubGreeter/0.1");
        _weather = new WeatherClient(_http);
        _calendar = new CalendarFeed(_http);
    }

    public string? WeatherError { get; private set; }
    public string? CalendarError { get; private set; }

    public async Task<WeatherReport?> GetWeatherAsync(WeatherSettings s)
    {
        if (!s.IsConfigured) return null;
        var key = $"{s.City}|{s.Latitude}|{s.Longitude}|{s.Unit}";
        if (_weatherCache.Key == key && DateTime.UtcNow - _weatherCache.At < TimeSpan.FromMinutes(30)) return _weatherCache.Report;
        try
        {
            double lat, lon;
            if (s.Latitude is { } la && s.Longitude is { } lo)
            {
                (lat, lon) = (la, lo);
            }
            else
            {
                if (_geo.Key != s.City) _geo = (s.City, await _weather.GeocodeAsync(s.City));
                if (_geo.Location is null)
                {
                    WeatherError = $"Weather: couldn't find \"{s.City}\"";
                    return null;
                }
                (lat, lon) = (_geo.Location.Latitude, _geo.Location.Longitude);
            }
            var fahrenheit = s.Unit.StartsWith("f", StringComparison.OrdinalIgnoreCase);
            var report = await _weather.GetTodayAsync(lat, lon, fahrenheit);
            _weatherCache = (key, DateTime.UtcNow, report);
            WeatherError = null;
            return report;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            WeatherError = "Weather unavailable";
            Log.Warn("Weather failed", ex);
            return _weatherCache.Key == key ? _weatherCache.Report : null;
        }
    }

    public async Task<IReadOnlyList<CalendarItem>> GetMeetingsAsync(CalendarSettings s, DateOnly day)
    {
        var urls = s.IcsUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0) return [];
        var key = string.Join("|", urls);
        if (_calendarCache.Key == key && _calendarCache.Day == day && DateTime.UtcNow - _calendarCache.At < TimeSpan.FromMinutes(10))
            return _calendarCache.Items;
        try
        {
            var items = await _calendar.GetDayAsync(urls, day);
            _calendarCache = (key, day, DateTime.UtcNow, items);
            CalendarError = null;
            return items;
        }
        catch (Exception ex)
        {
            // Ical.Net throws a variety of exception types on malformed feeds.
            CalendarError = "Calendar unavailable";
            Log.Warn("Calendar failed", ex);
            return _calendarCache.Key == key && _calendarCache.Day == day ? _calendarCache.Items : [];
        }
    }

    public void Invalidate()
    {
        _weatherCache = default;
        _calendarCache = default;
    }

    public void Dispose() => _http.Dispose();
}
