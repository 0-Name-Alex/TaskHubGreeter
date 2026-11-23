using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskHubGreeter.Client;

/// <summary>JSON settings matching TaskHub's API: camelCase, nulls omitted.</summary>
public static class TaskHubJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>A TaskHub task, as returned by the API (<c>TaskDto</c>).</summary>
public sealed record TaskDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string? Notes { get; init; }
    /// <summary><c>open</c> or <c>completed</c>.</summary>
    public string Status { get; init; } = TaskStatusNames.Open;
    /// <summary><c>none | low | medium | high | urgent</c>.</summary>
    public string Priority { get; init; } = PriorityNames.None;
    /// <summary>Local due date.</summary>
    public DateOnly? DueDate { get; init; }
    /// <summary>Local due time "HH:mm"; absent for all-day tasks.</summary>
    public string? DueTime { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public Guid? ListId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Recurrence { get; init; }
    public string? RecurrenceText { get; init; }
    public IReadOnlyList<int>? ReminderOffsets { get; init; }
    public int? RepeatReminderMinutes { get; init; }
    public DateTimeOffset? NextReminderUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public DateTimeOffset? DeletedUtc { get; init; }

    [JsonIgnore] public bool IsCompleted => string.Equals(Status, TaskStatusNames.Completed, StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsOpen => !IsCompleted && DeletedUtc is null;
    [JsonIgnore] public bool IsRecurring => !string.IsNullOrEmpty(Recurrence);
}

public static class TaskStatusNames
{
    public const string Open = "open";
    public const string Completed = "completed";
}

public static class PriorityNames
{
    public const string None = "none";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Urgent = "urgent";

    /// <summary>0 (none) … 4 (urgent); unknown values count as none.</summary>
    public static int Rank(string? priority) => priority?.ToLowerInvariant() switch
    {
        Low => 1,
        Medium => 2,
        High => 3,
        Urgent => 4,
        _ => 0,
    };
}

public static class TaskViews
{
    public const string Today = "today";
    public const string Overdue = "overdue";
    public const string Upcoming = "upcoming";
    public const string Completed = "completed";
}

/// <summary>Filters for <c>GET /v1/tasks</c>.</summary>
public sealed record TaskQuery
{
    public string? View { get; init; }
    public string? Text { get; init; }
    public string? Tag { get; init; }
    public Guid? ListId { get; init; }
    public bool IncludeCompleted { get; init; }
    public int? Limit { get; init; }

    public static TaskQuery ForView(string view, int? limit = null) => new() { View = view, Limit = limit };
}

public sealed record StatusDto(string Version, int ProcessId, DateTimeOffset Now, string TimeZone);

public sealed record TaskListDto(Guid Id, string Name, string? Color, int OpenCount);

public sealed record TagDto(string Name, int OpenCount);

/// <summary>Body of <c>POST /v1/tasks</c>. Use <see cref="Text"/> for quick-add syntax.</summary>
public sealed record CreateTaskRequest
{
    public string? Text { get; init; }
    public string? Title { get; init; }
    public string? Notes { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? DueTime { get; init; }
    public string? Priority { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public Guid? ListId { get; init; }
}

public sealed record NotifyButton(string Action, string Label);

/// <summary>Body of <c>POST /v1/notifications</c> (a TaskHub banner).</summary>
public sealed record NotifyRequest
{
    public required string Title { get; init; }
    public string? Body { get; init; }
    /// <summary><c>low | normal | high | urgent</c>.</summary>
    public string? Priority { get; init; }
    /// <summary>How long the banner stays, 0–3600; null = TaskHub default.</summary>
    public int? Seconds { get; init; }
    public IReadOnlyList<NotifyButton>? Buttons { get; init; }
    public string? Icon { get; init; }
    public bool? Sound { get; init; }
}

internal sealed record NotifyResult(Guid Id);

internal sealed record DndDto(bool Enabled);

internal sealed record UndoResultDto(string Description);

internal sealed record ApiError(string? Error);

internal sealed record SnoozeRequest(int Minutes);

/// <summary>Payload of a <c>notification.action</c> event.</summary>
public sealed record NotificationActionData(Guid NotificationId, string Action, IReadOnlyList<Guid>? TaskIds);

/// <summary>Payload of a <c>reminder.due</c> event.</summary>
public sealed record ReminderDueData(TaskDto Task, DateTimeOffset ScheduledUtc, bool Missed);
