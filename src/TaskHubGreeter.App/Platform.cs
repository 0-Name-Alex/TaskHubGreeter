using Microsoft.Win32;

namespace TaskHubGreeter.App;

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run entry (per-user, no admin).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskHubGreeter";
    public const string Argument = "--autostart";

    public static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
    }

    /// <summary>Adds/updates or removes the Run entry. Returns an error message, or null.</summary>
    public static string? Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                if (key.GetValue(ValueName) as string != Command) key.SetValue(ValueName, Command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("Could not change autostart", ex);
            return ex.Message;
        }
    }
}

/// <summary>
/// One Greeter per user session. A second launch asks the first to show its window, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showSignal, Action onShowRequested)
    {
        _mutex = mutex;
        _showSignal = showSignal;
        _registration = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static string Suffix => Environment.UserName.Replace('\\', '_');

    /// <summary>Returns the instance if this is the first; otherwise signals the first one (if <paramref name="signalExisting"/>) and returns null.</summary>
    public static SingleInstance? TryAcquire(Action onShowRequested, bool signalExisting)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\TaskHubGreeter-{Suffix}", out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\TaskHubGreeter-Show-{Suffix}");
        if (createdNew) return new SingleInstance(mutex, signal, onShowRequested);

        if (signalExisting) signal.Set();
        signal.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _showSignal.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
