using TaskHubGreeter.Client;

namespace TaskHubGreeter.App;

/// <summary>One task in the Greeter list.</summary>
public sealed class TaskRow : ObservableObject
{
    public const string OverdueSection = "Overdue";
    public const string TodaySection = "Today";

    private readonly Action<TaskRow, bool> _toggle;
    private TaskDto _task;
    private bool _isOverdue;
    private bool _isCompleted;
    private bool _isBusy;
    private string _dueLabel = "";
    private string? _doneNote;

    private readonly string? _sectionOverride;

    public TaskRow(TaskDto task, bool isOverdue, DateOnly today, Action<TaskRow, bool> toggle, string? section = null)
    {
        _task = task;
        _toggle = toggle;
        _sectionOverride = section;
        Update(task, isOverdue, today);
    }

    public Guid? ListId => _task.ListId;

    public Guid Id => _task.Id;
    public TaskDto Task => _task;
    public string Title => _task.Title;
    public string? Notes => string.IsNullOrWhiteSpace(_task.Notes) ? null : _task.Notes;
    public IReadOnlyList<string> Tags => _task.Tags;
    public bool HasTags => _task.Tags.Count > 0;
    public string PriorityMarker => Formatting.PriorityMarker(_task.Priority);
    public int PriorityRank => PriorityNames.Rank(_task.Priority);
    public string? RecurrenceText => _task.RecurrenceText;
    public bool IsRecurring => _task.IsRecurring;

    public string DueLabel { get => _dueLabel; private set => Set(ref _dueLabel, value); }

    public bool IsOverdue
    {
        get => _isOverdue;
        private set
        {
            if (Set(ref _isOverdue, value)) Raise(nameof(Section));
        }
    }

    /// <summary>The group the row is shown under.</summary>
    public string Section => _sectionOverride ?? (_isOverdue ? OverdueSection : TodaySection);

    /// <summary>Bound two-way to the check box. Setting it asks the view model to complete/reopen.</summary>
    public bool IsChecked
    {
        get => _isCompleted;
        set
        {
            if (value != _isCompleted && !_isBusy) _toggle(this, value);
        }
    }

    /// <summary>Completed during this session (kept visible, struck through, until the window closes).</summary>
    public bool IsCompleted => _isCompleted;

    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    /// <summary>e.g. "Done · next: Mon 9 Nov" after completing a repeating task.</summary>
    public string? DoneNote { get => _doneNote; private set => Set(ref _doneNote, value); }

    public string ToolTip
    {
        get
        {
            var parts = new List<string> { Title };
            if (Notes is not null) parts.Add(Notes);
            if (RecurrenceText is not null) parts.Add("Repeats: " + RecurrenceText);
            if (_task.NextReminderUtc is { } r) parts.Add("Reminder: " + r.ToLocalTime().ToString("ddd d MMM HH:mm"));
            return string.Join(Environment.NewLine, parts);
        }
    }

    public void SetCompleted(bool completed, string? note = null)
    {
        _isCompleted = completed;
        DoneNote = completed ? note : null;
        Raise(nameof(IsChecked));
        Raise(nameof(IsCompleted));
    }

    /// <summary>Refreshes the row from TaskHub's latest copy of the task.</summary>
    public void Update(TaskDto task, bool isOverdue, DateOnly today)
    {
        _task = task;
        IsOverdue = isOverdue;
        DueLabel = Formatting.DueLabel(task, today);
        if (_isCompleted && task.IsOpen) SetCompleted(false); // reopened elsewhere, or next occurrence still today
        Raise(nameof(Title));
        Raise(nameof(Notes));
        Raise(nameof(Tags));
        Raise(nameof(HasTags));
        Raise(nameof(PriorityMarker));
        Raise(nameof(PriorityRank));
        Raise(nameof(RecurrenceText));
        Raise(nameof(IsRecurring));
        Raise(nameof(ToolTip));
    }
}
