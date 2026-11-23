using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskHubGreeter.Client;

public sealed class TaskHubClientOptions
{
    /// <summary>TaskHub's home folder (where api.json lives). Null = %TASKHUB_HOME% or %LOCALAPPDATA%\TaskHub.</summary>
    public string? TaskHubHome { get; set; }

    /// <summary>Sent as <c>X-TaskHub-Client</c>; TaskHub labels our changes <c>api:&lt;name&gt;</c>.</summary>
    public string ClientName { get; set; } = "greeter";

    /// <summary>Per-request timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Thin client for TaskHub's local HTTP API. Discovers the port/token from api.json, adds the
/// bearer token and client header, never sends Origin, and re-reads api.json once on a 401 or a
/// refused connection (TaskHub may have restarted with a new port or token).
/// Thread-safe; all calls are async with a per-request timeout.
/// </summary>
public sealed class TaskHubClient : IDisposable
{
    public const string ClientHeader = "X-TaskHub-Client";

    private readonly HttpClient _http;
    private readonly TaskHubClientOptions _options;
    private readonly object _gate = new();
    private TaskHubEndpoint? _endpoint;

    public TaskHubClient(TaskHubClientOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? new TaskHubClientOptions();
        Discovery = new TaskHubDiscovery(_options.TaskHubHome);
        handler ??= new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = _options.Timeout,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public TaskHubDiscovery Discovery { get; }
    public string ClientName => _options.ClientName;
    public TimeSpan Timeout => _options.Timeout;

    /// <summary>The <c>source</c> TaskHub puts on events caused by this client.</summary>
    public string EventSource => $"api:{_options.ClientName.ToLowerInvariant()}";

    /// <summary>Returns the cached endpoint, or reads api.json (always, when <paramref name="refresh"/>).</summary>
    public TaskHubEndpoint GetEndpoint(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _endpoint is not null) return _endpoint;
        }
        var ep = Discovery.Read();
        lock (_gate) _endpoint = ep;
        return ep;
    }

    public void InvalidateEndpoint()
    {
        lock (_gate) _endpoint = null;
    }

    // ------------------------------------------------------------------ status

    public Task<StatusDto> GetStatusAsync(CancellationToken ct = default) =>
        SendAsync<StatusDto>(HttpMethod.Get, "v1/status", null, ct);

    /// <summary>True if TaskHub answers <c>/v1/status</c>.</summary>
    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            await GetStatusAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (TaskHubUnavailableException)
        {
            return false;
        }
        catch (TaskHubApiException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ tasks

    public Task<IReadOnlyList<TaskDto>> GetTasksAsync(TaskQuery query, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<TaskDto>>(HttpMethod.Get, "v1/tasks" + BuildQuery(query), null, ct);

    public Task<IReadOnlyList<TaskDto>> GetTasksAsync(string view, CancellationToken ct = default) =>
        GetTasksAsync(TaskQuery.ForView(view), ct);

    public Task<TaskDto> GetTaskAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Get, $"v1/tasks/{id}", null, ct);

    /// <summary>Quick add with TaskHub's natural-language syntax, e.g. <c>Pay rent tomorrow 9am #home !high @Finance</c>.</summary>
    public Task<TaskDto> QuickAddAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.", nameof(text));
        return CreateTaskAsync(new CreateTaskRequest { Text = text.Trim() }, ct);
    }

    public Task<TaskDto> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Post, "v1/tasks", request, ct);

    /// <summary>Completes a task. Repeating tasks move to their next date and stay open.</summary>
    public Task<TaskDto> CompleteAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Post, $"v1/tasks/{id}/complete", null, ct);

    public Task<TaskDto> ReopenAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Post, $"v1/tasks/{id}/reopen", null, ct);

    public Task<TaskDto> RestoreAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Post, $"v1/tasks/{id}/restore", null, ct);

    public Task<TaskDto> DeleteAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Delete, $"v1/tasks/{id}", null, ct);

    /// <summary>Snoozes the task's reminder; null minutes = the user's default.</summary>
    public Task<TaskDto> SnoozeAsync(Guid id, int? minutes = null, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Post, $"v1/tasks/{id}/snooze", minutes is { } m ? new SnoozeRequest(m) : null, ct);

    /// <summary>JSON merge-patch: present properties are set (null clears), absent ones unchanged.</summary>
    public Task<TaskDto> PatchAsync(Guid id, JsonObject patch, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Patch, $"v1/tasks/{id}", patch ?? throw new ArgumentNullException(nameof(patch)), ct);

    /// <summary>Undoes the most recent change (by any client). Returns its description, or null if there's nothing to undo.</summary>
    public async Task<string?> UndoAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await SendAsync<UndoResultDto>(HttpMethod.Post, "v1/undo", null, ct).ConfigureAwait(false);
            return result.Description;
        }
        catch (TaskHubApiException ex) when (ex.StatusCode == (int)HttpStatusCode.Conflict)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ lists & tags

    public Task<IReadOnlyList<TaskListDto>> GetListsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<TaskListDto>>(HttpMethod.Get, "v1/lists", null, ct);

    public Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<TagDto>>(HttpMethod.Get, "v1/tags", null, ct);

    // ------------------------------------------------------------------ notifications

    /// <summary>Shows a TaskHub banner; returns its id (button clicks arrive as <c>notification.action</c> events).</summary>
    public async Task<Guid> ShowNotificationAsync(NotifyRequest request, CancellationToken ct = default) =>
        (await SendAsync<NotifyResult>(HttpMethod.Post, "v1/notifications", request, ct).ConfigureAwait(false)).Id;

    public async Task<bool> GetDoNotDisturbAsync(CancellationToken ct = default) =>
        (await SendAsync<DndDto>(HttpMethod.Get, "v1/notifications/dnd", null, ct).ConfigureAwait(false)).Enabled;

    // ------------------------------------------------------------------ plumbing

    internal static string BuildQuery(TaskQuery q)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.View)) parts.Add("view=" + Uri.EscapeDataString(q.View));
        if (!string.IsNullOrWhiteSpace(q.Text)) parts.Add("q=" + Uri.EscapeDataString(q.Text));
        if (!string.IsNullOrWhiteSpace(q.Tag)) parts.Add("tag=" + Uri.EscapeDataString(q.Tag));
        if (q.ListId is { } list) parts.Add("list=" + list);
        if (q.IncludeCompleted) parts.Add("includeCompleted=true");
        if (q.Limit is { } limit) parts.Add("limit=" + limit);
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendCoreAsync(method, path, body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await ToApiExceptionAsync(response, ct).ConfigureAwait(false);
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(TaskHubJson.Options, ct).ConfigureAwait(false);
            return value ?? throw new TaskHubApiException((int)response.StatusCode, "TaskHub returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new TaskHubApiException((int)response.StatusCode, $"TaskHub returned unexpected JSON: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var endpoint = GetEndpoint(refresh: attempt > 0);
            using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            request.Headers.TryAddWithoutValidation(ClientHeader, _options.ClientName);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null) request.Content = ToContent(body);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.Timeout);
            HttpResponseMessage response;
            try
            {
                // ResponseContentRead: the body is buffered inside the timeout.
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TaskHubUnavailableException(UnavailableReason.Timeout,
                    $"TaskHub didn't respond within {_options.Timeout.TotalSeconds:0.#} s.", ex);
            }
            catch (HttpRequestException ex)
            {
                InvalidateEndpoint();
                var refused = ex.HttpRequestError is HttpRequestError.ConnectionError;
                // A refused connection means the request was never delivered, so retrying is safe even for POST.
                if (attempt == 0 && refused) continue;
                throw refused
                    ? new TaskHubUnavailableException(UnavailableReason.NotRunning, "TaskHub isn't running (connection refused).", ex)
                    : new TaskHubUnavailableException(UnavailableReason.Unreachable, $"Couldn't reach TaskHub: {ex.Message}", ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                // Token may have rotated on restart: re-read api.json + token and try once more.
                response.Dispose();
                InvalidateEndpoint();
                continue;
            }
            return response;
        }
    }

    private static HttpContent ToContent(object body)
    {
        // JsonNode keeps explicit nulls (needed for merge-patch "clear this field").
        var json = body is JsonNode node
            ? node.ToJsonString()
            : JsonSerializer.Serialize(body, body.GetType(), TaskHubJson.Options);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<TaskHubApiException> ToApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? message = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                message = JsonSerializer.Deserialize<ApiError>(text, TaskHubJson.Options)?.Error;
                message ??= text.Length > 200 ? text[..200] : text;
            }
        }
        catch (JsonException)
        {
            // not JSON; fall through to the generic message
        }
        message ??= status switch
        {
            401 => "TaskHub rejected the API token.",
            403 => "TaskHub rejected the request (Origin header).",
            404 => "Not found.",
            409 => "Nothing to undo.",
            421 => "TaskHub rejected the Host header.",
            _ => $"TaskHub returned HTTP {status}.",
        };
        return new TaskHubApiException(status, message);
    }

    public void Dispose() => _http.Dispose();
}
