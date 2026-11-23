using System.Text.Json;

namespace TaskHubGreeter.Client;

/// <summary>Where and how to reach a running TaskHub.</summary>
public sealed record TaskHubEndpoint(int Port, string Token, int? ProcessId, string? Version)
{
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");
    public Uri EventsUri(string topics) => new($"ws://127.0.0.1:{Port}/v1/events?topics={Uri.EscapeDataString(topics)}");
}

/// <summary>
/// Reads <c>api.json</c> (and the token file it points to). Location, in order: an explicit
/// TaskHub home folder (setting), <c>%TASKHUB_HOME%</c>, then <c>%LOCALAPPDATA%\TaskHub</c>.
/// </summary>
public sealed class TaskHubDiscovery
{
    private readonly IReadOnlyList<string> _candidates;

    public TaskHubDiscovery(string? taskHubHome = null) =>
        _candidates = CandidateHomes(taskHubHome).Select(h => Path.Combine(h, "api.json")).ToList();

    /// <summary>The api.json in use: the first candidate that exists (else the primary location).</summary>
    public string ApiJsonPath => _candidates.FirstOrDefault(File.Exists) ?? _candidates[0];

    /// <summary>
    /// An explicit home or %TASKHUB_HOME% is used alone. Otherwise %LOCALAPPDATA%\TaskHub, then a TaskHub
    /// dev instance (<c>TaskHub\.devhome</c>, from its run-dev.cmd) in a checkout beside this app.
    /// </summary>
    public static IReadOnlyList<string> CandidateHomes(string? taskHubHome)
    {
        if (!string.IsNullOrWhiteSpace(taskHubHome) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TASKHUB_HOME")))
            return [ResolveHome(taskHubHome)];
        var homes = new List<string> { ResolveHome(null) };
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var dev = Path.Combine(dir.FullName, "TaskHub", ".devhome");
            if (Directory.Exists(dev)) homes.Add(dev);
        }
        return homes;
    }

    public static string ResolveHome(string? taskHubHome)
    {
        if (!string.IsNullOrWhiteSpace(taskHubHome)) return Environment.ExpandEnvironmentVariables(taskHubHome.Trim());
        var env = Environment.GetEnvironmentVariable("TASKHUB_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskHub");
    }

    public static string ResolveApiJsonPath(string? taskHubHome) => Path.Combine(ResolveHome(taskHubHome), "api.json");

    /// <summary>Reads the current endpoint. Throws <see cref="TaskHubUnavailableException"/> if TaskHub isn't running.</summary>
    public TaskHubEndpoint Read()
    {
        var apiJsonPath = ApiJsonPath;
        if (!File.Exists(apiJsonPath))
            throw new TaskHubUnavailableException(UnavailableReason.NotRunning, $"TaskHub isn't running ({apiJsonPath} not found).");

        try
        {
            using var doc = JsonDocument.Parse(ReadShared(apiJsonPath));
            var root = doc.RootElement;
            var port = root.GetProperty("port").GetInt32();
            var tokenFile = root.GetProperty("tokenFile").GetString();
            if (port is <= 0 or > 65535 || string.IsNullOrWhiteSpace(tokenFile))
                throw new TaskHubUnavailableException(UnavailableReason.NotRunning, "api.json is incomplete.");
            if (!Path.IsPathRooted(tokenFile))
                tokenFile = Path.Combine(Path.GetDirectoryName(apiJsonPath) ?? "", tokenFile);

            int? processId = root.TryGetProperty("processId", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt32() : null;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var token = ReadShared(tokenFile).Trim();
            if (token.Length == 0)
                throw new TaskHubUnavailableException(UnavailableReason.NotRunning, "The TaskHub token file is empty.");
            return new TaskHubEndpoint(port, token, processId, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // TaskHub may be mid-write or just exited; treat as not running (caller retries later).
            throw new TaskHubUnavailableException(UnavailableReason.NotRunning, $"Couldn't read TaskHub's api.json: {ex.Message}", ex);
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
