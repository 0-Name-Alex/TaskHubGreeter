namespace TaskHubGreeter.Client.Tests;

public sealed class TodayTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private static TaskDto T(string title, DateTimeOffset? reminder = null, string status = "open") => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Status = status,
        NextReminderUtc = reminder,
    };

    [Fact]
    public void Splits_overdue_from_today_and_finds_next_future_reminder()
    {
        var late = T("Late", Now.AddMinutes(-30));
        var a = T("A", Now.AddHours(4).AddMinutes(30));
        var b = T("B", Now.AddHours(2));
        var done = T("Done", Now.AddMinutes(5), status: "completed");
        var future = T("Future", Now.AddDays(1));

        var snap = TodaySnapshot.Build([late, a, b, done], [late], [future, a], Now);

        Assert.Equal(new[] { late.Id }, snap.Overdue.Select(t => t.Id));
        Assert.Equal(new[] { a.Id, b.Id }, snap.DueToday.Select(t => t.Id)); // server order kept
        Assert.Equal(Now.AddHours(2), snap.NextReminderUtc);
        Assert.Equal("2 due today · 1 overdue · next reminder 12:00", snap.SummaryText(Now, Utc));
    }

    [Fact]
    public void Overdue_not_in_today_view_is_still_shown()
    {
        var late = T("Late");
        var snap = TodaySnapshot.Build([], [late], [], Now);
        Assert.Single(snap.Overdue);
        Assert.Equal("0 due today · 1 overdue", snap.SummaryText(Now, Utc));
    }

    [Fact]
    public void Empty_day_reads_nicely()
    {
        Assert.Equal("Nothing due today", TodaySnapshot.Empty.SummaryText(Now, Utc));
        var snap = TodaySnapshot.Build([], [], [T("Later", Now.AddDays(2))], Now);
        Assert.StartsWith("Nothing due today · next reminder ", snap.SummaryText(Now, Utc));
        Assert.EndsWith(" 10:00", snap.SummaryText(Now, Utc)); // "Mon 10:00"
    }

    [Theory]
    [InlineData(6, "Good morning")]
    [InlineData(11, "Good morning")]
    [InlineData(12, "Good afternoon")]
    [InlineData(17, "Good evening")]
    [InlineData(23, "Good evening")]
    [InlineData(3, "Hello")]
    public void Greeting_by_hour(int hour, string expected) => Assert.Equal(expected, Formatting.GreetingFor(hour));

    [Fact]
    public void Due_labels()
    {
        var today = new DateOnly(2026, 11, 7);
        Assert.Equal("09:00", Formatting.DueLabel(new TaskDto { DueDate = today, DueTime = "09:00" }, today));
        Assert.Equal("All day", Formatting.DueLabel(new TaskDto { DueDate = today }, today));
        Assert.Equal("Yesterday 18:00", Formatting.DueLabel(new TaskDto { DueDate = today.AddDays(-1), DueTime = "18:00" }, today));
        Assert.Equal("", Formatting.DueLabel(new TaskDto(), today));
    }

    [Fact]
    public void Priority_markers()
    {
        Assert.Equal("", Formatting.PriorityMarker("none"));
        Assert.Equal("!!!", Formatting.PriorityMarker("high"));
        Assert.Equal("!!!!", Formatting.PriorityMarker("URGENT"));
        Assert.Equal("", Formatting.PriorityMarker(null));
    }
}
