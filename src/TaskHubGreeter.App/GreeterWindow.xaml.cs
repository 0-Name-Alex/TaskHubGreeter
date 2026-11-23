using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TaskHubGreeter.Client;

namespace TaskHubGreeter.App;

/// <summary>
/// The Greeter: a small centred, borderless, topmost window. Hidden (not closed) on Esc, the close
/// button or after a period without input, so it can be shown again instantly.
/// </summary>
public partial class GreeterWindow : Window
{
    private readonly GreeterViewModel _vm;
    private readonly Func<int> _idleSeconds;
    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastInputUtc = DateTime.UtcNow;
    private Point _lastMouse;

    public GreeterWindow(GreeterViewModel vm, Func<int> idleSeconds)
    {
        _vm = vm;
        _idleSeconds = idleSeconds;
        InitializeComponent();
        DataContext = vm;

        Root.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        Loaded += (_, _) =>
        {
            if (Root.Background is null) Root.SetResourceReference(Border.BackgroundProperty, "Greeter.FallbackBackground");
        };

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += OnIdleTick;

        PreviewKeyDown += OnWindowKeyDown;
        PreviewMouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            if (p != _lastMouse) Touch();
            _lastMouse = p;
        };
        PreviewMouseDown += (_, _) => Touch();
        PreviewMouseWheel += (_, _) => Touch();
        SizeChanged += (_, _) => CenterOnScreen();
        Deactivated += (_, _) => Topmost = false; // don't sit on top of whatever the user switched to
        _vm.RowsChanged += (_, _) => RestoreFocusAfterSync();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GreeterViewModel.IsSearchOpen) && _vm.IsSearchOpen)
                Dispatcher.InvokeAsync(() => SearchBox.Focus(), DispatcherPriority.Input);
        };

        TaskList.ContextMenu = BuildRowMenu();
    }

    /// <summary>Raised when the window hides itself.</summary>
    public event EventHandler? Dismissed;

    public void ShowGreeter(bool activate)
    {
        Touch();
        Topmost = true;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 16);
        if (!IsVisible)
        {
            CenterOnScreen();
            Show();
        }
        if (activate)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        _idleTimer.Start();
        FocusInitial();
    }

    public void HideGreeter()
    {
        if (!IsVisible) return;
        _idleTimer.Stop();
        Hide();
        _vm.OnHidden();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void Touch() => _lastInputUtc = DateTime.UtcNow;

    private void OnIdleTick(object? sender, EventArgs e)
    {
        var seconds = _idleSeconds();
        if (seconds <= 0) return;
        var idle = DateTime.UtcNow - _lastInputUtc;
        // Don't close under the user's mouse, while they're typing, or mid-request.
        if (IsMouseOver || QuickAdd.Text.Length > 0 || SearchBox.IsKeyboardFocusWithin || TaskList.ContextMenu?.IsOpen == true || _vm.IsBusy) return;
        if (idle >= TimeSpan.FromSeconds(seconds)) HideGreeter();
    }

    private void CenterOnScreen()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) UpdateLayout();
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 400;
        Left = area.Left + (area.Width - width) / 2;
        Top = area.Top + Math.Max(0, (area.Height - height) / 2.4); // a little above centre
    }

    private void FocusInitial()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_vm.IsSearchOpen)
            {
                SearchBox.Focus();
                return;
            }
            var rows = _vm.VisibleRows;
            if (_vm.IsConnected && rows.Count > 0 && FocusRow(_vm.SelectedRow is { } s && rows.Contains(s) ? s : rows[0])) return;
            if (QuickAdd.IsVisible) QuickAdd.Focus();
            else Root.Focus();
        }, DispatcherPriority.Input);
    }

    private bool FocusRow(TaskRow row)
    {
        TaskList.SelectedItem = row;
        TaskList.ScrollIntoView(row);
        TaskList.UpdateLayout();
        if (TaskList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
        {
            item.Focus();
            return true;
        }
        return false;
    }

    private void RestoreFocusAfterSync()
    {
        if (!IsVisible || SearchBox.IsKeyboardFocusWithin || QuickAdd.IsKeyboardFocusWithin) return;
        // First load after showing: put focus on the first task.
        if (!TaskList.IsKeyboardFocusWithin && IsActive)
        {
            FocusInitial();
            return;
        }
        var rows = _vm.VisibleRows;
        if (TaskList.IsKeyboardFocusWithin && (_vm.SelectedRow is null || !rows.Contains(_vm.SelectedRow)) && rows.Count > 0)
            Dispatcher.InvokeAsync(() => FocusRow(rows[0]), DispatcherPriority.Input);
    }

    private TaskRow? CurrentRow => TaskList.SelectedItem as TaskRow;

    // ------------------------------------------------------------------ keyboard

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        Touch();
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (e.Key == Key.Escape)
        {
            if (QuickAdd.IsKeyboardFocusWithin && QuickAdd.Text.Length > 0) QuickAdd.Clear();
            else if (_vm.IsSearchOpen) _vm.IsSearchOpen = false;
            else HideGreeter();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z)
        {
            // Let the text boxes undo typing; otherwise undo the last TaskHub change.
            if (QuickAdd.IsKeyboardFocusWithin && QuickAdd.CanUndo) return;
            if (SearchBox.IsKeyboardFocusWithin && SearchBox.CanUndo) return;
            _ = _vm.UndoAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            if (_vm.IsSearchOpen) SearchBox.Focus();
            else _vm.IsSearchOpen = true;
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = _vm.RefreshAsync();
            e.Handled = true;
        }
        else if (ctrl && CurrentRow is { } row && !QuickAdd.IsKeyboardFocusWithin && !SearchBox.IsKeyboardFocusWithin)
        {
            switch (e.Key)
            {
                case Key.T:
                    _ = _vm.RescheduleAsync(row, shift ? DayPlanning.NextWeek(today) : DayPlanning.Tomorrow(today));
                    e.Handled = true;
                    break;
                case Key.D:
                    _ = _vm.RescheduleAsync(row, today);
                    e.Handled = true;
                    break;
                case Key.S:
                    _ = _vm.SnoozeAsync(row);
                    e.Handled = true;
                    break;
            }
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && CurrentRow is { } row)
        {
            _ = _vm.ToggleAsync(row, !row.IsChecked);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && TaskList.SelectedIndex == TaskList.Items.Count - 1)
        {
            QuickAdd.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && TaskList.SelectedIndex == 0 && _vm.IsSearchOpen)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
        {
            if (TaskList.ContextMenu is { } menu && CurrentRow is not null)
            {
                menu.PlacementTarget = TaskList.ItemContainerGenerator.ContainerFromItem(CurrentRow) as UIElement ?? TaskList;
                menu.IsOpen = true;
            }
            e.Handled = true;
        }
    }

    /// <summary>Typing while the list has focus goes to quick add.</summary>
    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || e.Text == " ") return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
        QuickAdd.Focus();
        QuickAdd.Text += e.Text;
        QuickAdd.CaretIndex = QuickAdd.Text.Length;
        e.Handled = true;
    }

    private void OnQuickAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = _vm.QuickAddAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _vm.VisibleRows is { Count: > 0 } rows)
        {
            FocusRow(rows[^1]);
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Down || e.Key == Key.Enter) && _vm.VisibleRows is { Count: > 0 } rows)
        {
            FocusRow(rows[0]);
            e.Handled = true;
        }
    }

    private void OnQuickAddChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = QuickAdd.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (QuickAdd.Text.Length > 0) _vm.ClearError();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideGreeter();

    // ------------------------------------------------------------------ right-click menu

    private ContextMenu BuildRowMenu()
    {
        var menu = new ContextMenu();
        var complete = Item("Complete", "Space", r => _ = _vm.ToggleAsync(r, !r.IsChecked));
        menu.Items.Add(complete);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move to today", "Ctrl+D", r => _ = _vm.RescheduleAsync(r, Today())));
        menu.Items.Add(Item("Move to tomorrow", "Ctrl+T", r => _ = _vm.RescheduleAsync(r, DayPlanning.Tomorrow(Today()))));
        menu.Items.Add(Item("Move to next week (Mon)", "Ctrl+Shift+T", r => _ = _vm.RescheduleAsync(r, DayPlanning.NextWeek(Today()))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Snooze reminder", "Ctrl+S", r => _ = _vm.SnoozeAsync(r)));

        menu.Opened += (_, _) =>
        {
            Touch();
            if (CurrentRow is not { } row)
            {
                menu.IsOpen = false;
                return;
            }
            complete.Header = row.IsChecked ? "Mark as not done" : "Complete";
        };
        return menu;
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    private MenuItem Item(string header, string gesture, Action<TaskRow> action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) =>
        {
            if (CurrentRow is { } row) action(row);
        };
        return item;
    }
}
