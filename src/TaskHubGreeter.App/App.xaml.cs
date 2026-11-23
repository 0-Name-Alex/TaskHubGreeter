using System.Windows;
using System.Windows.Threading;

namespace TaskHubGreeter.App;

/// <summary>
/// Startup: single instance → theme → settings → autostart → controller → tray → hotkey and
/// Windows session/power triggers. Lives in the tray until Quit.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private SettingsStore? _settings;
    private GreeterController? _controller;
    private TrayIcon? _tray;
    private ShellMessages? _shell;
    private Presence? _presence;
    private string? _hotkeyError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var atSignIn = e.Args.Any(a => a.Equals(Autostart.Argument, StringComparison.OrdinalIgnoreCase));

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true; // never crash the tray app
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("Unobserved task exception", args.Exception.GetBaseException());
            args.SetObserved();
        };

        // A second launch (e.g. from the Start menu) just shows the running Greeter.
        _instance = SingleInstance.TryAcquire(() => Dispatcher.InvokeAsync(() => _controller?.ShowWindow(activate: true)), signalExisting: !atSignIn);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        Log.Info($"Greeter starting (atSignIn={atSignIn}, pid={Environment.ProcessId})");
        Theme.Apply(Resources);

        _settings = new SettingsStore(Dispatcher);
        _settings.Load();
        ApplyAutostart();

        _controller = new GreeterController(Dispatcher, _settings);
        _tray = new TrayIcon(_controller, _settings, () => _ = QuitAsync());
        _tray.Create();

        _shell = new ShellMessages();
        _shell.HotkeyPressed += (_, _) => _controller.ToggleWindow();
        _shell.SessionUnlocked += (_, _) => OnTrigger("unlock", s => s.AutoShow.Unlock, delay: TimeSpan.FromSeconds(1));
        _shell.Resumed += (_, _) =>
        {
            // Resuming to the lock screen: the unlock that follows will greet instead.
            if (_shell.IsLocked) return;
            OnTrigger("resume", s => s.AutoShow.Resume, delay: TimeSpan.FromSeconds(3));
        };
        _shell.ThemeChanged += (_, _) => Theme.Apply(Resources);
        RegisterHotkey();

        // "Welcome back" after a long stretch without keyboard/mouse input (without locking).
        _presence = new Presence(() => _shell?.IsLocked == true);
        _presence.Returned += (_, _) => OnTrigger("return", s => s.AutoShow.ReturnAfterIdleMinutes > 0, delay: TimeSpan.FromMilliseconds(500));
        _presence.Configure(_settings.Current.AutoShow.ReturnAfterIdleMinutes);

        _settings.Changed += async (_, _) =>
        {
            ApplyAutostart();
            RegisterHotkey();
            _presence?.Configure(_settings.Current.AutoShow.ReturnAfterIdleMinutes);
            await _controller.ApplySettingsAsync();
            if (_settings.LastError is { } err) _tray.SetToolTip("TaskHub Greeter — " + err);
        };

        _ = _controller.StartAsync(atSignIn, showNow: !atSignIn);
    }

    private void OnTrigger(string name, Func<GreeterSettings, bool> enabled, TimeSpan delay)
    {
        if (_settings is null || _controller is null || !enabled(_settings.Current)) return;
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (_shell?.IsLocked == true) return;
            await _controller.AutoShowAsync(name);
        };
        timer.Start();
    }

    private void RegisterHotkey()
    {
        if (_shell is null || _settings is null) return;
        var hotkey = _settings.Current.Hotkey;
        if (_shell.RegisterHotkey(hotkey, out var error))
        {
            _hotkeyError = null;
            _tray?.SetToolTip(string.IsNullOrWhiteSpace(hotkey) ? "TaskHub Greeter" : $"TaskHub Greeter ({hotkey})");
        }
        else if (error != _hotkeyError)
        {
            _hotkeyError = error;
            Log.Warn(error ?? "Hotkey registration failed");
            _tray?.SetToolTip("TaskHub Greeter — " + error);
        }
    }

    private void ApplyAutostart()
    {
        if (_settings is null) return;
        Autostart.Set(_settings.Current.StartWithWindows);
    }

    private async Task QuitAsync()
    {
        Log.Info("Greeter quitting");
        _presence?.Dispose();
        _presence = null;
        _shell?.Dispose();
        _shell = null;
        _tray?.Dispose();
        _tray = null;
        if (_controller is not null) await _controller.DisposeAsync();
        _controller = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        _tray?.Dispose();
        _settings?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
