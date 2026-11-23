using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace TaskHubGreeter.Client.Tests;

public sealed class TaskHubClientTests : IAsyncLifetime
{
    private const string PayRent = """
        {"id":"01a19d7f-318d-7041-9a9b-f8782e183a8f","title":"Pay rent","status":"open","priority":"high",
         "dueDate":"2026-11-08","dueTime":"09:00","dueAtUtc":"2026-11-08T03:30:00+00:00","tags":["home"],
         "recurrence":"FREQ=MONTHLY","recurrenceText":"Every month","reminderOffsets":[0],
         "nextReminderUtc":"2026-11-08T03:30:00+00:00","createdUtc":"2026-11-01T10:00:00+00:00","updatedUtc":"2026-11-01T10:00:00+00:00"}
        """;

    private readonly string _home = FakeTaskHub.NewHome();
    private FakeTaskHub _hub = null!;

    public async Task InitializeAsync() => _hub = await FakeTaskHub.StartAsync(_home);

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private TaskHubClient NewClient(TimeSpan? timeout = null) =>
        new(new TaskHubClientOptions { TaskHubHome = _home, Timeout = timeout ?? TimeSpan.FromSeconds(5) });

    // ---------------------------------------------------------------- discovery

    [Fact]
    public void Discovery_reads_port_and_trimmed_token()
    {
        var ep = new TaskHubDiscovery(_home).Read();
        Assert.Equal(_hub.Port, ep.Port);
        Assert.Equal("tok-1", ep.Token);
        Assert.Equal("0.1.0", ep.Version);
        Assert.Equal(new Uri($"http://127.0.0.1:{_hub.Port}/"), ep.BaseUri);
    }

    [Fact]
    public void Discovery_without_api_json_means_not_running()
    {
        var ex = Assert.Throws<TaskHubUnavailableException>(() => new TaskHubDiscovery(FakeTaskHub.NewHome()).Read());
        Assert.Equal(UnavailableReason.NotRunning, ex.Reason);
    }

    [Fact]
    public void Discovery_with_garbage_api_json_means_not_running()
    {
        var home = FakeTaskHub.NewHome();
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "api.json"), "{ half written");
        var ex = Assert.Throws<TaskHubUnavailableException>(() => new TaskHubDiscovery(home).Read());
        Assert.Equal(UnavailableReason.NotRunning, ex.Reason);
    }

    [Fact]
    public void Explicit_home_is_used_for_api_json()
    {
        Assert.Equal(Path.Combine(_home, "api.json"), TaskHubDiscovery.ResolveApiJsonPath(_home));
    }

    // ---------------------------------------------------------------- headers / security rules

    [Fact]
    public async Task Requests_carry_token_client_header_and_loopback_host_but_no_origin()
    {
        _hub.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 200, """{"version":"0.1.0","processId":42,"now":"2026-11-07T16:00:00+00:00","timeZone":"India Standard Time"}""");
        using var client = NewClient();

        var status = await client.GetStatusAsync();

        Assert.Equal(42, status.ProcessId);
        Assert.Equal("India Standard Time", status.TimeZone);
        var req = Assert.Single(_hub.Requests);
        Assert.Equal("GET", req.Method);
        Assert.Equal("/v1/status", req.Path);
        Assert.Equal("Bearer tok-1", req.Header("Authorization"));
        Assert.Equal("greeter", req.Header("X-TaskHub-Client"));
        Assert.Equal($"127.0.0.1:{_hub.Port}", req.Header("Host"));
        Assert.Null(req.Header("Origin"));
        Assert.Equal("api:greeter", client.EventSource);
    }

    // ---------------------------------------------------------------- tasks

    [Fact]
    public async Task Today_view_is_requested_and_tasks_are_parsed()
    {
        _hub.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 200, "[" + PayRent + "]");
        using var client = NewClient();

        var tasks = await client.GetTasksAsync(TaskViews.Today);

        Assert.Equal("?view=today", _hub.Requests.Single().Query);
        var t = Assert.Single(tasks);
        Assert.Equal(Guid.Parse("01a19d7f-318d-7041-9a9b-f8782e183a8f"), t.Id);
        Assert.Equal("Pay rent", t.Title);
        Assert.Equal(PriorityNames.High, t.Priority);
        Assert.Equal(new DateOnly(2026, 11, 8), t.DueDate);
        Assert.Equal("09:00", t.DueTime);
        Assert.Equal(new[] { "home" }, t.Tags);
        Assert.True(t.IsOpen);
        Assert.True(t.IsRecurring);
        Assert.Equal("Every month", t.RecurrenceText);
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T03:30:00+00:00"), t.NextReminderUtc);
    }

    [Fact]
    public void Query_string_escapes_and_includes_all_filters()
    {
        var list = Guid.NewGuid();
        var q = TaskHubClient.BuildQuery(new TaskQuery { View = "upcoming", Text = "pay & rent", Tag = "home", ListId = list, IncludeCompleted = true, Limit = 20 });
        Assert.Equal($"?view=upcoming&q=pay%20%26%20rent&tag=home&list={list}&includeCompleted=true&limit=20", q);
        Assert.Equal("", TaskHubClient.BuildQuery(new TaskQuery()));
    }

    [Fact]
    public async Task Quick_add_posts_text_and_returns_the_created_task()
    {
        _hub.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 201, PayRent);
        using var client = NewClient();

        var task = await client.QuickAddAsync("  Pay rent tomorrow 9am #home !high @Finance ");

        Assert.Equal("Pay rent", task.Title);
        var req = _hub.Requests.Single();
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1/tasks", req.Path);
        using var body = JsonDocument.Parse(req.Body);
        Assert.Equal("Pay rent tomorrow 9am #home !high @Finance", body.RootElement.GetProperty("text").GetString());
        Assert.False(body.RootElement.TryGetProperty("title", out _)); // nulls omitted
    }

    [Fact]
    public async Task Complete_reopen_and_snooze_hit_the_right_paths()
    {
        _hub.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 200, PayRent);
        using var client = NewClient();
        var id = Guid.Parse("01a19d7f-318d-7041-9a9b-f8782e183a8f");

        await client.CompleteAsync(id);
        await client.ReopenAsync(id);
        await client.SnoozeAsync(id);
        await client.SnoozeAsync(id, 30);

        var reqs = _hub.Requests.ToArray();
        Assert.Equal($"/v1/tasks/{id}/complete", reqs[0].Path);
        Assert.Equal($"/v1/tasks/{id}/reopen", reqs[1].Path);
        Assert.Equal($"/v1/tasks/{id}/snooze", reqs[2].Path);
        Assert.Equal("", reqs[2].Body); // default snooze = empty body
        Assert.Equal("""{"minutes":30}""", reqs[3].Body);
        Assert.All(reqs, r => Assert.Equal("POST", r.Method));
    }

    [Fact]
    public async Task Patch_sends_explicit_nulls()
    {
        _hub.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 200, PayRent);
        using var client = NewClient();
        var id = Guid.NewGuid();

        await client.PatchAsync(id, new JsonObject { ["dueDate"] = "2026-11-09", ["dueTime"] = null });

        var req = _hub.Requests.Single();
        Assert.Equal("PATCH", req.Method);
        Assert.Equal($"/v1/tasks/{id}", req.Path);
        Assert.Equal("""{"dueDate":"2026-11-09","dueTime":null}""", req.Body);
        Assert.StartsWith("application/json", req.Header("Content-Type"));
    }

    [Fact]
    public async Task Undo_returns_description_or_null_when_nothing_to_undo()
    {
        var calls = 0;
        _hub.Handler = (ctx, _) => ++calls == 1
            ? FakeTaskHub.Json(ctx, 200, """{"description":"Completed 'Pay rent'"}""")
            : FakeTaskHub.Json(ctx, 409, """{"error":"Nothing to undo."}""");
        using var client = NewClient();

        Assert.Equal("Completed 'Pay rent'", await client.UndoAsync());
        Assert.Null(await client.UndoAsync());
    }

    [Fact]
    public async Task Api_errors_carry_status_and_message()
    {
        _hub.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 404, """{"error":"Task not found."}""");
        using var client = NewClient();

        var ex = await Assert.ThrowsAsync<TaskHubApiException>(() => client.CompleteAsync(Guid.NewGuid()));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("Task not found.", ex.Message);
    }

    [Fact]
    public async Task Lists_tags_notifications_and_dnd()
    {
        _hub.Handler = (ctx, req) => req.Path switch
        {
            "/v1/lists" => FakeTaskHub.Json(ctx, 200, """[{"id":"9b6a3c5e-1111-4a4a-9a9a-000000000001","name":"Finance","openCount":3}]"""),
            "/v1/tags" => FakeTaskHub.Json(ctx, 200, """[{"name":"home","openCount":2}]"""),
            "/v1/notifications" => FakeTaskHub.Json(ctx, 202, """{"id":"9b6a3c5e-2222-4a4a-9a9a-000000000002"}"""),
            "/v1/notifications/dnd" => FakeTaskHub.Json(ctx, 200, """{"enabled":true}"""),
            _ => FakeTaskHub.Json(ctx, 404, """{"error":"no"}"""),
        };
        using var client = NewClient();

        Assert.Equal("Finance", Assert.Single(await client.GetListsAsync()).Name);
        Assert.Equal(2, Assert.Single(await client.GetTagsAsync()).OpenCount);
        var id = await client.ShowNotificationAsync(new NotifyRequest
        {
            Title = "Good morning!",
            Body = "3 due today",
            Priority = "normal",
            Seconds = 10,
            Buttons = [new NotifyButton("greeter-open", "Show today")],
        });
        Assert.Equal(Guid.Parse("9b6a3c5e-2222-4a4a-9a9a-000000000002"), id);
        Assert.True(await client.GetDoNotDisturbAsync());

        var notify = _hub.Requests.Single(r => r.Path == "/v1/notifications");
        using var body = JsonDocument.Parse(notify.Body);
        Assert.Equal("greeter-open", body.RootElement.GetProperty("buttons")[0].GetProperty("action").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("seconds").GetInt32());
    }

    // ---------------------------------------------------------------- restarts, timeouts, not running

    [Fact]
    public async Task Rotated_token_is_reread_after_a_401()
    {
        _hub.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 200, "[]");
        using var client = NewClient();
        await client.GetTasksAsync(TaskViews.Today); // caches tok-1

        _hub.Token = "tok-2";
        _hub.WriteDiscovery(); // TaskHub restarted with a new token

        await client.GetTasksAsync(TaskViews.Today);

        var auths = _hub.Requests.Select(r => r.Header("Authorization")).ToArray();
        Assert.Equal(new[] { "Bearer tok-1", "Bearer tok-1", "Bearer tok-2" }, auths);
    }

    [Fact]
    public async Task A_wrong_token_that_stays_wrong_surfaces_401()
    {
        _hub.Token = "something-else";
        using var client = NewClient();

        var ex = await Assert.ThrowsAsync<TaskHubApiException>(() => client.GetTasksAsync(TaskViews.Today));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(2, _hub.Requests.Count); // one retry after re-reading api.json
    }

    [Fact]
    public async Task Restart_on_a_new_port_is_followed()
    {
        _hub.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 200, "[]");
        using var client = NewClient();
        await client.GetTasksAsync(TaskViews.Today);
        var oldPort = _hub.Port;
        await _hub.StopAsync();

        // TaskHub comes back on another port and rewrites api.json.
        await using var restarted = await FakeTaskHub.StartAsync(_home, "tok-1");
        restarted.Handler = (ctx, _) => FakeTaskHub.Json(ctx, 200, "[" + PayRent + "]");
        // (If the OS happened to reuse the old port the test still exercises the restart path.)
        _ = oldPort;

        var tasks = await client.GetTasksAsync(TaskViews.Today);

        Assert.Single(tasks);
        Assert.Single(restarted.Requests);
    }

    [Fact]
    public async Task Nothing_listening_means_not_running()
    {
        _hub.WriteDiscovery(port: FakeTaskHub.UnusedPort());
        using var client = NewClient();

        var ex = await Assert.ThrowsAsync<TaskHubUnavailableException>(() => client.GetStatusAsync());

        Assert.Equal(UnavailableReason.NotRunning, ex.Reason);
        Assert.False(await client.IsRunningAsync());
    }

    [Fact]
    public async Task Missing_api_json_means_not_running()
    {
        using var client = new TaskHubClient(new TaskHubClientOptions { TaskHubHome = FakeTaskHub.NewHome() });
        var ex = await Assert.ThrowsAsync<TaskHubUnavailableException>(() => client.GetTasksAsync(TaskViews.Today));
        Assert.Equal(UnavailableReason.NotRunning, ex.Reason);
    }

    [Fact]
    public async Task Slow_server_times_out_quickly()
    {
        _hub.Handler = async (ctx, _) =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted); }
            catch (OperationCanceledException) { return; }
            await FakeTaskHub.Json(ctx, 200, "[]");
        };
        using var client = NewClient(TimeSpan.FromMilliseconds(400));
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<TaskHubUnavailableException>(() => client.GetTasksAsync(TaskViews.Today));

        Assert.Equal(UnavailableReason.Timeout, ex.Reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        _hub.Handler = async (ctx, _) =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted); }
            catch (OperationCanceledException) { }
        };
        using var client = NewClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTasksAsync(TaskViews.Today, cts.Token));
    }
}
