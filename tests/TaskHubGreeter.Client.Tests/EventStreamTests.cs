using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace TaskHubGreeter.Client.Tests;

public sealed class EventStreamTests : IAsyncLifetime
{
    private readonly string _home = FakeTaskHub.NewHome();
    private FakeTaskHub _hub = null!;

    public async Task InitializeAsync() => _hub = await FakeTaskHub.StartAsync(_home);

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private static async Task Send(WebSocket ws, string json) =>
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<T> Within<T>(Task<T> task, int seconds = 10)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.True(done == task, "Timed out waiting");
        return await task;
    }

    private static TaskHubEventStreamOptions Fast() => new()
    {
        Topics = "task.*,dnd.changed,notification.action",
        ReconnectDelays = [TimeSpan.FromMilliseconds(100)],
    };

    [Fact]
    public async Task Receives_events_with_auth_and_reconnects_after_the_server_drops()
    {
        var connections = 0;
        _hub.Handler = async (ctx, req) =>
        {
            if (req.Path != "/v1/events" || !ctx.WebSockets.IsWebSocketRequest)
            {
                await FakeTaskHub.Json(ctx, 400, """{"error":"ws only"}""");
                return;
            }
            var n = Interlocked.Increment(ref connections);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await Send(ws, """{"id":"0193a000-0000-7000-8000-000000000001","type":"stream.ready","time":"2026-11-07T16:00:00Z","source":"api","data":{"topics":["task.*"]}}""");
            if (n == 1)
            {
                await Send(ws, """{"id":"0193a000-0000-7000-8000-000000000002","type":"task.created","time":"2026-11-07T16:00:01Z","source":"ui","data":{"task":{"id":"01a19d7f-318d-7041-9a9b-f8782e183a8f","title":"Pay rent","status":"open","priority":"high","tags":["home"],"createdUtc":"2026-11-07T16:00:01Z","updatedUtc":"2026-11-07T16:00:01Z"}}}""");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "TaskHub is closing", CancellationToken.None);
                return;
            }
            await Send(ws, """{"id":"0193a000-0000-7000-8000-000000000003","type":"dnd.changed","time":"2026-11-07T16:00:02Z","source":"ui","data":{"enabled":true}}""");
            await Send(ws, """{"id":"0193a000-0000-7000-8000-000000000004","type":"notification.action","time":"2026-11-07T16:00:03Z","source":"ui","data":{"notificationId":"9b6a3c5e-2222-4a4a-9a9a-000000000002","action":"greeter-open","taskIds":[]}}""");
            try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
            catch (OperationCanceledException) { }
        };

        var events = Channel.CreateUnbounded<TaskHubEvent>();
        using var client = new TaskHubClient(new TaskHubClientOptions { TaskHubHome = _home });
        await using var stream = new TaskHubEventStream(client, Fast());
        stream.EventReceived += (_, e) => events.Writer.TryWrite(e);
        stream.Start();

        var ready = await Within(events.Reader.ReadAsync().AsTask());
        Assert.Equal(TaskHubEvent.StreamReady, ready.Type);

        var created = await Within(events.Reader.ReadAsync().AsTask());
        Assert.Equal("task.created", created.Type);
        Assert.True(created.IsTaskEvent);
        Assert.Equal("ui", created.Source);
        Assert.Equal("Pay rent", created.Task?.Title);

        // Server closed the socket; the stream reconnects and gets a fresh stream.ready.
        Assert.Equal(TaskHubEvent.StreamReady, (await Within(events.Reader.ReadAsync().AsTask())).Type);
        var dnd = await Within(events.Reader.ReadAsync().AsTask());
        Assert.True(dnd.DndEnabled);
        var action = await Within(events.Reader.ReadAsync().AsTask());
        Assert.Equal("greeter-open", action.NotificationAction?.Action);
        Assert.Equal(Guid.Parse("9b6a3c5e-2222-4a4a-9a9a-000000000002"), action.NotificationAction?.NotificationId);
        Assert.True(stream.IsConnected);

        var upgrade = _hub.Requests.First(r => r.Path == "/v1/events");
        Assert.Equal("Bearer tok-1", upgrade.Header("Authorization"));
        Assert.Equal("greeter", upgrade.Header("X-TaskHub-Client"));
        Assert.Null(upgrade.Header("Origin"));
        Assert.Equal("?topics=task.*,dnd.changed,notification.action", Uri.UnescapeDataString(upgrade.Query));
    }

    [Fact]
    public async Task Keeps_retrying_until_TaskHub_starts()
    {
        var home = FakeTaskHub.NewHome(); // nothing there yet: TaskHub not running
        var events = Channel.CreateUnbounded<TaskHubEvent>();
        var states = Channel.CreateUnbounded<bool>();
        using var client = new TaskHubClient(new TaskHubClientOptions { TaskHubHome = home });
        await using var stream = new TaskHubEventStream(client, Fast());
        stream.EventReceived += (_, e) => events.Writer.TryWrite(e);
        stream.ConnectionChanged += (_, up) => states.Writer.TryWrite(up);
        stream.Start();

        await Task.Delay(400);
        Assert.False(stream.IsConnected);
        Assert.NotNull(stream.LastError);

        await using var hub = await FakeTaskHub.StartAsync(home);
        hub.Handler = async (ctx, _) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await Send(ws, """{"id":"0193a000-0000-7000-8000-000000000001","type":"stream.ready","time":"2026-11-07T16:00:00Z","source":"api"}""");
            try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
            catch (OperationCanceledException) { }
        };

        Assert.Equal(TaskHubEvent.StreamReady, (await Within(events.Reader.ReadAsync().AsTask())).Type);
        Assert.True(await Within(states.Reader.ReadAsync().AsTask()));
        Assert.True(stream.IsConnected);
    }
}
