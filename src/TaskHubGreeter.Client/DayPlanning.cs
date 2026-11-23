using System.Globalization;

namespace TaskHubGreeter.Client;

/// <summary>Morning = plan the day, Evening = wrap it up.</summary>
public enum DayMode
{
    Morning,
    Day,
    Evening,
}

public sealed record DayProgress(int Done, int Remaining)
{
    public int Total => Done + Remaining;

    /// <summary>0..1; 0 when there was nothing to do.</summary>
    public double Fraction => Total == 0 ? 0 : (double)Done / Total;

    public string Text => Total == 0 ? "Nothing planned" : $"{Done} of {Total} done";
}

public sealed record UpcomingDay(DateOnly Date, string Label, IReadOnlyList<TaskDto> Tasks);

public static class DayPlanning
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    /// <summary>5–11 morning, 12–16 day, 17–4 evening.</summary>
    public static DayMode ModeFor(int hour) => hour switch
    {
        >= 5 and < 12 => DayMode.Morning,
        >= 12 and < 17 => DayMode.Day,
        _ => DayMode.Evening,
    };

    public static DateOnly Tomorrow(DateOnly today) => today.AddDays(1);

    /// <summary>The next Monday (a full week ahead if today is Monday).</summary>
    public static DateOnly NextWeek(DateOnly today)
    {
        var days = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }

    /// <summary>"Today", "Tomorrow", "Mon 9 Nov".</summary>
    public static string DayLabel(DateOnly date, DateOnly today) =>
        date == today ? "Today" : date == today.AddDays(1) ? "Tomorrow" : date.ToString("ddd d MMM", Culture);

    /// <summary>Tasks completed today, by their local completion date.</summary>
    public static int CountCompletedToday(IEnumerable<TaskDto> completed, DateOnly today, TimeZoneInfo zone) =>
        completed.Count(t => t.IsCompleted && t.CompletedUtc is { } c
                             && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(c, zone).DateTime) == today);

    public static DayProgress Progress(TodaySnapshot snapshot, int completedToday) =>
        new(completedToday, snapshot.Overdue.Count + snapshot.DueToday.Count);

    /// <summary>Open, non-repeating tasks from before today — what the morning "move to today" acts on.</summary>
    public static IReadOnlyList<TaskDto> CarryOverCandidates(TodaySnapshot snapshot, DateOnly today) =>
        snapshot.Overdue.Where(t => t.IsOpen && !t.IsRecurring && t.DueDate is { } d && d < today).ToList();

    /// <summary>Open, non-repeating tasks still on today's list — what the evening "move to tomorrow" acts on.</summary>
    public static IReadOnlyList<TaskDto> WrapUpCandidates(TodaySnapshot snapshot) =>
        snapshot.Overdue.Concat(snapshot.DueToday).Where(t => t.IsOpen && !t.IsRecurring).ToList();

    /// <summary>Upcoming tasks for the next <paramref name="days"/> days, grouped by day.</summary>
    public static IReadOnlyList<UpcomingDay> UpcomingByDay(IEnumerable<TaskDto> upcoming, DateOnly today, int days)
    {
        if (days <= 0) return [];
        var last = today.AddDays(days);
        return upcoming
            .Where(t => t.IsOpen && t.DueDate is { } d && d > today && d <= last)
            .GroupBy(t => t.DueDate!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new UpcomingDay(g.Key, DayLabel(g.Key, today),
                g.OrderBy(t => t.DueTime ?? "99:99", StringComparer.Ordinal).ThenByDescending(t => PriorityNames.Rank(t.Priority)).ToList()))
            .ToList();
    }

    /// <summary>"Good morning, Dhaya. You have 3 tasks today and 1 overdue. First meeting at 10:00."</summary>
    public static string SpokenSummary(string greeting, TodaySnapshot snapshot, string? nextMeeting)
    {
        var today = snapshot.DueToday.Count;
        var overdue = snapshot.Overdue.Count;
        var sentence = (today, overdue) switch
        {
            (0, 0) => "Nothing is due today.",
            (_, 0) => $"You have {Formatting.Plural(today, "task", "tasks")} today.",
            (0, _) => $"You have {Formatting.Plural(overdue, "overdue task", "overdue tasks")}.",
            _ => $"You have {Formatting.Plural(today, "task", "tasks")} today and {overdue} overdue.",
        };
        var text = $"{greeting}. {sentence}";
        if (!string.IsNullOrEmpty(nextMeeting)) text += $" {nextMeeting}.";
        return text;
    }
}

/// <summary>
/// Detects "you're back": the PC was idle (no keyboard/mouse) for at least the threshold and input
/// has just resumed, without the session being locked (unlock has its own trigger).
/// </summary>
public sealed class IdleReturnDetector(TimeSpan threshold)
{
    private static readonly TimeSpan ActiveAgain = TimeSpan.FromSeconds(5);
    private bool _away;

    public TimeSpan Threshold { get; set; } = threshold;

    /// <summary>Feed the current idle time periodically. Returns true once when the user comes back.</summary>
    public bool Update(TimeSpan idle, bool locked)
    {
        if (locked || Threshold <= TimeSpan.Zero)
        {
            _away = false;
            return false;
        }
        if (idle >= Threshold)
        {
            _away = true;
            return false;
        }
        if (_away && idle < ActiveAgain)
        {
            _away = false;
            return true;
        }
        return false;
    }
}
