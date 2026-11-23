using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace TaskHubGreeter.App;

/// <summary>Tray icon: left-click shows/hides; menu has Show / Settings / toggles / Quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly GreeterController _controller;
    private readonly SettingsStore _settings;
    private readonly Action _quit;
    private TaskbarIcon? _icon;
    private MenuItem? _bannerItem;
    private MenuItem? _autostartItem;
    private MenuItem? _speakItem;
    private MenuItem? _quoteItem;

    public TrayIcon(GreeterController controller, SettingsStore settings, Action quit)
    {
        _controller = controller;
        _settings = settings;
        _quit = quit;
    }

    public void Create()
    {
        if (_icon is not null) return;

        _bannerItem = new MenuItem { Header = "Use TaskHub banner", IsCheckable = true };
        _bannerItem.Click += (_, _) => _settings.Update(s => s.UseTaskHubBanner = _bannerItem.IsChecked);

        _autostartItem = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        _autostartItem.Click += (_, _) => _settings.Update(s => s.StartWithWindows = _autostartItem.IsChecked);

        _speakItem = new MenuItem { Header = "Speak greeting on auto-show", IsCheckable = true };
        _speakItem.Click += (_, _) => _settings.Update(s => s.SpeakGreeting = _speakItem.IsChecked);

        _quoteItem = new MenuItem { Header = "Show quote of the day", IsCheckable = true };
        _quoteItem.Click += (_, _) => _settings.Update(s => s.ShowQuote = _quoteItem.IsChecked);

        var menu = new ContextMenu();
        menu.Items.Add(Item("Show today", () => _controller.ShowWindow(activate: true), bold: true));
        menu.Items.Add(Item("Show as TaskHub banner", () => _ = ShowBannerAsync()));
        menu.Items.Add(Item("Read today aloud", () => _ = _controller.SpeakNowAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", () => GreeterViewModel.OpenFile(_settings.FilePath)));
        menu.Items.Add(_bannerItem);
        menu.Items.Add(_speakItem);
        menu.Items.Add(_quoteItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(Item("Open log folder", () => GreeterViewModel.OpenFile(Log.Folder)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Greeter", _quit));
        menu.Opened += (_, _) =>
        {
            _bannerItem.IsChecked = _settings.Current.UseTaskHubBanner;
            _autostartItem.IsChecked = _settings.Current.StartWithWindows;
            _speakItem.IsChecked = _settings.Current.SpeakGreeting;
            _quoteItem.IsChecked = _settings.Current.ShowQuote;
        };

        _icon = new TaskbarIcon
        {
            ToolTipText = "TaskHub Greeter",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/Greeter.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => _controller.ToggleWindow()),
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void SetToolTip(string text)
    {
        if (_icon is not null) _icon.ToolTipText = text;
    }

    private async Task ShowBannerAsync()
    {
        if (!await _controller.ShowBannerAsync()) _controller.ShowWindow(activate: true);
    }

    private static MenuItem Item(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
