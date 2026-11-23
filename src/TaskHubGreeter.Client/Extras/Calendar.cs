using Ical.Net.CalendarComponents;

namespace TaskHubGreeter.Client.Extras;

public sealed record CalendarItem(string Title, DateTime Start, DateTime? End, bool IsAllDay, string? Location)
{
    /// <summary>"10:00–10:30", "10:00" or "All day".</summary>
    public string TimeLabel => IsAllDay ? "All day" : End is { } e && e > Start ? $"{Start:HH:mm}–{e:HH:mm}" : $"{Start:HH:mm}";

    public bool IsNow(DateTime nowLocal) => !IsAllDay && Start <= nowLocal && (End ?? Start.AddMinutes(30)) > nowLocal;
    public bool IsPast(DateTime nowLocal) => !IsAllDay && (End ?? Start.AddMinutes(30)) <= nowLocal;
}

/// <summary>
/// Today's meetings from one or more iCalendar (.ics) feeds — e.g. Google Calendar's "secret
/// address in iCal format" or Outlook's published calendar link. Recurring events are expanded.
/// </summary>
public sealed class CalendarFeed(HttpClient http)
{
    public async Task<IReadOnlyList<CalendarItem>> GetDayAsync(IEnumerable<string> urls, DateOnly day, CancellationToken ct = default)
    {
        var items = new List<CalendarItem>();
        foreach (var raw in urls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            var url = raw.Trim();
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url["webcal://".Length..];
            var text = await http.GetStringAsync(new Uri(url), ct).ConfigureAwait(false);
            items.AddRange(ParseDay(text, day));
        }
        return Sort(items);
    }

    /// <summary>Events that happen on <paramref name="day"/> (local time), all-day ones first.</summary>
    public static IReadOnlyList<CalendarItem> ParseDay(string icsText, DateOnly day)
    {
        var calendar = Ical.Net.Calendar.Load(icsText);
        if (calendar is null) return [];
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);

        var items = new List<CalendarItem>();
        var seen = new HashSet<(string, DateTime)>();
        foreach (var occurrence in calendar.GetOccurrences(start.AddDays(-1), end))
        {
            if (occurrence.Source is not CalendarEvent evt) continue;
            if (string.Equals(evt.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;

            var allDay = evt.IsAllDay;
            var s = occurrence.Period.StartTime.AsSystemLocal;
            DateTime? e = occurrence.Period.EndTime?.AsSystemLocal;
            if (e is null && evt.Duration > TimeSpan.Zero) e = s + evt.Duration;

            bool onDay = allDay
                ? s.Date <= start && (e ?? s.AddDays(1)) > start
                : s < end && (e ?? s) > start || s.Date == start;
            if (!onDay) continue;

            var title = string.IsNullOrWhiteSpace(evt.Summary) ? "(no title)" : evt.Summary.Trim();
            if (!seen.Add((title, s))) continue;
            items.Add(new CalendarItem(title, s, e, allDay, string.IsNullOrWhiteSpace(evt.Location) ? null : evt.Location.Trim()));
        }
        return Sort(items);
    }

    /// <summary>"Next meeting at 14:00: Design review" for the spoken greeting, or null.</summary>
    public static string? NextMeetingSentence(IEnumerable<CalendarItem> items, DateTime nowLocal)
    {
        var next = items.Where(i => !i.IsAllDay && i.Start >= nowLocal).OrderBy(i => i.Start).FirstOrDefault();
        return next is null ? null : $"Next meeting at {next.Start:HH:mm}: {next.Title}";
    }

    private static List<CalendarItem> Sort(IEnumerable<CalendarItem> items) =>
        items.OrderByDescending(i => i.IsAllDay).ThenBy(i => i.Start).ThenBy(i => i.Title, StringComparer.CurrentCulture).ToList();
}
