using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace TaskHubGreeter.App;

public sealed class AutoShowSettings
{
    public bool SignIn { get; set; } = true;
    public bool Unlock { get; set; } = true;
    public bool Resume { get; set; } = true;

    /// <summary>Also greet when you come back after this many minutes without keyboard/mouse input (0 = off).</summary>
    public int ReturnAfterIdleMinutes { get; set; } = 15;

    /// <summary>Don't auto-show while a full-screen app, game or presentation is running.</summary>
    public bool QuietWhenFullScreen { get; set; } = true;
}

public sealed class WeatherSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>City name, e.g. "Chennai". Ignored when latitude/longitude are set.</summary>
    public string City { get; set; } = "";

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>"celsius" or "fahrenheit".</summary>
    public string Unit { get; set; } = "celsius";

    [JsonIgnore]
    public bool IsConfigured => Enabled && (Latitude is not null && Longitude is not null || !string.IsNullOrWhiteSpace(City));
}

public sealed class CalendarSettings
{
    /// <summary>iCal (.ics) links — Google Calendar's "secret address in iCal format", Outlook's published ICS link, …</summary>
    public List<string> IcsUrls { get; set; } = [];
}

/// <summary>Contents of %APPDATA%\TaskHubGreeter\settings.json.</summary>
public sealed class GreeterSettings
{
    /// <summary>Name used in the greeting ("Good morning, &lt;name&gt;").</summary>
    public string Name { get; set; } = "";

    /// <summary>Which Windows events show the Greeter automatically.</summary>
    public AutoShowSettings AutoShow { get; set; } = new();

    /// <summary>Automatic shows are at least this many minutes apart (manual shows ignore it).</summary>
    public int MinMinutesBetweenAutoShows { get; set; } = 30;

    /// <summary>Started by the "Start TaskHub" button. Empty = look for a TaskHub build next to this one.</summary>
    public string TaskHubExePath { get; set; } = "";

    /// <summary>TaskHub's home folder (where api.json lives). Empty = %TASKHUB_HOME% or %LOCALAPPDATA%\TaskHub.
    /// Set this to TaskHub's .devhome folder to talk to a dev instance started with run-dev.cmd.</summary>
    public string TaskHubHome { get; set; } = "";

    /// <summary>Show the summary as a TaskHub banner instead of the Greeter window.</summary>
    public bool UseTaskHubBanner { get; set; }

    /// <summary>Global hotkey that shows/hides the Greeter, e.g. "Ctrl+Alt+G". Empty = none.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+G";

    /// <summary>Close the window after this many seconds without input (0 = never).</summary>
    public int IdleCloseSeconds { get; set; } = 20;

    /// <summary>Register in HKCU\…\Run so the Greeter starts at sign-in.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Morning "plan your day" and evening "wrap up" prompts.</summary>
    public bool DayModes { get; set; } = true;

    /// <summary>How many days the "Coming up" section looks ahead (0 hides it).</summary>
    public int UpcomingDays { get; set; } = 3;

    public bool ShowQuote { get; set; } = true;

    /// <summary>Read the greeting aloud when the Greeter appears automatically.</summary>
    public bool SpeakGreeting { get; set; }

    public WeatherSettings Weather { get; set; } = new();
    public CalendarSettings Calendar { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DefaultName() : Name.Trim();

    [JsonIgnore]
    public TimeSpan MinTimeBetweenAutoShows => TimeSpan.FromMinutes(Math.Clamp(MinMinutesBetweenAutoShows, 0, 24 * 60));

    public static string DefaultName()
    {
        var user = Environment.UserName;
        return string.IsNullOrEmpty(user) ? "" : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(user.ToLowerInvariant());
    }

    public GreeterSettings Clone() => JsonSerializer.Deserialize<GreeterSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

/// <summary>Loads/saves settings.json and reloads it when the file is edited by hand.</summary>
public sealed class SettingsStore : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private DateTime _lastOwnWriteUtc;

    public SettingsStore(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskHubGreeter", "settings.json");
    }

    public string FilePath { get; }
    public GreeterSettings Current { get; private set; } = new();
    public string? LastError { get; private set; }

    /// <summary>Raised on the UI thread after the file changed and was re-read.</summary>
    public event EventHandler? Changed;

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            Current = new GreeterSettings { Name = GreeterSettings.DefaultName(), TaskHubExePath = TaskHubLocator.Find() ?? "" };
            Save();
            Log.Info($"Created {FilePath}");
        }
        else
        {
            Current = ReadFile() ?? Current;
            // Older file: write it back so the newer options show up for editing.
            try
            {
                if (LastError is null && !File.ReadAllText(FilePath).Contains("\"returnAfterIdleMinutes\"", StringComparison.Ordinal)) Save();
            }
            catch (IOException) { }
        }
        StartWatching();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _lastOwnWriteUtc = DateTime.UtcNow;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save settings", ex);
        }
    }

    public void Update(Action<GreeterSettings> change)
    {
        var copy = Current.Clone();
        change(copy);
        Current = copy;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private GreeterSettings? ReadFile()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var settings = JsonSerializer.Deserialize<GreeterSettings>(stream, JsonOptions) ?? new GreeterSettings();
                settings.AutoShow ??= new AutoShowSettings();
                settings.Weather ??= new WeatherSettings();
                settings.Calendar ??= new CalendarSettings();
                settings.Calendar.IcsUrls ??= [];
                LastError = null;
                return settings;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(100); // editor still writing
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LastError = $"settings.json couldn't be read: {ex.Message}";
                Log.Warn(LastError);
                return null;
            }
        }
        return null;
    }

    private void StartWatching()
    {
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _dispatcher.InvokeAsync(Debounce);
            _watcher.Created += (_, _) => _dispatcher.InvokeAsync(Debounce);
            _watcher.Renamed += (_, _) => _dispatcher.InvokeAsync(Debounce);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Log.Warn("Can't watch settings.json", ex);
        }
    }

    private void Debounce()
    {
        if (_debounce is null)
        {
            _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
            {
                _debounce!.Stop();
                if (DateTime.UtcNow - _lastOwnWriteUtc < TimeSpan.FromSeconds(1)) return;
                if (ReadFile() is { } s)
                {
                    Current = s;
                    Log.Info("Settings reloaded");
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }, _dispatcher);
        }
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Stop();
    }
}

/// <summary>Finds a TaskHub.exe to start when the setting is empty.</summary>
public static class TaskHubLocator
{
    public static string? Find(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            return File.Exists(path) ? path : null;
        }

        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TaskHub", "TaskHub.exe"),
        };
        // A TaskHub checkout next to the Greeter's (e.g. D:\Projects\TaskHub beside D:\Projects\TaskHubGreeter).
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var sibling = Path.Combine(dir.FullName, "TaskHub");
            candidates.Add(Path.Combine(sibling, "artifacts", "TaskHub", "TaskHub.exe"));
            candidates.Add(Path.Combine(sibling, "src", "TaskHub.App", "bin", "Release", "net10.0-windows", "TaskHub.exe"));
            candidates.Add(Path.Combine(sibling, "src", "TaskHub.App", "bin", "Debug", "net10.0-windows", "TaskHub.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
