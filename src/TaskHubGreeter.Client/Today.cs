using System.Globalization;

namespace TaskHubGreeter.Client;

/// <summary>What the Greeter shows: overdue, due today, and the next reminder.</summary>
public sealed class TodaySnapshot
{
    private TodaySnapshot(IReadOnlyList<TaskDto> overdue, IReadOnlyList<TaskDto> dueToday, DateTimeOffset? nextReminderUtc)
    {
        Overdue = overdue;
        DueToday = dueToday;
        NextReminderUtc = nextReminderUtc;
    }

    public IReadOnlyList<TaskDto> Overdue { get; }
    public IReadOnlyList<TaskDto> DueToday { get; }
    public DateTimeOffset? NextReminderUtc { get; }

    public static TodaySnapshot Empty { get; } = new([], [], null);

    /// <summary>
    /// Splits TaskHub's <c>today</c> view (which already includes overdue, sorted) using the
    /// <c>overdue</c> view, so "overdue" means exactly what TaskHub means by it. The next reminder is
    /// the smallest future <c>nextReminderUtc</c> among open today/upcoming tasks.
    /// </summary>
    public static TodaySnapshot Build(IReadOnlyList<TaskDto> today, IReadOnlyList<TaskDto> overdue,
        IReadOnlyList<TaskDto> upcoming, DateTimeOffset now)
    {
        var overdueIds = overdue.Where(t => t.IsOpen).Select(t => t.Id).ToHashSet();
        var overdueList = today.Where(t => t.IsOpen && overdueIds.Contains(t.Id)).ToList();
        // Anything overdue that the today view didn't include (shouldn't happen, but don't lose it).
        overdueList.AddRange(overdue.Where(t => t.IsOpen && overdueList.All(o => o.Id != t.Id)));
        var dueToday = today.Where(t => t.IsOpen && !overdueIds.Contains(t.Id)).ToList();

        var seen = new HashSet<Guid>();
        DateTimeOffset? next = null;
        foreach (var t in today.Concat(upcoming))
        {
            if (!seen.Add(t.Id) || !t.IsOpen || t.NextReminderUtc is not { } r || r < now) continue;
            if (next is null || r < next) next = r;
        }
        return new TodaySnapshot(overdueList, dueToday, next);
    }

    /// <summary>e.g. "3 due today · 1 overdue · next reminder 14:30".</summary>
    public string SummaryText(DateTimeOffset now, TimeZoneInfo zone)
    {
        var parts = new List<string>();
        if (DueToday.Count == 0 && Overdue.Count == 0) parts.Add("Nothing due today");
        else parts.Add($"{DueToday.Count} due today");
        if (Overdue.Count > 0) parts.Add($"{Overdue.Count} overdue");
        if (NextReminderUtc is { } r) parts.Add("next reminder " + Formatting.ReminderTime(r, now, zone));
        return string.Join(" · ", parts);
    }
}

public static class Formatting
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    /// <summary>"Good morning" / "Good afternoon" / "Good evening" / "Hello".</summary>
    public static string GreetingFor(int hour) => hour switch
    {
        >= 5 and < 12 => "Good morning",
        >= 12 and < 17 => "Good afternoon",
        >= 17 => "Good evening",
        _ => "Hello",
    };

    /// <summary>"Saturday, 7 November".</summary>
    public static string LongDate(DateTime local) => local.ToString("dddd, d MMMM", Culture);

    /// <summary>"14:30" if today, "Mon 09:00" otherwise.</summary>
    public static string ReminderTime(DateTimeOffset utc, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return local.Date == today ? local.ToString("HH:mm", Culture) : local.ToString("ddd HH:mm", Culture);
    }

    /// <summary>Due label for a row: "09:00", "All day", "Yesterday", "Yesterday 18:00", "Tue 3 Nov", …</summary>
    public static string DueLabel(TaskDto task, DateOnly today)
    {
        if (task.DueDate is not { } date) return "";
        var time = string.IsNullOrEmpty(task.DueTime) ? null : task.DueTime;
        string day;
        if (date == today) return time ?? "All day";
        if (date == today.AddDays(-1)) day = "Yesterday";
        else if (date == today.AddDays(1)) day = "Tomorrow";
        else if (date.Year == today.Year) day = date.ToString("ddd d MMM", Culture);
        else day = date.ToString("d MMM yyyy", Culture);
        return time is null ? day : $"{day} {time}";
    }

    /// <summary>"!" markers for the priority (none → "", urgent → "!!!!").</summary>
    public static string PriorityMarker(string? priority) => new('!', PriorityNames.Rank(priority));

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
