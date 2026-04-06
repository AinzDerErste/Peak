using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Peak.App.ViewModels;
using Peak.App.Views.Widgets;
using System.Windows.Shapes;
using Peak.Core.Configuration;
using Peak.Core.Services;
using Peak.Platform.Native;

namespace Peak.App.Views;

public partial class IslandWindow : Window
{
    private readonly IslandViewModel _viewModel;
    private IntPtr _hwnd;
    private bool _hiddenByFullscreen;
    private IslandState _currentAnimatedState = IslandState.Collapsed;
    private readonly DispatcherTimer _fullscreenCheckTimer;
    private readonly ScaleTransform _scaleTransform;
    private readonly TranslateTransform _translateTransform;

    // Win32 constants for blocking minimize
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;

    // Target sizes for each state
    private static readonly (double W, double H) CollapsedSize = (210, 36);
    private static readonly (double W, double H) PeekSize = (400, 56);
    // Fixed window size
    private const double FixedWindowWidth = 520;
    private const double FixedWindowHeight = 400;
    private const double TopOffset = 8;

    // GPU-accelerated animation — state-specific timing
    private static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(300));
    private static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration PeekDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration AnimDuration = new(TimeSpan.FromMilliseconds(280)); // fallback / hide-unhide
    private static readonly IEasingFunction AnimEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction ExpandEase = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

    // Current actual size
    private double _currentW;
    private double _currentH;

    // Slot hosts and edit overlays
    private ContentControl[] _slotHosts = null!;
    private Border[] _editOverlays = null!;

    // Row containers and separators for dynamic collapse
    private FrameworkElement[] _widgetRows = null!;
    private Border[] _separators = null!;

    // Row mode toggle buttons (edit mode)
    private Border[] _rowToggles = null!;
    private TextBlock[] _rowToggleIcons = null!;
    private ColumnDefinition[] _rightColumns = null!;

    // Polling timer — checks real cursor position to decide collapse
    private DispatcherTimer? _mousePollingTimer;

    // Audio visualizer
    private AudioVisualizerService? _audioVisualizer;
    private readonly Rectangle[] _vizBarsRight = new Rectangle[AudioVisualizerService.BarCount];
    private bool _visualizerRunning;
    private UIElement? _visualizerOverride;

    // Plugin extension points
    public Func<CollapsedWidget, FrameworkElement?>? ExternalCollapsedRenderer { get; set; }

    // Unhide greeting messages (keep short — must fit ~200px)
    private static readonly string[] _greetings =
    [
        "Miss me already? 😏",
        "Back so soon?",
        "Oh, you again.",
        "I was napping...",
        "Plot twist: I'm back.",
        "Get anything done?",
        "I was hiding. Rude.",
        "Loading... done.",
        "I'm back, baby.",
        "Shadow realm exit.",
        "Task failed: hide.",
        "Our little secret.",
        "MacBook who?",
        "Island at home:",
        "Bro pressed it 💀",
        "Commitment issues?",
        "Do you see me?",
        "Respawning...",
        "*yawns* What year?",
        "Unhider unlocked.",
        "Magic buttons go!",
        "Hey. Hi. Hello.",
        "Not again...",
        "Peek-a-boo!",
        "Sup.",
    ];
    private static readonly Random _greetingRng = new();
    private int _lastGreetingIndex = -1;

    public IslandWindow(IslandViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _scaleTransform = new ScaleTransform(1, 1);
        _translateTransform = new TranslateTransform(0, 0);

        Loaded += OnLoaded;
        Closed += (_, _) => UnregisterGlobalHotkey();
        StateChanged += OnStateChanged;
        Deactivated += OnDeactivated;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _fullscreenCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenCheckTimer.Tick += OnFullscreenCheck;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Install WndProc hook to block minimize at Win32 level
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        DwmApi.EnableDarkMode(_hwnd);
        User32.HideFromAltTab(_hwnd);
        RegisterGlobalHotkey();

        Width = FixedWindowWidth;
        Height = FixedWindowHeight;
        PositionWindowOnce();

        _currentW = CollapsedSize.W;
        _currentH = CollapsedSize.H;
        IslandBorder.Width = _currentW;
        IslandBorder.Height = _currentH;

        IslandBorder.RenderTransformOrigin = new Point(0.5, 0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_scaleTransform);
        transformGroup.Children.Add(_translateTransform);
        IslandBorder.RenderTransform = transformGroup;

        IslandBorder.MouseEnter += OnIslandMouseEnter;
        IslandBorder.MouseLeave += OnIslandMouseLeave;

        // Drag-over expands the island so user can drop files
        IslandBorder.DragEnter += OnIslandDragEnter;
        IslandBorder.DragLeave += OnIslandDragLeave;
        IslandBorder.DragOver += OnIslandDragOver;
        IslandBorder.Drop += OnIslandDrop;

        // Initialize slot hosts and edit overlays
        _slotHosts = [SlotHost0, SlotHost1, SlotHost2, SlotHost3, SlotHost4, SlotHost5];
        _editOverlays = [EditOverlay0, EditOverlay1, EditOverlay2, EditOverlay3, EditOverlay4, EditOverlay5];
        _widgetRows = [WidgetRow0, WidgetRow1, WidgetRow2];
        _separators = [Separator01, Separator12];
        _rowToggles = [RowToggle0, RowToggle1, RowToggle2];
        _rowToggleIcons = [RowToggleIcon0, RowToggleIcon1, RowToggleIcon2];
        _rightColumns = [Col1Row0, Col1Row1, Col1Row2];

        // Apply row modes from settings then render
        for (int i = 0; i < 3; i++) ApplyRowMode(i);
        RenderAllSlots();
        RenderCollapsedSlots();
        UpdateRowVisibility();
        ApplyBorderSetting();

        _fullscreenCheckTimer.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            Dispatcher.Invoke(PositionWindowOnce);

        // Initialize audio visualizer bars
        InitVisualizerBars();
        if (Application.Current is App app)
            _audioVisualizer = app.Services.GetService(typeof(AudioVisualizerService)) as AudioVisualizerService;

        IslandBorder.SizeChanged += (_, _) => PositionVisualizerCircle();
    }

    private void PositionWindowOnce()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - FixedWindowWidth) / 2;
        Top = workArea.Top + TopOffset;
    }

    private void ApplyBorderSetting()
    {
        if (_viewModel.Settings.ShowBorder)
        {
            IslandBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
            IslandBorder.BorderThickness = new Thickness(0.8);
        }
        else
        {
            IslandBorder.BorderBrush = Brushes.Transparent;
            IslandBorder.BorderThickness = new Thickness(0);
        }
    }

    // ─── Widget Slot Rendering ───────────────────────────────────

    private void RenderAllSlots()
    {
        RenderSlot(0, _viewModel.Slot0);
        RenderSlot(1, _viewModel.Slot1);
        RenderSlot(2, _viewModel.Slot2);
        RenderSlot(3, _viewModel.Slot3);
        RenderSlot(4, _viewModel.Slot4);
        RenderSlot(5, _viewModel.Slot5);
    }

    private void RenderSlot(int index, WidgetType type)
    {
        if (_slotHosts == null || index < 0 || index >= _slotHosts.Length) return;

        var host = _slotHosts[index];
        host.Content = type switch
        {
            WidgetType.Clock => new ClockWidget { DataContext = _viewModel },
            WidgetType.Weather => new WeatherWidget { DataContext = _viewModel },
            WidgetType.Media => new MediaWidget { DataContext = _viewModel },
            WidgetType.SystemMonitor => new SystemMonitorWidget { DataContext = _viewModel },
            WidgetType.Calendar => new CalendarWidget { DataContext = _viewModel },
            WidgetType.Timer => new TimerWidget { DataContext = _viewModel },
            WidgetType.Network => new NetworkWidget { DataContext = _viewModel },
            WidgetType.QuickAccess => new QuickAccessWidget { DataContext = _viewModel },
            WidgetType.Clipboard => new ClipboardWidget { DataContext = _viewModel },
            WidgetType.QuickNotes => new QuickNotesWidget { DataContext = _viewModel },
            WidgetType.VolumeMixer => new VolumeMixerWidget { DataContext = _viewModel },
            WidgetType.Pomodoro => new PomodoroWidget { DataContext = _viewModel },
            _ => null
        };

        UpdateRowVisibility();
    }

    private void ApplyRowMode(int row)
    {
        if (_rightColumns == null) return;
        var mode = _viewModel.GetRowMode(row);
        var isWide = mode == RowMode.Wide;
        var leftSlotIndex = row * 2;
        var rightSlotIndex = row * 2 + 1;

        // Hide/show right column
        _rightColumns[row].Width = isWide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        // ColumnSpan for left slot host + overlay
        Grid.SetColumnSpan(_slotHosts[leftSlotIndex], isWide ? 2 : 1);
        Grid.SetColumnSpan(_editOverlays[leftSlotIndex], isWide ? 2 : 1);

        // Adjust left slot margin (no right margin in wide mode)
        _slotHosts[leftSlotIndex].Margin = isWide ? new Thickness(0, 4, 0, 4) : new Thickness(0, 4, 6, 4);
        _editOverlays[leftSlotIndex].Margin = isWide ? new Thickness(0) : new Thickness(0, 0, 6, 0);

        // Hide right slot + overlay in wide mode
        _slotHosts[rightSlotIndex].Visibility = isWide ? Visibility.Collapsed : Visibility.Visible;
        _editOverlays[rightSlotIndex].Visibility = isWide ? Visibility.Collapsed :
            (_viewModel.IsEditMode ? Visibility.Visible : Visibility.Collapsed);

        // Update toggle icon
        if (_rowToggleIcons != null)
            _rowToggleIcons[row].Text = isWide ? "⇒" : "⇔";
    }

    private void OnRowToggleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string tagStr && int.TryParse(tagStr, out var row))
        {
            var current = _viewModel.GetRowMode(row);
            var newMode = current == RowMode.TwoSlots ? RowMode.Wide : RowMode.TwoSlots;
            _viewModel.SetRowMode(row, newMode);
            ApplyRowMode(row);
            RenderSlot(row * 2, GetSlot(row * 2));
            if (newMode == RowMode.TwoSlots)
                RenderSlot(row * 2 + 1, GetSlot(row * 2 + 1));
            UpdateRowVisibility();
        }
    }

    private WidgetType GetSlot(int index) => index switch
    {
        0 => _viewModel.Slot0, 1 => _viewModel.Slot1,
        2 => _viewModel.Slot2, 3 => _viewModel.Slot3,
        4 => _viewModel.Slot4, 5 => _viewModel.Slot5,
        _ => WidgetType.None
    };

    private void UpdateRowVisibility()
    {
        if (_widgetRows == null) return;

        // Check each row: visible if at least one slot has a widget (or in edit mode)
        // For wide rows, only check the left slot
        bool[] rowHasContent = new bool[3];
        for (int i = 0; i < 3; i++)
        {
            var leftSlot = GetSlot(i * 2);
            var rightSlot = GetSlot(i * 2 + 1);
            var isWide = _viewModel.GetRowMode(i) == RowMode.Wide;
            rowHasContent[i] = leftSlot != WidgetType.None || (!isWide && rightSlot != WidgetType.None) || _viewModel.IsEditMode;
        }

        for (int i = 0; i < _widgetRows.Length; i++)
            _widgetRows[i].Visibility = rowHasContent[i] ? Visibility.Visible : Visibility.Collapsed;

        // Separators: visible only between two visible rows
        _separators[0].Visibility = rowHasContent[0] && rowHasContent[1] ? Visibility.Visible : Visibility.Collapsed;
        _separators[1].Visibility = (rowHasContent[1] || rowHasContent[0]) && rowHasContent[2] ? Visibility.Visible : Visibility.Collapsed;

        // Dynamic row heights: each row sizes to fit its largest widget
        // Separator ~17px (1px + 8px*2 margin)
        int[] rowHeights = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!rowHasContent[i]) { rowHeights[i] = 0; continue; }
            var leftSlot = GetSlot(i * 2);
            var rightSlot = _viewModel.GetRowMode(i) == RowMode.Wide ? WidgetType.None : GetSlot(i * 2 + 1);
            rowHeights[i] = Math.Max(GetWidgetHeight(leftSlot), GetWidgetHeight(rightSlot));
            // Edit mode needs room for ComboBoxes
            if (_viewModel.IsEditMode && rowHeights[i] < 55) rowHeights[i] = 55;
            _widgetRows[i].Height = rowHeights[i];
        }

        int visibleSeps = (rowHasContent[0] && rowHasContent[1] ? 1 : 0)
                        + ((rowHasContent[0] || rowHasContent[1]) && rowHasContent[2] ? 1 : 0);
        var contentH = rowHeights.Sum() + visibleSeps * 17;
        if (_viewModel.IsEditMode) contentH += 35;
        // Add space for notification banner if visible
        if (_viewModel.HasNotification) contentH += 60;
        if (contentH < 40) contentH = 40;
        ExpandedSize = (460, contentH);
    }

    private static int GetWidgetHeight(WidgetType type) => type switch
    {
        WidgetType.None => 0,
        // Compact: just text/small icons
        WidgetType.Clock => 60,
        WidgetType.Weather => 60,
        WidgetType.SystemMonitor => 70,
        WidgetType.Network => 70,
        WidgetType.Timer => 60,
        WidgetType.Pomodoro => 60,
        // Content-heavy: need more vertical space
        WidgetType.Media => 95,
        WidgetType.Calendar => 100,
        WidgetType.QuickAccess => 100,
        WidgetType.Clipboard => 100,
        WidgetType.QuickNotes => 100,
        WidgetType.VolumeMixer => 120,
        _ => 85
    };

    // Make ExpandedSize mutable for dynamic sizing
    private static (double W, double H) ExpandedSize = (460, 280);

    private void OnSlotSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.Tag is string tagStr && int.TryParse(tagStr, out var index))
        {
            if (combo.SelectedItem is WidgetType type)
            {
                _viewModel.SetSlot(index, type);
                RenderSlot(index, type);
            }
        }
    }

    private void OnEditSlotClick(object sender, MouseButtonEventArgs e)
    {
        // Handled by ComboBox inside the overlay
    }

    // ─── Widget Drag & Drop (Edit Mode) ─────────────────────────

    private const string SlotDragFormat = "PeakSlotIndex";
    private Border? _dragHighlightOverlay;
    private bool _isWidgetDragging;

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsEditMode) return;
        if (sender is not TextBlock tb || tb.Tag is not string tagStr || !int.TryParse(tagStr, out var slotIndex)) return;

        // Find the parent edit overlay
        var overlay = _editOverlays[slotIndex];
        overlay.Opacity = 0.5;
        _isWidgetDragging = true;

        var data = new DataObject(SlotDragFormat, slotIndex);
        DragDrop.DoDragDrop(overlay, data, DragDropEffects.Move);

        // Reset after drag ends
        _isWidgetDragging = false;
        overlay.Opacity = 1.0;
        ClearDragHighlight();
    }

    private void OnEditOverlayDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SlotDragFormat)) return;
        if (sender is Border border)
        {
            _dragHighlightOverlay = border;
            border.BorderBrush = (Brush)FindResource("AccentBrush");
            border.BorderThickness = new Thickness(2);
        }
        e.Handled = true;
    }

    private void OnEditOverlayDragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SlotDragFormat)) return;
        if (sender is Border border)
        {
            border.BorderBrush = null;
            border.BorderThickness = new Thickness(0);
            if (_dragHighlightOverlay == border) _dragHighlightOverlay = null;
        }
        e.Handled = true;
    }

    private void OnEditOverlayDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SlotDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnEditOverlayDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SlotDragFormat)) return;
        if (sender is not Border border || border.Tag is not string tagStr || !int.TryParse(tagStr, out var toIndex)) return;

        var fromIndex = (int)e.Data.GetData(SlotDragFormat)!;
        if (fromIndex != toIndex)
        {
            _viewModel.SwapSlots(fromIndex, toIndex);
            RenderSlot(fromIndex, GetSlot(fromIndex));
            RenderSlot(toIndex, GetSlot(toIndex));
        }

        ClearDragHighlight();
        e.Handled = true;
    }

    private void ClearDragHighlight()
    {
        if (_dragHighlightOverlay != null)
        {
            _dragHighlightOverlay.BorderBrush = null;
            _dragHighlightOverlay.BorderThickness = new Thickness(0);
            _dragHighlightOverlay = null;
        }
    }

    // ─── Notification Banner ─────────────────────────────────────

    private void UpdateNotificationBanner()
    {
        NotificationBanner.Visibility = _viewModel.HasNotification ? Visibility.Visible : Visibility.Collapsed;

        // Recalculate expanded height if currently expanded
        if (_viewModel.CurrentState == IslandState.Expanded)
        {
            UpdateRowVisibility();
            // Re-animate to new height
            var (targetW, targetH) = ExpandedSize;
            if (Math.Abs(IslandBorder.Height - targetH) > 1)
            {
                _currentH = targetH;
                IslandBorder.BeginAnimation(HeightProperty, null);
                var heightAnim = new DoubleAnimation(targetH, new Duration(TimeSpan.FromMilliseconds(200)))
                {
                    EasingFunction = AnimEase,
                    FillBehavior = FillBehavior.Stop
                };
                heightAnim.Completed += (_, _) => IslandBorder.Height = targetH;
                IslandBorder.BeginAnimation(HeightProperty, heightAnim);
            }
        }
    }

    private void OnPeekLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.HasNotification)
        {
            _viewModel.OpenCurrentNotificationApp();
            _viewModel.DismissCurrentNotification();
            e.Handled = true;
        }
    }

    private void OnPeekRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.HasNotification)
        {
            _viewModel.DismissCurrentNotification();
            e.Handled = true;
        }
    }

    // ─── Edit Mode Toggle ────────────────────────────────────────

    private void SetEditModeVisibility(bool isEdit)
    {
        var vis = isEdit ? Visibility.Visible : Visibility.Collapsed;

        // Show/hide edit overlays (respecting wide mode)
        for (int i = 0; i < _editOverlays.Length; i++)
        {
            var row = i / 2;
            var isRightSlot = i % 2 == 1;
            var isWide = _viewModel.GetRowMode(row) == RowMode.Wide;

            if (isRightSlot && isWide)
                _editOverlays[i].Visibility = Visibility.Collapsed;
            else
                _editOverlays[i].Visibility = vis;
        }

        // Show/hide row toggle buttons
        foreach (var toggle in _rowToggles)
            toggle.Visibility = vis;

        EditDoneButton.Visibility = vis;
        UpdateRowVisibility();
    }

    // ─── Fullscreen Detection ────────────────────────────────────

    private void OnFullscreenCheck(object? sender, EventArgs e)
    {
        bool isFullscreen = User32.IsFullscreenAppRunning(_hwnd);
        if (isFullscreen && !_hiddenByFullscreen)
        {
            _hiddenByFullscreen = true;
            Hide();
        }
        else if (!isFullscreen && _hiddenByFullscreen)
        {
            _hiddenByFullscreen = false;
            if (_viewModel.IsVisible) Show();
        }
    }

    // ─── Global Hotkey (Ctrl+Shift+N to toggle Hidden) ──────────

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HOTKEY_ID_TOGGLE = 9001;
    private const int WM_HOTKEY = 0x0312;

    private void RegisterGlobalHotkey()
    {
        var s = _viewModel.Settings;
        RegisterHotKey(_hwnd, HOTKEY_ID_TOGGLE, s.HotkeyModifiers, s.HotkeyVirtualKey);
    }

    private void UnregisterGlobalHotkey()
    {
        UnregisterHotKey(_hwnd, HOTKEY_ID_TOGGLE);
    }

    public void ReRegisterGlobalHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterGlobalHotkey();
        RegisterGlobalHotkey();
    }

    private void HandleHotkeyToggle()
    {
        if (_viewModel.CurrentState == IslandState.Hidden)
            _viewModel.Collapse();
        else
            _viewModel.HideIsland();
    }

    private void ShowUnhideGreeting(IslandState targetState)
    {
        // Pick a random greeting (never repeat the last one)
        int idx;
        do { idx = _greetingRng.Next(_greetings.Length); } while (idx == _lastGreetingIndex);
        _lastGreetingIndex = idx;
        GreetingText.Text = _greetings[idx];
        GreetingText.Visibility = Visibility.Visible;
        CollapsedContent.Visibility = Visibility.Collapsed;

        // Slide-in from top + fade in
        GreetingText.RenderTransform = new TranslateTransform(0, -10);
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(250)))
        {
            EasingFunction = AnimEase
        };
        var slideIn = new DoubleAnimation(-10, 0, new Duration(TimeSpan.FromMilliseconds(250)))
        {
            EasingFunction = AnimEase,
            FillBehavior = FillBehavior.Stop
        };
        slideIn.Completed += (_, _) => ((TranslateTransform)GreetingText.RenderTransform).Y = 0;

        fadeIn.Completed += (_, _) =>
        {
            // After 5 seconds, fade out greeting and show normal content
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(200)));
                var slideOut = new DoubleAnimation(0, -8, new Duration(TimeSpan.FromMilliseconds(200)))
                {
                    EasingFunction = AnimEase,
                    FillBehavior = FillBehavior.Stop
                };
                fadeOut.Completed += (_, _) =>
                {
                    GreetingText.Visibility = Visibility.Collapsed;
                    GreetingText.BeginAnimation(OpacityProperty, null);
                    GreetingText.Opacity = 0;

                    // Show normal collapsed content with fade in
                    CollapsedContent.Visibility = Visibility.Visible;
                    var contentFadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(250)))
                    {
                        FillBehavior = FillBehavior.Stop
                    };
                    contentFadeIn.Completed += (_, _) => CollapsedContent.Opacity = 1;
                    CollapsedContent.BeginAnimation(OpacityProperty, contentFadeIn);
                };
                GreetingText.BeginAnimation(OpacityProperty, fadeOut);
                ((TranslateTransform)GreetingText.RenderTransform).BeginAnimation(TranslateTransform.YProperty, slideOut);
            };
            timer.Start();
        };
        GreetingText.BeginAnimation(OpacityProperty, fadeIn);
        ((TranslateTransform)GreetingText.RenderTransform).BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    // ─── Mouse Interaction ───────────────────────────────────────

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private bool IsMouseOverIsland()
    {
        if (!GetCursorPos(out var cursor)) return false;
        var topLeft = IslandBorder.PointToScreen(new Point(0, 0));
        var bottomRight = IslandBorder.PointToScreen(
            new Point(IslandBorder.ActualWidth, IslandBorder.ActualHeight));
        return cursor.X >= topLeft.X && cursor.X <= bottomRight.X
            && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
    }

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        StopMousePolling();
        if (_viewModel.CurrentState == IslandState.Hidden)
            return; // Hidden state is only toggled via hotkey

        // While a notification is being peeked, let the user interact with the Peek
        // (left-click open, right-click dismiss) instead of auto-expanding. Pause
        // the auto-collapse timer while the mouse is over the island.
        if (_viewModel.CurrentState == IslandState.Peek && _viewModel.HasNotification)
        {
            _viewModel.PauseAutoCollapse();
            return;
        }

        _viewModel.Expand();
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsEditMode) return;
        if (_viewModel.CurrentState == IslandState.Hidden) return;
        if (_isDragging || _isWidgetDragging) return;

        // Resume auto-collapse if we paused it for an interactive peek
        if (_viewModel.CurrentState == IslandState.Peek && _viewModel.HasNotification)
            _viewModel.ResumeAutoCollapse();

        StartMousePolling();
    }

    // ─── Drag & Drop: auto-expand + highlight ───────────────────
    //
    // WPF fires spurious DragLeave/DragEnter when moving between child elements
    // on transparent windows. We use DragOver as heartbeat: as long as DragOver
    // fires, the cursor is still over us. A debounce timer detects true leave.

    private bool _isDragging;
    private DispatcherTimer? _dragLeaveTimer;

    private void OnIslandDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (_viewModel.CurrentState == IslandState.Hidden) return;

        CancelDragLeaveTimer();

        if (!_isDragging)
        {
            _isDragging = true;
            StopMousePolling();

            if (_viewModel.CurrentState != IslandState.Expanded)
                _viewModel.Expand();

            SetQuickAccessHighlight(true);
        }

        e.Handled = true;
    }

    private void OnIslandDragOver(object sender, DragEventArgs e)
    {
        // Every DragOver means cursor is still here — cancel any pending leave
        CancelDragLeaveTimer();

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnIslandDragLeave(object sender, DragEventArgs e)
    {
        if (_isWidgetDragging || _viewModel.IsEditMode) return;
        // Don't collapse immediately — wait to see if DragOver fires again
        StartDragLeaveTimer();
    }

    private void OnIslandDrop(object sender, DragEventArgs e)
    {
        CancelDragLeaveTimer();
        SetQuickAccessHighlight(false);
        _isDragging = false;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var path in files)
        {
            if (_viewModel.QuickAccessItems.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var isFolder = System.IO.Directory.Exists(path);
            var name = System.IO.Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;

            _viewModel.QuickAccessItems.Add(new Peak.Core.Models.QuickAccessItem
            {
                Path = path,
                Name = name,
                IsFolder = isFolder
            });
        }
        _viewModel.SaveQuickAccess();
        e.Handled = true;
    }

    private void StartDragLeaveTimer()
    {
        if (_dragLeaveTimer != null) return;
        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            CancelDragLeaveTimer();
            SetQuickAccessHighlight(false);
            _isDragging = false;
            _viewModel.Collapse();
        };
        _dragLeaveTimer.Start();
    }

    private void CancelDragLeaveTimer()
    {
        _dragLeaveTimer?.Stop();
        _dragLeaveTimer = null;
    }

    private void SetQuickAccessHighlight(bool highlight)
    {
        foreach (var host in _slotHosts)
        {
            if (host.Content is Widgets.QuickAccessWidget widget)
                widget.SetDropHighlight(highlight);
        }
    }

    private void StartMousePolling()
    {
        if (_mousePollingTimer != null) return; // Already polling
        _mousePollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _mousePollingTimer.Tick += OnMousePollingTick;
        _mousePollingTimer.Start();
    }

    private void StopMousePolling()
    {
        _mousePollingTimer?.Stop();
        _mousePollingTimer = null;
    }

    private void OnMousePollingTick(object? sender, EventArgs e)
    {
        if (_viewModel.IsEditMode) { StopMousePolling(); return; }
        if (_viewModel.CurrentState == IslandState.Hidden) { StopMousePolling(); return; }
        if (_isDragging || _isWidgetDragging) { StopMousePolling(); return; }

        if (IsMouseOverIsland())
        {
            // Mouse came back — stop polling, stay expanded
            StopMousePolling();
            if (_viewModel.CurrentState != IslandState.Expanded)
                _viewModel.Expand();
        }
        else
        {
            // Mouse truly gone — collapse
            StopMousePolling();
            if (_viewModel.CurrentState != IslandState.Collapsed)
                _viewModel.Collapse();
        }
    }

    private void PositionVisualizerCircle()
    {
        if (VisualizerCircle.Visibility != Visibility.Visible) return;

        // Pill is HorizontalAlignment=Center in the Grid
        // Circle goes 6px to the right of the pill's right edge
        var pillW = IslandBorder.ActualWidth;
        var gridW = ((Grid)IslandBorder.Parent).ActualWidth;
        if (pillW <= 0 || gridW <= 0) return;

        double pillRight = (gridW + pillW) / 2; // right edge of centered pill
        VisualizerCircle.Margin = new Thickness(pillRight + 6, 0, 0, 0);
    }

    // ─── Collapsed Slots ──────────────────────────────────────────

    public void RenderCollapsedSlots()
    {
        var leftContent = CreateCollapsedWidget(_viewModel.CollapsedLeft);
        var centerContent = CreateCollapsedWidget(_viewModel.CollapsedCenter);
        var rightContent = CreateCollapsedWidget(_viewModel.CollapsedRight);

        CollapsedSlotLeft.Content = leftContent;
        CollapsedSlotCenter.Content = centerContent;
        CollapsedSlotRight.Content = rightContent;

        // Use actual content (not just enum != None) so plugin-driven slots
        // that return null (e.g. Discord not in call) collapse properly.
        bool hasLeft = leftContent != null;
        bool hasCenter = centerContent != null;
        bool hasRight = rightContent != null;

        CollapsedSep1.Visibility = hasLeft && hasCenter ? Visibility.Visible : Visibility.Collapsed;
        CollapsedSep2.Visibility = (hasCenter && hasRight)
            || (hasLeft && hasRight && !hasCenter) ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement? CreateCollapsedWidget(CollapsedWidget type)
    {
        // Give plugins first chance to render this collapsed widget
        var pluginContent = ExternalCollapsedRenderer?.Invoke(type);
        if (pluginContent != null) return pluginContent;

        switch (type)
        {
            case CollapsedWidget.Clock:
                var clock = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
                clock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("CurrentTime"));
                return clock;

            case CollapsedWidget.Weather:
                var weatherPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var wIcon = new Path { Stretch = Stretch.Uniform, Width = 13, Height = 13, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
                wIcon.SetBinding(Path.DataProperty, new System.Windows.Data.Binding("WeatherIconGeometry"));
                var wTemp = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
                wTemp.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("WeatherTemp"));
                weatherPanel.Children.Add(wIcon);
                weatherPanel.Children.Add(wTemp);
                return weatherPanel;

            case CollapsedWidget.Temperature:
                var temp = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
                temp.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("WeatherTemp"));
                return temp;

            case CollapsedWidget.WeatherIcon:
                var icon = new Path { Stretch = Stretch.Uniform, Width = 13, Height = 13, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
                icon.SetBinding(Path.DataProperty, new System.Windows.Data.Binding("WeatherIconGeometry"));
                return icon;

            case CollapsedWidget.Date:
                var date = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
                date.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("CurrentDate"));
                return date;

            case CollapsedWidget.MediaTitle:
                var media = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
                media.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("MediaTitle"));
                return media;

            // DiscordCallCount is handled entirely by the plugin renderer.
            // If the plugin returns null (not in a call), the slot stays empty.

            default:
                return null;
        }
    }

    // ─── Visualizer Override (Plugin Hook) ───────────────────────

    public void SetVisualizerOverride(UIElement? content)
    {
        _visualizerOverride = content;

        // Remove any previous size-sync handler
        if (_overrideSizeHandler != null)
        {
            IslandBorder.SizeChanged -= _overrideSizeHandler;
            _overrideSizeHandler = null;
        }

        if (content != null)
        {
            // Stop standard visualizer, clear bars, show custom content
            if (_visualizerRunning && _audioVisualizer != null)
            {
                _audioVisualizer.LevelsUpdated -= OnVisualizerLevels;
                _audioVisualizer.Stop();
                _visualizerRunning = false;
            }
            VisualizerRight.Children.Clear();

            const double inset = 3.0; // gap between image and circle border on each side
            void ApplySize()
            {
                var d = IslandBorder.ActualHeight;
                if (d <= 0) return;
                VisualizerRight.Width = d;
                VisualizerRight.Height = d;
                var inner = Math.Max(0, d - 2 * inset);
                if (content is FrameworkElement el)
                {
                    el.Width = inner;
                    el.Height = inner;
                    Canvas.SetLeft(el, inset);
                    Canvas.SetTop(el, inset);
                }
                VisualizerCircle.CornerRadius = new CornerRadius(d / 2);
                PositionVisualizerCircle();
            }

            if (content is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Center;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            VisualizerRight.Children.Add(content);

            // Respect current island state — bubble is only shown in the collapsed notch.
            UpdateVisualizerState();

            // Initial sizing — may be 0 if window hasn't laid out yet.
            ApplySize();

            // Keep override in sync with the island border size (handles collapse/expand).
            _overrideSizeHandler = (_, _) => ApplySize();
            IslandBorder.SizeChanged += _overrideSizeHandler;

            // If initial size was 0, also schedule a deferred apply after layout pass.
            if (IslandBorder.ActualHeight <= 0)
                Dispatcher.BeginInvoke(new Action(ApplySize), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            // Restore standard bars
            VisualizerRight.Children.Clear();
            VisualizerRight.Width = 22;
            VisualizerRight.Height = 16;
            VisualizerCircle.CornerRadius = new CornerRadius(6);
            // Reset visibility — UpdateVisualizerState will set Visible if music is playing.
            VisualizerCircle.Visibility = Visibility.Collapsed;
            for (int i = 0; i < _vizBarsRight.Length; i++) _vizBarsRight[i] = null!;
            InitVisualizerBars();
            UpdateVisualizerState();
        }
    }

    private SizeChangedEventHandler? _overrideSizeHandler;

    // ─── ViewModel Property Changes ──────────────────────────────

    // ─── Audio Visualizer ───────────────────────────────────────

    private void InitVisualizerBars()
    {
        const double barWidth = 3;
        const double gap = 1.5;
        double canvasW = VisualizerRight.Width;
        double canvasH = VisualizerRight.Height;
        double totalW = AudioVisualizerService.BarCount * barWidth + (AudioVisualizerService.BarCount - 1) * gap;
        double offsetX = (canvasW - totalW) / 2;

        for (int i = 0; i < AudioVisualizerService.BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                Fill = Brushes.Transparent
            };

            Canvas.SetLeft(bar, offsetX + i * (barWidth + gap));
            Canvas.SetTop(bar, (canvasH - 2) / 2);

            VisualizerRight.Children.Add(bar);
            _vizBarsRight[i] = bar;
        }
    }

    private void StartVisualizer()
    {
        if (_visualizerRunning || _audioVisualizer == null) return;
        _visualizerRunning = true;

        VisualizerCircle.Visibility = Visibility.Visible;
        var h = IslandBorder.ActualHeight;
        if (h > 0) VisualizerCircle.CornerRadius = new CornerRadius(h / 2);
        PositionVisualizerCircle();

        var accentBrush = (Brush)FindResource("AccentBrush");
        foreach (var bar in _vizBarsRight) bar.Fill = accentBrush;

        _audioVisualizer.LevelsUpdated += OnVisualizerLevels;

        var sens = _viewModel.Settings.VisualizerSensitivity;
        _audioVisualizer.Amplification = (float)(5 + sens * 0.75); // 10%→12.5, 50%→42.5, 100%→80

        var deviceId = _viewModel.Settings.AudioDeviceId;
        _audioVisualizer.Start(string.IsNullOrEmpty(deviceId) ? null : deviceId);
    }

    private void StopVisualizer()
    {
        if (!_visualizerRunning || _audioVisualizer == null) return;
        _visualizerRunning = false;

        _audioVisualizer.LevelsUpdated -= OnVisualizerLevels;
        _audioVisualizer.Stop();

        VisualizerCircle.Visibility = Visibility.Collapsed;
    }

    private void OnVisualizerLevels(float[] levels)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_visualizerRunning) return;
            double canvasH = VisualizerRight.Height;

            for (int i = 0; i < levels.Length && i < _vizBarsRight.Length; i++)
            {
                double h = Math.Max(2, levels[i] * canvasH);
                _vizBarsRight[i].Height = h;
                // Re-center vertically as height changes
                Canvas.SetTop(_vizBarsRight[i], (canvasH - h) / 2);
            }
        }, DispatcherPriority.Render);
    }

    private void UpdateVisualizerState()
    {
        // If a plugin has taken over the bubble, keep it visible and skip the audio visualizer.
        if (_visualizerOverride != null)
        {
            VisualizerCircle.Visibility = _viewModel.CurrentState is IslandState.Collapsed
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        if (_viewModel.HasMedia && _viewModel.IsPlaying &&
            _viewModel.CurrentState is IslandState.Collapsed)
        {
            StartVisualizer();
        }
        else
        {
            StopVisualizer();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Update visualizer on relevant changes
        if (e.PropertyName is nameof(IslandViewModel.CurrentState)
            or nameof(IslandViewModel.IsPlaying)
            or nameof(IslandViewModel.HasMedia))
        {
            Dispatcher.Invoke(UpdateVisualizerState);
        }

        if (e.PropertyName == nameof(IslandViewModel.CurrentState))
            Dispatcher.Invoke(() => TransitionToState(_viewModel.CurrentState));
        else if (e.PropertyName == nameof(IslandViewModel.IsVisible))
            Dispatcher.Invoke(() =>
            {
                if (_hiddenByFullscreen) return;
                if (_viewModel.IsVisible) Show();
                else Hide();
            });
        else if (e.PropertyName == nameof(IslandViewModel.IsEditMode))
            Dispatcher.Invoke(() => SetEditModeVisibility(_viewModel.IsEditMode));
        else if (e.PropertyName == nameof(IslandViewModel.HasNotification))
            Dispatcher.Invoke(() => UpdateNotificationBanner());
        else if (e.PropertyName is "Row0Mode" or "Row1Mode" or "Row2Mode")
        {
            var row = int.Parse(e.PropertyName![3..4]);
            Dispatcher.Invoke(() => { ApplyRowMode(row); UpdateRowVisibility(); });
        }
        else if (e.PropertyName is "Slot0" or "Slot1" or "Slot2" or "Slot3" or "Slot4" or "Slot5")
        {
            var index = int.Parse(e.PropertyName![4..]);
            var type = index switch
            {
                0 => _viewModel.Slot0, 1 => _viewModel.Slot1,
                2 => _viewModel.Slot2, 3 => _viewModel.Slot3,
                4 => _viewModel.Slot4, 5 => _viewModel.Slot5,
                _ => WidgetType.None
            };
            Dispatcher.Invoke(() => RenderSlot(index, type));
        }
    }

    // ─── GPU-Accelerated Animation ───────────────────────────────

    private void TransitionToState(IslandState state)
    {
        if (state == _currentAnimatedState) return;

        // Re-apply settings on every transition (picks up changes from Settings window)
        ApplyBorderSetting();
        if (state == IslandState.Collapsed)
        {
            RenderCollapsedSlots();
            _viewModel.HasNotification = false;
        }

        var previousState = _currentAnimatedState;
        _currentAnimatedState = state;

        // ── Hidden → Something: slide down first, then transition normally
        if (previousState == IslandState.Hidden && state != IslandState.Hidden)
        {
            // Restore window position with TopOffset
            Top = SystemParameters.WorkArea.Top + TopOffset;

            // Cancel translate animation
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);

            var slideDown = new DoubleAnimation(0, AnimDuration)
            {
                EasingFunction = AnimEase,
                FillBehavior = FillBehavior.Stop
            };
            slideDown.Completed += (_, _) =>
            {
                _translateTransform.Y = 0;
                ShowUnhideGreeting(state);
            };
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, slideDown);
            return;
        }

        // ── Something → Hidden: first collapse, then slide up
        if (state == IslandState.Hidden)
        {
            // First ensure we're at collapsed size
            var (targetW, targetH) = CollapsedSize;
            _currentW = targetW;
            _currentH = targetH;

            // Cancel running animations
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            IslandBorder.BeginAnimation(WidthProperty, null);
            IslandBorder.BeginAnimation(HeightProperty, null);
            IslandBorder.Width = targetW;
            IslandBorder.Height = targetH;
            _scaleTransform.ScaleX = 1;
            _scaleTransform.ScaleY = 1;

            // Hide all content — only the empty pill shape is visible
            CollapsedContent.Opacity = 0;
            PeekContent.Opacity = 0;
            ExpandedContent.Opacity = 0;

            // Move window to screen edge (no gap)
            Top = SystemParameters.WorkArea.Top;

            // Now slide up — leave 10px visible (just the rounded bottom edge)
            double hideY = -(targetH - 10);
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
            var slideUp = new DoubleAnimation(hideY, AnimDuration)
            {
                EasingFunction = AnimEase,
                FillBehavior = FillBehavior.Stop
            };
            slideUp.Completed += (_, _) => _translateTransform.Y = hideY;
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
            return;
        }

        // ── Normal transitions (Collapsed ↔ Peek ↔ Expanded)
        DoScaleTransition(state);
    }

    private void DoScaleTransition(IslandState state)
    {
        var (targetW, targetH) = state switch
        {
            IslandState.Collapsed => CollapsedSize,
            IslandState.Peek => PeekSize,
            IslandState.Expanded => ExpandedSize,
            _ => CollapsedSize
        };

        double oldW = _currentW;
        double oldH = _currentH;
        _currentW = targetW;
        _currentH = targetH;

        // Hide ALL content during scale to avoid text distortion
        CollapsedContent.Opacity = 0;
        PeekContent.Opacity = 0;
        ExpandedContent.Opacity = 0;

        SetContentVisibility(state);

        // Cancel any running animations
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        IslandBorder.BeginAnimation(WidthProperty, null);
        IslandBorder.BeginAnimation(HeightProperty, null);
        IslandBorder.Width = targetW;
        IslandBorder.Height = targetH;

        double startScaleX = oldW / targetW;
        double startScaleY = oldH / targetH;
        _scaleTransform.ScaleX = startScaleX;
        _scaleTransform.ScaleY = startScaleY;

        // State-specific timing and easing
        bool isExpanding = state == IslandState.Expanded;
        bool isCollapsing = state == IslandState.Collapsed;
        bool isPeek = state == IslandState.Peek;

        var duration = isExpanding ? ExpandDuration
            : isCollapsing ? CollapseDuration
            : PeekDuration;
        var ease = isExpanding ? ExpandEase : AnimEase;
        var durationMs = isExpanding ? 300.0 : isCollapsing ? 220.0 : 250.0;

        var scaleXAnim = new DoubleAnimation(1.0, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        var scaleYAnim = new DoubleAnimation(1.0, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };

        var activeContent = state switch
        {
            IslandState.Collapsed => (UIElement)CollapsedContent,
            IslandState.Peek => PeekContent,
            IslandState.Expanded => ExpandedContent,
            _ => CollapsedContent
        };

        // Start content fade-in at 60% of scale duration (parallel, not sequential)
        var fadeDelay = TimeSpan.FromMilliseconds(durationMs * 0.6);
        var fadeTimer = new DispatcherTimer { Interval = fadeDelay };
        fadeTimer.Tick += (_, _) =>
        {
            fadeTimer.Stop();
            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(120)))
            {
                FillBehavior = FillBehavior.Stop
            };
            fadeIn.Completed += (_, _) => activeContent.Opacity = 1;
            activeContent.BeginAnimation(OpacityProperty, fadeIn);
        };
        fadeTimer.Start();

        scaleYAnim.Completed += (_, _) =>
        {
            _scaleTransform.ScaleX = 1.0;
            _scaleTransform.ScaleY = 1.0;
        };

        _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
    }

    private void SetContentVisibility(IslandState state)
    {
        // Hidden uses CollapsedContent (same visual, just translated up)
        CollapsedContent.Visibility = state is IslandState.Collapsed or IslandState.Hidden ? Visibility.Visible : Visibility.Collapsed;
        PeekContent.Visibility = state == IslandState.Peek ? Visibility.Visible : Visibility.Collapsed;
        ExpandedContent.Visibility = state == IslandState.Expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Win32 WndProc Hook — Block Minimize ──────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy, flags;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                if (wParam.ToInt32() == HOTKEY_ID_TOGGLE)
                {
                    HandleHotkeyToggle();
                    handled = true;
                    return IntPtr.Zero;
                }
                break;

            // Block SC_MINIMIZE system command entirely
            case WM_SYSCOMMAND:
                if ((wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;

            // Prevent any window-position change that would hide/minimize us
            case WM_WINDOWPOSCHANGING:
                // If the window is being made non-visible (ShowWindow minimize),
                // keep it visible by stripping the hide flag
                const int SWP_HIDEWINDOW = 0x0080;
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((pos.flags & SWP_HIDEWINDOW) != 0 && !_hiddenByFullscreen)
                {
                    pos.flags &= ~SWP_HIDEWINDOW;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                break;
        }

        return IntPtr.Zero;
    }

    // ─── Prevent Focus Loss ─────────────────────────────────────

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Safety net: if minimize somehow gets through, undo immediately
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Topmost = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Re-assert topmost when another app steals focus (e.g. media player activation)
        if (!_hiddenByFullscreen && _viewModel.IsVisible)
        {
            Topmost = false;
            Topmost = true;
        }
    }
}
