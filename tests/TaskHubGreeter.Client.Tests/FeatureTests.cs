using TaskHubGreeter.Client.Extras;

namespace TaskHubGreeter.Client.Tests;

public sealed class DayPlanningTests
{
    private static readonly DateOnly Sat = new(2026, 11, 7);

    private static TaskDto T(string title, DateOnly? due = null, string? time = null, string? rrule = null, string status = "open", DateTimeOffset? completed = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        DueDate = due,
        DueTime = time,
        Recurrence = rrule,
        Status = status,
        CompletedUtc = completed,
    };

    [Theory]
    [InlineData(7, DayMode.Morning)]
    [InlineData(13, DayMode.Day)]
    [InlineData(19, DayMode.Evening)]
    [InlineData(2, DayMode.Evening)]
    public void Day_modes(int hour, DayMode mode) => Assert.Equal(mode, DayPlanning.ModeFor(hour));

    [Fact]
    public void Reschedule_dates()
    {
        Assert.Equal(new DateOnly(2026, 11, 8), DayPlanning.Tomorrow(Sat));
        Assert.Equal(new DateOnly(2026, 11, 9), DayPlanning.NextWeek(Sat));                 // Sat → Mon
        Assert.Equal(new DateOnly(2026, 11, 16), DayPlanning.NextWeek(new DateOnly(2026, 11, 9))); // Mon → next Mon
        Assert.Equal("Tomorrow", DayPlanning.DayLabel(Sat.AddDays(1), Sat));
    }

    [Fact]
    public void Progress_counts_completed_today_against_what_is_left()
    {
        var utc = TimeZoneInfo.Utc;
        var done = new[]
        {
            T("a", status: "completed", completed: new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero)),
            T("b", status: "completed", completed: new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)), // yesterday
        };
        var snap = TodaySnapshot.Build([T("x", Sat), T("y", Sat)], [], [], DateTimeOffset.UtcNow);

        var progress = DayPlanning.Progress(snap, DayPlanning.CountCompletedToday(done, Sat, utc));

        Assert.Equal(1, progress.Done);
        Assert.Equal(3, progress.Total);
        Assert.Equal("1 of 3 done", progress.Text);
        Assert.Equal(1 / 3.0, progress.Fraction, 3);
        Assert.Equal("Nothing planned", new DayProgress(0, 0).Text);
    }

    [Fact]
    public void Carry_over_and_wrap_up_skip_repeating_tasks()
    {
        var old = T("old", Sat.AddDays(-2));
        var repeating = T("gym", Sat.AddDays(-1), rrule: "FREQ=DAILY");
        var laterToday = T("later", Sat, "18:00");
        var snap = TodaySnapshot.Build([old, repeating, laterToday], [old, repeating], [], DateTimeOffset.UtcNow);

        Assert.Equal(new[] { old.Id }, DayPlanning.CarryOverCandidates(snap, Sat).Select(t => t.Id));
        Assert.Equal(new[] { old.Id, laterToday.Id }, DayPlanning.WrapUpCandidates(snap).Select(t => t.Id));
    }

    [Fact]
    public void Upcoming_is_grouped_by_day_within_the_window()
    {
        var tomorrow9 = T("t9", Sat.AddDays(1), "09:00");
        var tomorrowAllDay = T("tAll", Sat.AddDays(1));
        var monday = T("mon", Sat.AddDays(2));
        var farAway = T("far", Sat.AddDays(10));

        var days = DayPlanning.UpcomingByDay([farAway, tomorrowAllDay, monday, tomorrow9], Sat, 3);

        Assert.Equal(2, days.Count);
        Assert.Equal("Tomorrow", days[0].Label);
        Assert.Equal(new[] { "t9", "tAll" }, days[0].Tasks.Select(t => t.Title));
        Assert.Equal("mon", Assert.Single(days[1].Tasks).Title);
        Assert.Empty(DayPlanning.UpcomingByDay([tomorrow9], Sat, 0));
    }

    [Fact]
    public void Spoken_summary()
    {
        var snap = TodaySnapshot.Build([T("a", Sat), T("b", Sat), T("late", Sat.AddDays(-1))], [T("late", Sat.AddDays(-1))], [], DateTimeOffset.UtcNow);
        // "late" in the overdue view has a different id than the one in today, so it's appended as overdue.
        var text = DayPlanning.SpokenSummary("Good morning, Dhaya", snap, "Next meeting at 10:00: Standup");
        Assert.StartsWith("Good morning, Dhaya. You have ", text);
        Assert.EndsWith("Next meeting at 10:00: Standup.", text);
        Assert.Equal("Good evening. Nothing is due today.", DayPlanning.SpokenSummary("Good evening", TodaySnapshot.Empty, null));
    }

    [Fact]
    public void Idle_return_fires_once_after_a_long_idle()
    {
        var d = new IdleReturnDetector(TimeSpan.FromMinutes(15));
        Assert.False(d.Update(TimeSpan.FromMinutes(3), locked: false));
        Assert.False(d.Update(TimeSpan.FromMinutes(16), locked: false)); // away
        Assert.True(d.Update(TimeSpan.FromSeconds(1), locked: false));   // back
        Assert.False(d.Update(TimeSpan.FromSeconds(1), locked: false));  // only once

        Assert.False(d.Update(TimeSpan.FromMinutes(20), locked: false));
        Assert.False(d.Update(TimeSpan.FromMinutes(21), locked: true));  // locked: unlock trigger handles it
        Assert.False(d.Update(TimeSpan.FromSeconds(1), locked: false));
    }
}

public sealed class ExtrasTests
{
    [Fact]
    public void Quote_of_the_day_is_stable_per_day_and_rotates()
    {
        var day = new DateOnly(2026, 11, 7);
        Assert.Equal(QuoteOfTheDay.For(day), QuoteOfTheDay.For(day));
        Assert.NotEqual(QuoteOfTheDay.For(day), QuoteOfTheDay.For(day.AddDays(1)));
        Assert.All(QuoteOfTheDay.All, q => Assert.False(string.IsNullOrWhiteSpace(q.Text)));
    }

    [Fact]
    public void Parses_open_meteo_forecast_and_geocoding()
    {
        const string forecast = """
            {"latitude":13.08,"longitude":80.27,"current":{"time":"2026-11-07T22:30","temperature_2m":27.6,"weather_code":2},
             "daily":{"time":["2026-11-07"],"weather_code":[61],"temperature_2m_max":[31.2],"temperature_2m_min":[24.4],"precipitation_probability_max":[40]}}
            """;
        var w = WeatherClient.ParseForecast(forecast, "C");
        Assert.NotNull(w);
        Assert.Equal("28° Partly cloudy · H 31° L 24° · 40% rain", w.Line);
        Assert.Equal("", w.Glyph);

        const string geo = """{"results":[{"name":"Chennai","latitude":13.08784,"longitude":80.27847,"country":"India"}]}""";
        var g = WeatherClient.ParseGeocode(geo);
        Assert.Equal("Chennai", g?.Name);
        Assert.Equal(13.08784, g!.Latitude, 4);
        Assert.Null(WeatherClient.ParseGeocode("""{"generationtime_ms":0.5}"""));
    }

    [Fact]
    public void Ics_feed_lists_todays_events_including_recurring_and_skips_cancelled()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//greeter-tests//EN
            BEGIN:VEVENT
            UID:1@test
            DTSTAMP:20261101T000000Z
            DTSTART:20261107T100000
            DTEND:20261107T103000
            SUMMARY:Standup
            LOCATION:Room 4
            END:VEVENT
            BEGIN:VEVENT
            UID:2@test
            DTSTAMP:20261101T000000Z
            DTSTART;VALUE=DATE:20261107
            DTEND;VALUE=DATE:20261108
            SUMMARY:Holiday
            END:VEVENT
            BEGIN:VEVENT
            UID:3@test
            DTSTAMP:20261101T000000Z
            DTSTART:20261101T140000
            DTEND:20261101T150000
            RRULE:FREQ=DAILY
            SUMMARY:Daily review
            END:VEVENT
            BEGIN:VEVENT
            UID:4@test
            DTSTAMP:20261101T000000Z
            DTSTART:20261107T160000
            DTEND:20261107T170000
            STATUS:CANCELLED
            SUMMARY:Cancelled thing
            END:VEVENT
            BEGIN:VEVENT
            UID:5@test
            DTSTAMP:20261101T000000Z
            DTSTART:20261108T090000
            DTEND:20261108T100000
            SUMMARY:Tomorrow's thing
            END:VEVENT
            END:VCALENDAR
            """;

        var items = CalendarFeed.ParseDay(ics.Replace("\r\n", "\n").Replace("\n", "\r\n"), new DateOnly(2026, 11, 7));

        Assert.Equal(new[] { "Holiday", "Standup", "Daily review" }, items.Select(i => i.Title));
        Assert.Equal("All day", items[0].TimeLabel);
        Assert.Equal("10:00–10:30", items[1].TimeLabel);
        Assert.Equal("Room 4", items[1].Location);
        Assert.Equal("14:00–15:00", items[2].TimeLabel);

        var at1030 = new DateTime(2026, 11, 7, 10, 15, 0);
        Assert.True(items[1].IsNow(at1030));
        Assert.Equal("Next meeting at 14:00: Daily review", CalendarFeed.NextMeetingSentence(items, at1030));
    }
}
