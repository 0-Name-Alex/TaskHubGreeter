using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace TaskHubGreeter.App;

/// <summary>
/// Controls follow Windows light/dark through WPF's Fluent theme (ThemeMode="System"). This class adds
/// the few Greeter-specific brushes (overdue red, accent, chips) and refreshes them when the theme changes.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Apply(ResourceDictionary resources)
    {
        IsDark = ReadAppsUseDarkTheme();
        var accent = ReadAccentColor() ?? Color.FromRgb(0x00, 0x78, 0xD4);

        Set(resources, "Greeter.OverdueBrush", IsDark ? Color.FromRgb(0xFF, 0x6B, 0x6B) : Color.FromRgb(0xC4, 0x2B, 0x1C));
        Set(resources, "Greeter.WarningBrush", IsDark ? Color.FromRgb(0xFF, 0xC8, 0x57) : Color.FromRgb(0x9D, 0x5D, 0x00));
        Set(resources, "Greeter.SuccessBrush", IsDark ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x0F, 0x7B, 0x0F));
        Set(resources, "Greeter.AccentBrush", IsDark ? Lighten(accent, 0.35) : accent);
        Set(resources, "Greeter.ChipBrush", IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        Set(resources, "Greeter.PanelBrush", IsDark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
        Set(resources, "Greeter.FallbackBackground", IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF9, 0xF9, 0xF9));
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    private static bool ReadAppsUseDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static Color? ReadAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                var u = unchecked((uint)abgr);
                return Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return null;
    }
}
