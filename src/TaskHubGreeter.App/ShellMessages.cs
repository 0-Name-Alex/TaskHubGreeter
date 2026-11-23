using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TaskHubGreeter.App;

/// <summary>
/// A hidden top-level window that receives the Windows messages the Greeter reacts to:
/// session lock/unlock (WTS), resume from sleep (WM_POWERBROADCAST), the global hotkey
/// (RegisterHotKey) and theme changes (WM_SETTINGCHANGE "ImmersiveColorSet").
/// All events are raised on the UI thread.
/// </summary>
public sealed class ShellMessages : IDisposable
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int WM_HOTKEY = 0x0312;

    private const int PBT_APMRESUMESUSPEND = 0x7;
    private const int PBT_APMRESUMEAUTOMATIC = 0x12;

    private const int WTS_CONSOLE_CONNECT = 0x1;
    private const int WTS_REMOTE_CONNECT = 0x3;
    private const int WTS_SESSION_LOGON = 0x5;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 0x4748; // "GH"

    private readonly HwndSource _source;
    private bool _hotkeyRegistered;
    private bool _sessionRegistered;
    private DateTime _lastResumeUtc;

    public ShellMessages()
    {
        var parameters = new HwndSourceParameters("TaskHubGreeter.Messages")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x00000080,         // WS_EX_TOOLWINDOW
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _sessionRegistered = WTSRegisterSessionNotification(_source.Handle, 0 /* NOTIFY_FOR_THIS_SESSION */);
        if (!_sessionRegistered) Log.Warn($"WTSRegisterSessionNotification failed ({Marshal.GetLastWin32Error()})");
    }

    public event EventHandler? HotkeyPressed;
    public event EventHandler? SessionUnlocked;
    public event EventHandler? SessionLocked;
    public event EventHandler? Resumed;
    public event EventHandler? ThemeChanged;

    /// <summary>True between a lock and the next unlock.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>Registers e.g. "Ctrl+Alt+G". Returns false (with a reason) if it's invalid or taken by another app.</summary>
    public bool RegisterHotkey(string? gesture, out string? error)
    {
        UnregisterHotkey();
        error = null;
        if (string.IsNullOrWhiteSpace(gesture)) return true;
        if (!TryParseHotkey(gesture, out var modifiers, out var vk))
        {
            error = $"Hotkey \"{gesture}\" isn't valid. Use e.g. Ctrl+Alt+G.";
            return false;
        }
        _hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | MOD_NOREPEAT, vk);
        if (!_hotkeyRegistered) error = $"Hotkey {gesture} is already used by another app.";
        return _hotkeyRegistered;
    }

    public void UnregisterHotkey()
    {
        if (!_hotkeyRegistered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    internal static bool TryParseHotkey(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        Key? key = null;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win" or "windows": modifiers |= MOD_WIN; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (key is not null || !Enum.TryParse<Key>(name, ignoreCase: true, out var k) || k == Key.None) return false;
                    key = k;
                    break;
            }
        }
        if (key is null || modifiers == 0) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case WM_WTSSESSION_CHANGE:
                switch (wParam.ToInt32())
                {
                    case WTS_SESSION_LOCK:
                        IsLocked = true;
                        SessionLocked?.Invoke(this, EventArgs.Empty);
                        break;
                    case WTS_SESSION_UNLOCK:
                    case WTS_SESSION_LOGON:
                    case WTS_CONSOLE_CONNECT:
                    case WTS_REMOTE_CONNECT:
                        var wasLocked = IsLocked;
                        IsLocked = false;
                        if (wasLocked || wParam.ToInt32() == WTS_SESSION_UNLOCK) SessionUnlocked?.Invoke(this, EventArgs.Empty);
                        break;
                }
                break;

            case WM_POWERBROADCAST:
                var evt = wParam.ToInt32();
                // Both resume messages usually arrive; report once.
                if (evt is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND && DateTime.UtcNow - _lastResumeUtc > TimeSpan.FromSeconds(30))
                {
                    _lastResumeUtc = DateTime.UtcNow;
                    Resumed?.Invoke(this, EventArgs.Empty);
                }
                break;

            case WM_SETTINGCHANGE:
                if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotkey();
        if (_sessionRegistered) WTSUnRegisterSessionNotification(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
