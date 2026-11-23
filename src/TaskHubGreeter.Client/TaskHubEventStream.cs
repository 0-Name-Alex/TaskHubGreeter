using System.Net.WebSockets;
using System.Text.Json;

namespace TaskHubGreeter.Client;

/// <summary>One event from <c>/v1/events</c>: <c>{ id, type, time, source, data }</c>.</summary>
public sealed record TaskHubEvent(Guid Id, string Type, DateTimeOffset Time, string Source, JsonElement? Data)
{
    public const string StreamReady = "stream.ready";
    public const string NotificationActionType = "notification.action";
    public const string DndChangedType = "dnd.changed";
    public const string ReminderDueType = "reminder.due";

    public bool IsTaskEvent => Type.StartsWith("task.", StringComparison.Ordinal);

    /// <summary><c>data.task</c> for task.* and reminder.* events.</summary>
    public TaskDto? Task => Get<TaskDto>("task");

    public NotificationActionData? NotificationAction =>
        Type == NotificationActionType ? As<NotificationActionData>() : null;

    public ReminderDueData? ReminderDue => Type == ReminderDueType ? As<ReminderDueData>() : null;

    /// <summary><c>data.enabled</c> for dnd.changed.</summary>
    public bool? DndEnabled =>
        Type == DndChangedType && Data is { ValueKind: JsonValueKind.Object } d
        && d.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : null;

    public T? As<T>() where T : class
    {
        if (Data is not { ValueKind: JsonValueKind.Object } d) return null;
        try { return d.Deserialize<T>(TaskHubJson.Options); }
        catch (JsonException) { return null; }
    }

    private T? Get<T>(string property) where T : class
    {
        if (Data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        try { return p.Deserialize<T>(TaskHubJson.Options); }
        catch (JsonException) { return null; }
    }

    public static TaskHubEvent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var id = r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && Guid.TryParse(i.GetString(), out var g) ? g : Guid.Empty;
        var type = r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var time = r.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.String && tm.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.UtcNow;
        var source = r.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
        JsonElement? data = r.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
        return new TaskHubEvent(id, type, time, source, data);
    }
}

public sealed class TaskHubEventStreamOptions
{
    public string Topics { get; set; } = "task.*,reminder.*,dnd.changed,notification.action";

    /// <summary>Wait before reconnect attempt N (the last value repeats).</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
}

/// <summary>
/// Keeps a WebSocket open to <c>/v1/events</c> and raises <see cref="EventReceived"/> for each message.
/// Reconnects with backoff (re-reading api.json first) whenever TaskHub restarts or isn't running yet.
/// Events are raised on a background thread.
/// </summary>
public sealed class TaskHubEventStream : IAsyncDisposable
{
    private readonly TaskHubClient _client;
    private readonly TaskHubEventStreamOptions _options;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _connected;

    public TaskHubEventStream(TaskHubClient client, TaskHubEventStreamOptions? options = null)
    {
        _client = client;
        _options = options ?? new TaskHubEventStreamOptions();
    }

    public event EventHandler<TaskHubEvent>? EventReceived;

    /// <summary>Raised with true once <c>stream.ready</c> arrives, false when the connection drops.</summary>
    public event EventHandler<bool>? ConnectionChanged;

    public bool IsConnected => _connected;
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loop is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await ConnectAndReadAsync(ct).ConfigureAwait(false)) failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or TaskHubUnavailableException or HttpRequestException
                                           or IOException or JsonException or OperationCanceledException or InvalidOperationException)
            {
                LastError = ex.Message;
            }
            SetConnected(false);

            var delays = _options.ReconnectDelays;
            var delay = delays.Count == 0 ? TimeSpan.FromSeconds(5) : delays[Math.Min(failures, delays.Count - 1)];
            failures++;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        SetConnected(false);
    }

    /// <summary>Returns true if the stream got as far as <c>stream.ready</c>.</summary>
    private async Task<bool> ConnectAndReadAsync(CancellationToken ct)
    {
        var endpoint = _client.GetEndpoint(refresh: true);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + endpoint.Token);
        socket.Options.SetRequestHeader(TaskHubClient.ClientHeader, _client.ClientName);
        // ClientWebSocket sends no Origin header unless asked to, which is what TaskHub requires.

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(_client.Timeout);
            await socket.ConnectAsync(endpoint.EventsUri(_options.Topics), connectCts.Token).ConfigureAwait(false);
        }

        var ready = false;
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
                break;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var json = System.Text.Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            TaskHubEvent e;
            try { e = TaskHubEvent.Parse(json); }
            catch (JsonException) { continue; }

            if (e.Type == TaskHubEvent.StreamReady && !ready)
            {
                ready = true;
                LastError = null;
                SetConnected(true);
            }
            Raise(e);
        }
        return ready;
    }

    private void Raise(TaskHubEvent e)
    {
        var handlers = EventReceived;
        if (handlers is null) return;
        try { handlers(this, e); }
        catch (Exception ex) { LastError = "Event handler failed: " + ex.Message; }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        try { ConnectionChanged?.Invoke(this, value); }
        catch (Exception ex) { LastError = "Event handler failed: " + ex.Message; }
    }
}
