using System.Runtime.InteropServices;
using System.Windows.Threading;
using TaskHubGreeter.Client;

namespace TaskHubGreeter.App;

/// <summary>
/// Watches keyboard/mouse idle time (GetLastInputInfo) and raises <see cref="Returned"/> when you
/// come back after being away, plus helpers to tell whether a full-screen app or presentation is running.
/// </summary>
public sealed class Presence : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly IdleReturnDetector _detector = new(TimeSpan.FromMinutes(15));
    private readonly Func<bool> _isLocked;

    public Presence(Func<bool> isLocked)
    {
        _isLocked = isLocked;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            if (_detector.Update(IdleTime(), _isLocked())) Returned?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised on the UI thread when input resumes after the idle threshold.</summary>
    public event EventHandler? Returned;

    /// <summary>Minutes without input that count as "away"; 0 turns the trigger off.</summary>
    public void Configure(int minutes)
    {
        _detector.Threshold = TimeSpan.FromMinutes(Math.Max(0, minutes));
        if (minutes > 0) _timer.Start();
        else _timer.Stop();
    }

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    /// <summary>
    /// True while a full-screen app, game or presentation is in the foreground (Windows would hold
    /// its own notifications too). Uses SHQueryUserNotificationState.
    /// </summary>
    public static bool IsFullScreenBusy()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return false;
            return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void Dispose() => _timer.Stop();

    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);
}

/// <summary>Speaks the greeting with the built-in Windows voice (SAPI), no extra packages.</summary>
public sealed class Speaker : IDisposable
{
    private object? _voice;

    public void Speak(string text)
    {
        try
        {
            if (_voice is null)
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type is null)
                {
                    Log.Warn("No Windows speech voice available");
                    return;
                }
                _voice = Activator.CreateInstance(type);
            }
            // SVSFlagsAsync (1) | SVSFPurgeBeforeSpeak (2): don't block, cut off anything still speaking.
            ((dynamic)_voice!).Speak(text, 3);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException)
        {
            Log.Warn("Speech failed", ex);
        }
    }

    public void Dispose()
    {
        if (_voice is not null && Marshal.IsComObject(_voice)) Marshal.FinalReleaseComObject(_voice);
        _voice = null;
    }
}
