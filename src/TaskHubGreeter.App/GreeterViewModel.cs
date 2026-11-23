using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TaskHubGreeter.Client;
using TaskHubGreeter.Client.Extras;

namespace TaskHubGreeter.App;

public enum ConnectionState
{
    Unknown,
    Connected,
    NotRunning,
    Unreachable,
}

/// <summary>A tag/list filter above the list. Kind: "all", "tag" or "list".</summary>
public sealed class FilterChip(string label, string kind, string key) : ObservableObject
{
    private bool _isSelected;
    public string Label { get; } = label;
    public string Kind { get; } = kind;
    public string Key { get; } = key;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed record UpcomingItem(string Title, string Time, string PriorityMarker);
public sealed record UpcomingGroup(string Label, IReadOnlyList<UpcomingItem> Items);
public sealed record MeetingRow(string TimeLabel, string Title, string? Location, bool IsNow, bool IsPast);

/// <summary>State and actions behind the Greeter window. Everything runs on the UI thread; network calls are awaited.</summary>
public sealed class GreeterViewModel : ObservableObject
{
    public const string SearchSection = "Search results";

    private readonly Func<TaskHubClient> _client;
    private readonly SettingsStore _settings;
    private readonly ExtrasService _extras;
    private readonly DispatcherTimer _refreshDebounce;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _statusClear;

    private int _refreshVersion;
    private int _actionVersion;
    private int _searchVersion;
    private int _extrasVersion;
    private string _greeting = "";
    private string _dateText = "";
    private string _summary = "";
    private string? _status;
    private bool _statusIsError;
    private ConnectionState _connection = ConnectionState.Unknown;
    private string _connectionMessage = "";
    private bool _isLoading;
    private bool _hasLoaded;
    private string _quickAddText = "";
    private bool _isAdding;
    private bool _isStartingTaskHub;
    private string? _lastActionDescription;
    private bool _changedElsewhereSinceLastAction;
    private List<(Guid Id, DateOnly? Date)>? _bulkUndo;
    private TaskRow? _selectedRow;

    private DayProgress _progress = new(0, 0);
    private DayMode _dayMode;
    private string? _dayPromptText;
    private string? _dayPromptButton;
    private IReadOnlyList<TaskDto> _dayPromptTasks = [];
    private (DayMode, DateOnly)? _dayPromptDismissed;
    private bool _isUpcomingExpanded;
    private FilterChip? _selectedChip;
    private bool _isSearchOpen;
    private string _searchText = "";
    private bool _isSearchBusy;
    private string? _weatherLine;
    private string? _weatherGlyph;
    private IReadOnlyList<CalendarItem> _meetingItems = [];

    public GreeterViewModel(Func<TaskHubClient> client, SettingsStore settings, ExtrasService extras)
    {
        _client = client;
        _settings = settings;
        _extras = extras;

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskRow.Section)));
        RowsView.IsLiveGrouping = true;
        RowsView.LiveGroupingProperties.Add(nameof(TaskRow.Section));
        RowsView.Filter = o => o is TaskRow r && MatchesChip(r);

        SearchView = (ListCollectionView)CollectionViewSource.GetDefaultView(SearchRows);
        SearchView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskRow.Section)));

        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshDebounce.Tick += async (_, _) =>
        {
            _refreshDebounce.Stop();
            await RefreshAsync();
        };
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await RunSearchAsync();
        };
        _statusClear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _statusClear.Tick += (_, _) =>
        {
            _statusClear.Stop();
            if (!StatusIsError) Status = null;
        };

        UndoCommand = new RelayCommand(async () => await UndoAsync());
        QuickAddCommand = new RelayCommand(async () => await QuickAddAsync());
        StartTaskHubCommand = new RelayCommand(async _ => await StartTaskHubAsync(), _ => !IsStartingTaskHub);
        RetryCommand = new RelayCommand(async () => await RefreshAsync());
        OpenSettingsCommand = new RelayCommand(() => OpenFile(_settings.FilePath));
        DayPromptCommand = new RelayCommand(async () => await RunDayPromptAsync());
        DismissDayPromptCommand = new RelayCommand(DismissDayPrompt);
        SelectChipCommand = new RelayCommand(p => SelectChip(p as FilterChip));
        ToggleSearchCommand = new RelayCommand(() => IsSearchOpen = !IsSearchOpen);
    }

    // ------------------------------------------------------------------ collections

    public ObservableCollection<TaskRow> Rows { get; } = [];
    public ListCollectionView RowsView { get; }
    public ObservableCollection<TaskRow> SearchRows { get; } = [];
    public ListCollectionView SearchView { get; }
    public ObservableCollection<FilterChip> Chips { get; } = [];
    public ObservableCollection<UpcomingGroup> Upcoming { get; } = [];
    public ObservableCollection<MeetingRow> Meetings { get; } = [];
    public TodaySnapshot Snapshot { get; private set; } = TodaySnapshot.Empty;

    /// <summary>What the task list shows: search results while searching, otherwise today.</summary>
    public ListCollectionView ActiveView => IsSearching ? SearchView : RowsView;

    /// <summary>Rows currently visible in the list, in display order.</summary>
    public IReadOnlyList<TaskRow> VisibleRows => ActiveView.Cast<object>().OfType<TaskRow>().ToList();

    // ------------------------------------------------------------------ commands

    public ICommand UndoCommand { get; }
    public ICommand QuickAddCommand { get; }
    public ICommand StartTaskHubCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand DayPromptCommand { get; }
    public ICommand DismissDayPromptCommand { get; }
    public ICommand SelectChipCommand { get; }
    public ICommand ToggleSearchCommand { get; }

    // ------------------------------------------------------------------ header

    public string Greeting { get => _greeting; private set => Set(ref _greeting, value); }
    public string DateText { get => _dateText; private set => Set(ref _dateText, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public string? WeatherLine
    {
        get => _weatherLine;
        private set
        {
            if (Set(ref _weatherLine, value)) Raise(nameof(ShowWeather));
        }
    }

    public string? WeatherGlyph { get => _weatherGlyph; private set => Set(ref _weatherGlyph, value); }
    public bool ShowWeather => !string.IsNullOrEmpty(_weatherLine);

    public bool ShowQuote => _settings.Current.ShowQuote;
    public string QuoteText => "“" + QuoteOfTheDay.For(DateOnly.FromDateTime(DateTime.Now)).Text + "”";
    public string QuoteAuthor => "— " + QuoteOfTheDay.For(DateOnly.FromDateTime(DateTime.Now)).Author;

    public DayProgress Progress
    {
        get => _progress;
        private set
        {
            if (!Set(ref _progress, value)) return;
            Raise(nameof(ProgressFraction));
            Raise(nameof(ProgressLabel));
            Raise(nameof(ProgressToolTip));
            Raise(nameof(ShowProgress));
        }
    }

    public double ProgressFraction => _progress.Fraction;
    public string ProgressLabel => $"{_progress.Done}/{_progress.Total}";
    public string ProgressToolTip => _progress.Text + " today";
    public bool ShowProgress => IsConnected && _progress.Total > 0;

    // ------------------------------------------------------------------ status / connection

    public string? Status { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public ConnectionState Connection
    {
        get => _connection;
        private set
        {
            if (!Set(ref _connection, value)) return;
            Raise(nameof(IsConnected));
            Raise(nameof(ShowOffline));
            Raise(nameof(ShowEmpty));
            Raise(nameof(CanStartTaskHub));
            Raise(nameof(ShowProgress));
            Raise(nameof(ShowDayPrompt));
            Raise(nameof(ShowChips));
            Raise(nameof(ShowUpcoming));
        }
    }

    public bool IsConnected => _connection == ConnectionState.Connected;
    public bool ShowOffline => _connection is ConnectionState.NotRunning or ConnectionState.Unreachable;
    public bool CanStartTaskHub => _connection == ConnectionState.NotRunning;
    public string ConnectionMessage { get => _connectionMessage; private set => Set(ref _connectionMessage, value); }

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool ShowEmpty => IsConnected && _hasLoaded && !IsSearching && Rows.Count == 0;

    // ------------------------------------------------------------------ day prompt (plan / wrap up)

    public DayMode DayMode { get => _dayMode; private set => Set(ref _dayMode, value); }

    public string? DayPromptText
    {
        get => _dayPromptText;
        private set
        {
            if (Set(ref _dayPromptText, value)) Raise(nameof(ShowDayPrompt));
        }
    }

    public string? DayPromptButton { get => _dayPromptButton; private set => Set(ref _dayPromptButton, value); }
    public string DayPromptTitle => _dayMode == DayMode.Morning ? "Plan your day" : "Wrap up";
    public string DayPromptGlyph => _dayMode == DayMode.Morning ? "" : ""; // sun / moon
    public bool ShowDayPrompt => IsConnected && !IsSearching && !string.IsNullOrEmpty(_dayPromptText);

    // ------------------------------------------------------------------ chips / upcoming / meetings

    public bool ShowChips => IsConnected && !IsSearching && Chips.Count > 2;

    public int UpcomingCount => Upcoming.Sum(g => g.Items.Count);
    public string UpcomingHeader => $"Coming up ({UpcomingCount})";
    public bool ShowUpcoming => IsConnected && !IsSearching && UpcomingCount > 0;
    public bool IsUpcomingExpanded { get => _isUpcomingExpanded; set => Set(ref _isUpcomingExpanded, value); }

    public bool ShowMeetings => Meetings.Count > 0 && !IsSearching;

    // ------------------------------------------------------------------ search

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set
        {
            if (!Set(ref _isSearchOpen, value)) return;
            if (!value) SearchText = "";
            OnSearchModeChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _searchDebounce.Stop();
            if (IsSearching) _searchDebounce.Start();
            else SearchRows.Clear();
            OnSearchModeChanged();
        }
    }

    public bool IsSearching => _isSearchOpen && _searchText.Trim().Length > 0;
    public bool IsSearchBusy { get => _isSearchBusy; private set => Set(ref _isSearchBusy, value); }
    public bool ShowNoMatches => IsSearching && !IsSearchBusy && SearchRows.Count == 0;

    // ------------------------------------------------------------------ quick add / undo

    public string QuickAddText { get => _quickAddText; set => Set(ref _quickAddText, value); }
    public bool IsAdding { get => _isAdding; private set => Set(ref _isAdding, value); }

    public bool IsStartingTaskHub
    {
        get => _isStartingTaskHub;
        private set
        {
            if (Set(ref _isStartingTaskHub, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanUndo => _lastActionDescription is not null && IsConnected;

    public string UndoToolTip => _lastActionDescription is null
        ? "Undo the last change (Ctrl+Z)"
        : _bulkUndo is null && _changedElsewhereSinceLastAction
            ? $"Undo the most recent change in TaskHub (made after \"{_lastActionDescription}\")"
            : $"Undo: {_lastActionDescription} (Ctrl+Z)";

    public TaskRow? SelectedRow { get => _selectedRow; set => Set(ref _selectedRow, value); }

    /// <summary>True while something is in flight that idle-close shouldn't interrupt.</summary>
    public bool IsBusy => IsAdding || IsStartingTaskHub || Rows.Any(r => r.IsBusy) || SearchRows.Any(r => r.IsBusy);

    /// <summary>"Good morning, Dhaya. You have 3 tasks today … Next meeting at 10:00: Standup."</summary>
    public string SpokenSummary => DayPlanning.SpokenSummary(Greeting, Snapshot, CalendarFeed.NextMeetingSentence(_meetingItems, DateTime.Now));

    /// <summary>Raised after the rows were re-synced or the list switched (the window restores keyboard focus).</summary>
    public event EventHandler? RowsChanged;

    // ------------------------------------------------------------------ loading

    public void UpdateGreeting()
    {
        var now = DateTime.Now;
        var name = _settings.Current.DisplayName;
        var hello = Formatting.GreetingFor(now.Hour);
        Greeting = string.IsNullOrEmpty(name) ? hello : $"{hello}, {name}";
        DateText = Formatting.LongDate(now);
        Raise(nameof(ShowQuote));
        Raise(nameof(QuoteText));
        Raise(nameof(QuoteAuthor));
    }

    /// <summary>Coalesces bursts of events into one refresh.</summary>
    public void ScheduleRefresh()
    {
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    /// <summary>Loads overdue/today/upcoming/done-today from TaskHub (and weather/calendar on the side). Never throws.</summary>
    public async Task RefreshAsync()
    {
        UpdateGreeting();
        _ = RefreshExtrasAsync();
        if (IsSearching) _ = RunSearchAsync();

        var version = ++_refreshVersion;
        var actionAtStart = _actionVersion;
        IsLoading = true;
        try
        {
            var client = _client();
            var today = client.GetTasksAsync(TaskViews.Today);
            var overdue = client.GetTasksAsync(TaskViews.Overdue);
            var upcoming = client.GetTasksAsync(TaskQuery.ForView(TaskViews.Upcoming, limit: 300));
            var completed = client.GetTasksAsync(TaskQuery.ForView(TaskViews.Completed, limit: 500));
            var lists = client.GetListsAsync();
            await Task.WhenAll(today, overdue, upcoming, completed, lists);

            // A newer refresh started, or the user acted meanwhile (result may be stale): drop it.
            if (version != _refreshVersion) return;
            if (actionAtStart != _actionVersion)
            {
                ScheduleRefresh();
                return;
            }

            var now = DateTimeOffset.Now;
            var todayDate = DateOnly.FromDateTime(DateTime.Now);
            Snapshot = TodaySnapshot.Build(today.Result, overdue.Result, upcoming.Result, now);
            SyncRows(Snapshot);
            Summary = Snapshot.SummaryText(now, TimeZoneInfo.Local);
            Progress = DayPlanning.Progress(Snapshot, DayPlanning.CountCompletedToday(completed.Result, todayDate, TimeZoneInfo.Local));
            BuildChips(lists.Result);
            BuildUpcoming(upcoming.Result, todayDate);
            BuildDayPrompt(todayDate);
            _hasLoaded = true;
            SetConnected();
        }
        catch (TaskHubUnavailableException ex)
        {
            if (version == _refreshVersion) SetOffline(ex);
        }
        catch (TaskHubApiException ex)
        {
            if (version == _refreshVersion)
            {
                SetConnected();
                ShowError(ex.StatusCode == 401
                    ? "TaskHub rejected the API token. Is the Greeter reading the right api.json? (taskHubHome in settings)"
                    : $"TaskHub: {ex.Message}");
            }
        }
        finally
        {
            if (version == _refreshVersion) IsLoading = false;
            Raise(nameof(ShowEmpty));
        }
    }

    private async Task RefreshExtrasAsync()
    {
        var version = ++_extrasVersion;
        var settings = _settings.Current;
        var weatherTask = _extras.GetWeatherAsync(settings.Weather);
        var meetingsTask = _extras.GetMeetingsAsync(settings.Calendar, DateOnly.FromDateTime(DateTime.Now));

        var weather = await weatherTask;
        if (version != _extrasVersion) return;
        WeatherLine = weather?.Line;
        WeatherGlyph = weather?.Glyph;

        var meetings = await meetingsTask;
        if (version != _extrasVersion) return;
        _meetingItems = meetings;
        var nowLocal = DateTime.Now;
        Meetings.Clear();
        foreach (var m in meetings)
            Meetings.Add(new MeetingRow(m.TimeLabel, m.Title, m.Location, m.IsNow(nowLocal), m.IsPast(nowLocal)));
        Raise(nameof(ShowMeetings));
    }

    /// <summary>Settings changed: forget cached weather/calendar.</summary>
    public void InvalidateExtras() => _extras.Invalidate();

    private void SetConnected()
    {
        Connection = ConnectionState.Connected;
        ConnectionMessage = "";
        Raise(nameof(CanUndo));
    }

    private void SetOffline(TaskHubUnavailableException ex)
    {
        Log.Info("TaskHub unavailable: " + ex.Message);
        if (ex.Reason == UnavailableReason.NotRunning)
        {
            Connection = ConnectionState.NotRunning;
            if (!IsStartingTaskHub) ConnectionMessage = "TaskHub isn't running";
            Summary = "Start TaskHub to see today's tasks.";
        }
        else
        {
            Connection = ConnectionState.Unreachable;
            ConnectionMessage = ex.Reason == UnavailableReason.Timeout ? "TaskHub isn't responding" : "Couldn't reach TaskHub";
            Summary = ex.Message;
        }
        Raise(nameof(CanUndo));
    }

    private void SyncRows(TodaySnapshot snap)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var existing = Rows.ToDictionary(r => r.Id);
        var desired = new List<TaskRow>();
        var snapshotIds = snap.Overdue.Concat(snap.DueToday).Select(t => t.Id).ToHashSet();
        // Rows completed in this session stay (struck through) at the end of their section.
        var leftovers = Rows.Where(r => r.IsCompleted && !snapshotIds.Contains(r.Id)).ToList();

        void Add(TaskDto task, bool overdue)
        {
            if (existing.Remove(task.Id, out var row)) row.Update(task, overdue, today);
            else row = new TaskRow(task, overdue, today, OnToggleRequested);
            desired.Add(row);
        }

        foreach (var t in snap.Overdue) Add(t, overdue: true);
        desired.AddRange(leftovers.Where(r => r.IsOverdue));
        foreach (var t in snap.DueToday) Add(t, overdue: false);
        desired.AddRange(leftovers.Where(r => !r.IsOverdue));

        // Minimal edits so selection and keyboard focus survive.
        var keep = desired.ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var at = Rows.IndexOf(desired[i]);
            if (at < 0) Rows.Insert(i, desired[i]);
            else if (at != i) Rows.Move(at, i);
        }
        if (SelectedRow is not null && !Rows.Contains(SelectedRow) && !SearchRows.Contains(SelectedRow)) SelectedRow = null;
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildChips(IReadOnlyList<TaskListDto> lists)
    {
        var open = Rows.Where(r => !r.IsCompleted).ToList();
        var listNames = lists.ToDictionary(l => l.Id, l => l.Name);
        var listChips = open.Where(r => r.ListId is { } id && listNames.ContainsKey(id))
            .GroupBy(r => r.ListId!.Value)
            .OrderByDescending(g => g.Count())
            .Select(g => new FilterChip("@" + listNames[g.Key], "list", g.Key.ToString()));
        var tagChips = open.SelectMany(r => r.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => new FilterChip("#" + g.Key, "tag", g.Key.ToLowerInvariant()));

        string? selectedKind = _selectedChip is { Kind: not "all" } ? _selectedChip.Kind : null;
        string? selectedKey = selectedKind is null ? null : _selectedChip!.Key;
        var chips = new List<FilterChip> { new("All", "all", "") };
        chips.AddRange(listChips.Concat(tagChips).Take(8));

        Chips.Clear();
        _selectedChip = null;
        foreach (var c in chips)
        {
            if (selectedKind is not null && c.Kind == selectedKind && c.Key == selectedKey) _selectedChip = c;
            Chips.Add(c);
        }
        _selectedChip ??= Chips[0];
        _selectedChip.IsSelected = true;
        RowsView.Refresh();
        Raise(nameof(ShowChips));
    }

    private void SelectChip(FilterChip? chip)
    {
        if (chip is null || Chips.Count == 0) return;
        if (_selectedChip is not null) _selectedChip.IsSelected = false;
        // Clicking the selected chip again goes back to All.
        _selectedChip = ReferenceEquals(chip, _selectedChip) && chip.Kind != "all" ? Chips[0] : chip;
        _selectedChip.IsSelected = true;
        RowsView.Refresh();
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool MatchesChip(TaskRow row)
    {
        var chip = _selectedChip;
        if (chip is null || chip.Kind == "all") return true;
        if (chip.Kind == "tag") return row.Tags.Any(t => string.Equals(t, chip.Key, StringComparison.OrdinalIgnoreCase));
        if (chip.Kind == "list") return row.ListId?.ToString() == chip.Key;
        return true;
    }

    private void BuildUpcoming(IReadOnlyList<TaskDto> upcoming, DateOnly today)
    {
        Upcoming.Clear();
        foreach (var day in DayPlanning.UpcomingByDay(upcoming, today, _settings.Current.UpcomingDays))
        {
            var items = day.Tasks.Select(t => new UpcomingItem(t.Title, t.DueTime ?? "", Formatting.PriorityMarker(t.Priority))).ToList();
            Upcoming.Add(new UpcomingGroup(day.Label, items));
        }
        Raise(nameof(UpcomingCount));
        Raise(nameof(UpcomingHeader));
        Raise(nameof(ShowUpcoming));
    }

    private void BuildDayPrompt(DateOnly today)
    {
        DayMode = DayPlanning.ModeFor(DateTime.Now.Hour);
        Raise(nameof(DayPromptTitle));
        Raise(nameof(DayPromptGlyph));
        _dayPromptTasks = [];
        string? text = null;
        string? button = null;
        var dismissed = _dayPromptDismissed is { } d && d.Item1 == DayMode && d.Item2 == today;

        if (_settings.Current.DayModes && !dismissed)
        {
            if (DayMode == DayMode.Morning)
            {
                _dayPromptTasks = DayPlanning.CarryOverCandidates(Snapshot, today);
                if (_dayPromptTasks.Count > 0)
                {
                    text = $"{Formatting.Plural(_dayPromptTasks.Count, "task", "tasks")} from earlier days. Bring them into today?";
                    button = "Move to today";
                }
            }
            else if (DayMode == DayMode.Evening)
            {
                _dayPromptTasks = DayPlanning.WrapUpCandidates(Snapshot);
                var done = Progress.Done;
                if (_dayPromptTasks.Count > 0)
                {
                    text = (done > 0 ? $"You finished {done} today. " : "") + $"{_dayPromptTasks.Count} still open — move them to tomorrow?";
                    button = "Move to tomorrow";
                }
                else if (done > 0)
                {
                    text = $"You finished {Formatting.Plural(done, "task", "tasks")} today. All clear!";
                }
            }
        }
        DayPromptText = text;
        DayPromptButton = button;
        Raise(nameof(ShowDayPrompt));
    }

    private void DismissDayPrompt()
    {
        _dayPromptDismissed = (DayMode, DateOnly.FromDateTime(DateTime.Now));
        DayPromptText = null;
    }

    /// <summary>The window was hidden: forget struck-through rows and close search so the next show starts clean.</summary>
    public void OnHidden()
    {
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (Rows[i].IsCompleted) Rows.RemoveAt(i);
        _refreshDebounce.Stop();
        IsSearchOpen = false;
        if (!StatusIsError) Status = null;
    }

    /// <summary>A TaskHub event arrived (UI thread).</summary>
    public void OnTaskHubEvent(TaskHubEvent e)
    {
        if (!e.IsTaskEvent) return;
        if (_lastActionDescription is not null && e.Source != _client().EventSource && !_changedElsewhereSinceLastAction)
        {
            _changedElsewhereSinceLastAction = true;
            Raise(nameof(UndoToolTip));
        }
        ScheduleRefresh();
    }

    // ------------------------------------------------------------------ search

    private void OnSearchModeChanged()
    {
        Raise(nameof(IsSearching));
        Raise(nameof(ActiveView));
        Raise(nameof(ShowNoMatches));
        Raise(nameof(ShowEmpty));
        Raise(nameof(ShowDayPrompt));
        Raise(nameof(ShowChips));
        Raise(nameof(ShowUpcoming));
        Raise(nameof(ShowMeetings));
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunSearchAsync()
    {
        if (!IsSearching) return;
        var text = _searchText.Trim();
        var version = ++_searchVersion;
        IsSearchBusy = true;
        Raise(nameof(ShowNoMatches));
        try
        {
            var results = await _client().GetTasksAsync(new TaskQuery { Text = text, Limit = 30 });
            if (version != _searchVersion || !IsSearching) return;
            var today = DateOnly.FromDateTime(DateTime.Now);
            var now = DateTimeOffset.Now;
            SearchRows.Clear();
            foreach (var t in results.Where(t => t.IsOpen))
            {
                var overdue = t.DueDate is { } d && (d < today || (t.DueAtUtc is { } at && at < now));
                SearchRows.Add(new TaskRow(t, overdue, today, OnToggleRequested, SearchSection));
            }
            RowsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is TaskHubUnavailableException or TaskHubApiException)
        {
            if (version == _searchVersion) ShowError("Search failed: " + ex.Message);
        }
        finally
        {
            if (version == _searchVersion) IsSearchBusy = false;
            Raise(nameof(ShowNoMatches));
        }
    }

    // ------------------------------------------------------------------ actions

    private void OnToggleRequested(TaskRow row, bool complete) => _ = ToggleAsync(row, complete);

    public async Task ToggleAsync(TaskRow row, bool complete)
    {
        if (row.IsBusy) return;
        _actionVersion++;
        row.IsBusy = true;
        row.SetCompleted(complete); // optimistic
        try
        {
            var client = _client();
            if (complete)
            {
                var updated = await client.CompleteAsync(row.Id);
                string? note = null;
                if (updated.IsOpen && updated.DueDate is not null)
                    note = "next: " + Formatting.DueLabel(updated, DateOnly.FromDateTime(DateTime.Now));
                row.SetCompleted(true, note);
                RememberAction($"Completed \"{row.Title}\"");
                ShowInfo(note is null ? $"Done: {row.Title}" : $"Done: {row.Title} · {note}");
            }
            else
            {
                await client.ReopenAsync(row.Id);
                row.SetCompleted(false);
                RememberAction($"Reopened \"{row.Title}\"");
            }
        }
        catch (Exception ex) when (ex is TaskHubUnavailableException or TaskHubApiException)
        {
            row.SetCompleted(!complete);
            ShowError((complete ? "Couldn't complete: " : "Couldn't reopen: ") + ex.Message);
            if (ex is TaskHubUnavailableException u) SetOffline(u);
        }
        finally
        {
            row.IsBusy = false;
            _actionVersion++;
            Summary = RecomputeSummary();
            ScheduleRefresh(); // in case the event stream is down
        }
    }

    /// <summary>Moves one task to another day (keeps its time).</summary>
    public async Task RescheduleAsync(TaskRow row, DateOnly date)
    {
        if (row.IsBusy) return;
        _actionVersion++;
        row.IsBusy = true;
        try
        {
            await _client().PatchAsync(row.Id, new JsonObject { ["dueDate"] = date.ToString("yyyy-MM-dd") });
            var label = DayPlanning.DayLabel(date, DateOnly.FromDateTime(DateTime.Now));
            RememberAction($"Moved \"{row.Title}\" to {label}");
            ShowInfo($"Moved \"{row.Title}\" to {label}");
        }
        catch (Exception ex) when (ex is TaskHubUnavailableException or TaskHubApiException)
        {
            ShowError("Couldn't move: " + ex.Message);
            if (ex is TaskHubUnavailableException u) SetOffline(u);
        }
        finally
        {
            row.IsBusy = false;
            _actionVersion++;
        }
        await RefreshAsync();
    }

    /// <summary>Snoozes the task's reminder by TaskHub's default snooze time.</summary>
    public async Task SnoozeAsync(TaskRow row)
    {
        if (row.IsBusy) return;
        _actionVersion++;
        row.IsBusy = true;
        try
        {
            var updated = await _client().SnoozeAsync(row.Id);
            RememberAction($"Snoozed \"{row.Title}\"");
            ShowInfo(updated.NextReminderUtc is { } r
                ? $"Reminder for \"{row.Title}\" snoozed until {Formatting.ReminderTime(r, DateTimeOffset.Now, TimeZoneInfo.Local)}"
                : $"Snoozed \"{row.Title}\"");
        }
        catch (Exception ex) when (ex is TaskHubUnavailableException or TaskHubApiException)
        {
            ShowError("Couldn't snooze: " + ex.Message);
            if (ex is TaskHubUnavailableException u) SetOffline(u);
        }
        finally
        {
            row.IsBusy = false;
            _actionVersion++;
        }
        await RefreshAsync();
    }

    private async Task RunDayPromptAsync()
    {
        var tasks = _dayPromptTasks;
        if (tasks.Count == 0) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var target = DayMode == DayMode.Morning ? today : DayPlanning.Tomorrow(today);
        await MoveManyAsync(tasks, target);
    }

    /// <summary>Moves several tasks to <paramref name="date"/>; one Undo puts them all back.</summary>
    public async Task MoveManyAsync(IReadOnlyList<TaskDto> tasks, DateOnly date)
    {
        _actionVersion++;
        var moved = new List<(Guid Id, DateOnly? Date)>();
        var client = _client();
        try
        {
            foreach (var t in tasks)
            {
                await client.PatchAsync(t.Id, new JsonObject { ["dueDate"] = date.ToString("yyyy-MM-dd") });
                moved.Add((t.Id, t.DueDate));
            }
        }
        catch (Exception ex) when (ex is TaskHubUnavailableException or TaskHubApiException)
        {
            ShowError($"Moved {moved.Count} of {tasks.Count}: {ex.Message}");
        }
        finally
        {
            _actionVersion++;
        }

        if (moved.Count > 0)
        {
            var label = DayPlanning.DayLabel(date, DateOnly.FromDateTime(DateTime.Now)).ToLowerInvariant();
            var what = $"Moved {Formatting.Plural(moved.Count, "task", "tasks")} to {label}";
            RememberAction(what, moved);
            if (!StatusIsError) ShowInfo(what);
        }
        await RefreshAsync();
    }

    public async Task QuickAddAsync()
    {
        var text = QuickAddText.Trim();
        if (text.Length == 0 || IsAdding) return;
        _actionVersion++;
        IsAdding = true;
        try
        {
            var task = await _client().QuickAddAsync(text);
            QuickAddText = "";
            RememberAction($"Added \"{task.Title}\"");
            var due = Formatting.DueLabel(task, DateOnly.FromDateTime(DateTime.Now));
            ShowInfo(due.Length == 0 ? $"Added \"{task.Title}\"" : $"Added \"{task.Title}\" · {due}");
        }
        catch (TaskHubApiException ex)
        {
            ShowError("Couldn't add: " + ex.Message);
        }
        catch (TaskHubUnavailableException ex)
        {
            ShowError("Couldn't add: " + ex.Message);
            SetOffline(ex);
        }
        finally
        {
            IsAdding = false;
            _actionVersion++;
        }
        await RefreshAsync();
    }

    public async Task UndoAsync()
    {
        _actionVersion++;
        try
        {
            if (_bulkUndo is { } bulk)
            {
                // A bulk move is several TaskHub changes; put each task back ourselves.
                var client = _client();
                foreach (var (id, date) in bulk)
                    await client.PatchAsync(id, new JsonObject { ["dueDate"] = date?.ToString("yyyy-MM-dd") });
                ShowInfo($"Undone: {_lastActionDescription}");
            }
            else
            {
                var description = await _client().UndoAsync();
                ShowInfo(description is null ? "Nothing to undo" : "Undone: " + description);
            }
            _lastActionDescription = null;
            _bulkUndo = null;
            _changedElsewhereSinceLastAction = false;
            Raise(nameof(CanUndo));
            Raise(nameof(UndoToolTip));
            // Undo restores whatever it restores; let struck-through rows come back if they reopened.
            for (var i = Rows.Count - 1; i >= 0; i--)
                if (Rows[i].IsCompleted) Rows.RemoveAt(i);
        }
        catch (TaskHubApiException ex)
        {
            ShowError("Couldn't undo: " + ex.Message);
        }
        catch (TaskHubUnavailableException ex)
        {
            ShowError("Couldn't undo: " + ex.Message);
            SetOffline(ex);
        }
        finally
        {
            _actionVersion++;
        }
        await RefreshAsync();
    }

    public async Task StartTaskHubAsync()
    {
        if (IsStartingTaskHub) return;
        var configured = _settings.Current.TaskHubExePath;
        var exe = TaskHubLocator.Find(configured);
        if (exe is null)
        {
            ShowError(string.IsNullOrWhiteSpace(configured)
                ? "Couldn't find TaskHub.exe. Set \"taskHubExePath\" in settings."
                : $"TaskHub.exe not found at {configured}. Fix \"taskHubExePath\" in settings.");
            return;
        }

        IsStartingTaskHub = true;
        ConnectionMessage = "Starting TaskHub…";
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            var home = _settings.Current.TaskHubHome;
            if (!string.IsNullOrWhiteSpace(home)) psi.Environment["TASKHUB_HOME"] = Environment.ExpandEnvironmentVariables(home);
            Process.Start(psi)?.Dispose();
            Log.Info($"Started {exe}");

            var client = _client();
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                if (await client.IsRunningAsync()) break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ShowError("Couldn't start TaskHub: " + ex.Message);
        }
        finally
        {
            IsStartingTaskHub = false;
        }
        await RefreshAsync();
        if (Connection == ConnectionState.NotRunning) ConnectionMessage = "TaskHub didn't start yet — try again in a moment";
    }

    private string RecomputeSummary()
    {
        if (!IsConnected) return Summary;
        // Locally completed rows no longer count; the next refresh brings exact numbers.
        var overdue = Rows.Count(r => r.IsOverdue && !r.IsCompleted);
        var today = Rows.Count(r => !r.IsOverdue && !r.IsCompleted);
        var done = Rows.Count(r => r.IsCompleted);
        Progress = new DayProgress(Math.Max(Progress.Done, done), overdue + today);
        var parts = new List<string> { overdue == 0 && today == 0 ? "Nothing due today" : $"{today} due today" };
        if (overdue > 0) parts.Add($"{overdue} overdue");
        if (Snapshot.NextReminderUtc is { } r && r > DateTimeOffset.Now)
            parts.Add("next reminder " + Formatting.ReminderTime(r, DateTimeOffset.Now, TimeZoneInfo.Local));
        return string.Join(" · ", parts);
    }

    private void RememberAction(string description, List<(Guid Id, DateOnly? Date)>? bulk = null)
    {
        _lastActionDescription = description;
        _bulkUndo = bulk;
        _changedElsewhereSinceLastAction = false;
        Raise(nameof(CanUndo));
        Raise(nameof(UndoToolTip));
    }

    private void ShowInfo(string message)
    {
        StatusIsError = false;
        Status = message;
        _statusClear.Stop();
        _statusClear.Start();
    }

    private void ShowError(string message)
    {
        Log.Warn(message);
        StatusIsError = true;
        Status = message;
        _statusClear.Stop();
    }

    public void ClearError()
    {
        if (StatusIsError) Status = null;
        StatusIsError = false;
    }

    internal static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { Log.Warn($"Couldn't open {path}", ex); }
        }
    }
}
