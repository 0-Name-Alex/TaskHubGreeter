using System.Text;
using System.Windows.Threading;
using TaskHubGreeter.Client;

namespace TaskHubGreeter.App;

/// <summary>
/// The app's brain: owns the TaskHub client and event stream, decides when to show the Greeter
/// (window or TaskHub banner), applies the 30-minute auto-show limit and Do Not Disturb, and
/// keeps the window's data live (events, or 30 s polling when the socket is down).
/// </summary>
public sealed class GreeterController : IAsyncDisposable
{
    public const string BannerAction = "greeter-open";

    private readonly Dispatcher _ui;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _pollTimer;
    private readonly ExtrasService _extras = new();
    private readonly Speaker _speaker = new();
    private TaskHubClient _client;
    private TaskHubEventStream _events;
    private string _clientHome;
    private GreeterWindow? _window;
    private DateTime _lastAutoShowUtc = DateTime.MinValue;
    private Guid? _lastBannerId;

    public GreeterController(Dispatcher ui, SettingsStore settings)
    {
        _ui = ui;
        _settings = settings;
        _clientHome = settings.Current.TaskHubHome;
        _client = CreateClient(_clientHome);
        _events = CreateEventStream(_client);
        ViewModel = new GreeterViewModel(() => _client, settings, _extras);

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(30) };
        _pollTimer.Tick += async (_, _) =>
        {
            if (IsWindowVisible && !_events.IsConnected) await ViewModel.RefreshAsync();
        };
    }

    public GreeterViewModel ViewModel { get; }
    public bool IsWindowVisible => _window?.IsVisible == true;
    public bool IsEventStreamConnected => _events.IsConnected;

    /// <summary>Starts listening. <paramref name="atSignIn"/> = launched by the Run key.</summary>
    public async Task StartAsync(bool atSignIn, bool showNow)
    {
        _events.Start();
        if (atSignIn)
        {
            if (_settings.Current.AutoShow.SignIn)
            {
                // TaskHub is probably starting at the same time; give it a moment.
                for (var i = 0; i < 20 && !await _client.IsRunningAsync(); i++) await Task.Delay(1000);
                await AutoShowAsync("sign-in");
            }
        }
        else if (showNow)
        {
            ShowWindow(activate: true);
        }
    }

    /// <summary>Settings file changed: pick up a different TaskHub home.</summary>
    public async Task ApplySettingsAsync()
    {
        ViewModel.InvalidateExtras();
        if (IsWindowVisible) _ = ViewModel.RefreshAsync();
        var home = _settings.Current.TaskHubHome;
        if (string.Equals(home, _clientHome, StringComparison.OrdinalIgnoreCase)) return;
        Log.Info($"TaskHub home changed to '{home}'");
        await _events.DisposeAsync();
        var old = _client;
        _clientHome = home;
        _client = CreateClient(home);
        _events = CreateEventStream(_client);
        _events.Start();
        old.Dispose();
        if (IsWindowVisible) await ViewModel.RefreshAsync();
    }

    // ------------------------------------------------------------------ showing

    /// <summary>Tray / hotkey / second launch: always shows (ignores the limit and DND).</summary>
    public void ShowWindow(bool activate = true)
    {
        _window ??= CreateWindow();
        var wasVisible = _window.IsVisible;
        ViewModel.UpdateGreeting();
        _window.ShowGreeter(activate);
        _pollTimer.Start();
        if (!wasVisible || !_events.IsConnected) _ = ViewModel.RefreshAsync();
    }

    public void ToggleWindow()
    {
        if (IsWindowVisible && _window!.IsActive) _window.HideGreeter();
        else ShowWindow(activate: true);
    }

    /// <summary>Sign-in / unlock / resume. Respects the min interval and Do Not Disturb.</summary>
    public async Task AutoShowAsync(string trigger)
    {
        var settings = _settings.Current;
        var since = DateTime.UtcNow - _lastAutoShowUtc;
        if (since < settings.MinTimeBetweenAutoShows)
        {
            Log.Info($"Auto-show ({trigger}) skipped: last one {since.TotalMinutes:0} min ago");
            return;
        }
        if (settings.AutoShow.QuietWhenFullScreen && Presence.IsFullScreenBusy())
        {
            Log.Info($"Auto-show ({trigger}) skipped: a full-screen app or presentation is running");
            return;
        }

        bool running;
        try
        {
            if (await _client.GetDoNotDisturbAsync())
            {
                Log.Info($"Auto-show ({trigger}) skipped: Do Not Disturb is on");
                return;
            }
            running = true;
        }
        catch (TaskHubUnavailableException)
        {
            running = false; // still greet, with the "TaskHub isn't running" line
        }
        catch (TaskHubApiException ex)
        {
            Log.Warn("DND check failed: " + ex.Message);
            running = true;
        }

        _lastAutoShowUtc = DateTime.UtcNow;
        Log.Info($"Auto-show ({trigger})");
        if (!(settings.UseTaskHubBanner && running && await ShowBannerAsync())) ShowWindow(activate: true);

        if (settings.SpeakGreeting)
        {
            await ViewModel.RefreshAsync();
            _speaker.Speak(ViewModel.IsConnected ? ViewModel.SpokenSummary : ViewModel.Greeting + ".");
        }
    }

    /// <summary>Reads today's summary aloud now (tray menu).</summary>
    public async Task SpeakNowAsync()
    {
        await ViewModel.RefreshAsync();
        _speaker.Speak(ViewModel.IsConnected ? ViewModel.SpokenSummary : ViewModel.Greeting + ". TaskHub isn't running.");
    }

    /// <summary>Shows today's summary as a TaskHub banner with a "Show today" button.</summary>
    public async Task<bool> ShowBannerAsync()
    {
        await ViewModel.RefreshAsync();
        if (!ViewModel.IsConnected) return false;
        var snap = ViewModel.Snapshot;
        var body = new StringBuilder(ViewModel.Summary);
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var t in snap.Overdue.Concat(snap.DueToday).Take(4))
        {
            var due = Formatting.DueLabel(t, today);
            body.Append('\n').Append("• ").Append(t.Title.Length > 60 ? t.Title[..60] + "…" : t.Title);
            if (due.Length > 0) body.Append("  (").Append(due).Append(')');
        }
        var more = snap.Overdue.Count + snap.DueToday.Count - 4;
        if (more > 0) body.Append('\n').Append($"…and {more} more");

        try
        {
            ViewModel.UpdateGreeting();
            _lastBannerId = await _client.ShowNotificationAsync(new NotifyRequest
            {
                Title = ViewModel.Greeting + "!",
                Body = body.Length > 1000 ? body.ToString(0, 1000) : body.ToString(),
                Priority = snap.Overdue.Count > 0 ? "high" : "normal",
                Seconds = 10,
                Buttons = [new NotifyButton(BannerAction, "Show today")],
            });
            Log.Info($"Banner shown ({_lastBannerId})");
            return true;
        }
        catch (Exception ex) when (ex is TaskHubUnavailableException or TaskHubApiException)
        {
            Log.Warn("Banner failed, showing the window instead", ex);
            return false;
        }
    }

    // ------------------------------------------------------------------ plumbing

    private GreeterWindow CreateWindow()
    {
        var window = new GreeterWindow(ViewModel, () => _settings.Current.IdleCloseSeconds);
        window.Dismissed += (_, _) => _pollTimer.Stop();
        return window;
    }

    private static TaskHubClient CreateClient(string? home) =>
        new(new TaskHubClientOptions
        {
            TaskHubHome = string.IsNullOrWhiteSpace(home) ? null : home,
            ClientName = "greeter",
            Timeout = TimeSpan.FromSeconds(3),
        });

    private TaskHubEventStream CreateEventStream(TaskHubClient client)
    {
        var stream = new TaskHubEventStream(client, new TaskHubEventStreamOptions
        {
            Topics = "task.*,reminder.*,dnd.changed,notification.action",
        });
        stream.EventReceived += (_, e) => _ui.InvokeAsync(() => OnEvent(e));
        stream.ConnectionChanged += (_, connected) => _ui.InvokeAsync(() =>
        {
            Log.Info(connected ? "Event stream connected" : "Event stream disconnected" + (stream.LastError is { } err ? $" ({err})" : ""));
            if (connected && IsWindowVisible) _ = ViewModel.RefreshAsync();
        });
        return stream;
    }

    private void OnEvent(TaskHubEvent e)
    {
        if (e.NotificationAction is { } action)
        {
            if (action.Action == BannerAction)
            {
                Log.Info($"Banner button clicked ({action.NotificationId}{(action.NotificationId == _lastBannerId ? "" : ", older banner")})");
                ShowWindow(activate: true);
            }
            return;
        }
        if (IsWindowVisible) ViewModel.OnTaskHubEvent(e);
    }

    public async ValueTask DisposeAsync()
    {
        _pollTimer.Stop();
        await _events.DisposeAsync();
        _client.Dispose();
        _extras.Dispose();
        _speaker.Dispose();
        _window?.Close();
    }
}
