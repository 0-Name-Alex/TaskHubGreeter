namespace TaskHubGreeter.Client;

public enum UnavailableReason
{
    /// <summary>No api.json, or nothing is listening on its port.</summary>
    NotRunning,

    /// <summary>TaskHub accepted the connection but didn't answer within the timeout.</summary>
    Timeout,

    /// <summary>Any other transport failure.</summary>
    Unreachable,
}

/// <summary>TaskHub can't be reached (not running, timed out, …). The UI shows a friendly state for this.</summary>
public sealed class TaskHubUnavailableException : Exception
{
    public TaskHubUnavailableException(UnavailableReason reason, string message, Exception? inner = null)
        : base(message, inner) => Reason = reason;

    public UnavailableReason Reason { get; }
}

/// <summary>TaskHub answered with an error status (400, 401, 403, 404, 409, 421, 5xx).</summary>
public sealed class TaskHubApiException : Exception
{
    public TaskHubApiException(int statusCode, string message)
        : base(message) => StatusCode = statusCode;

    public int StatusCode { get; }
}
