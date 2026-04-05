using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using Peak.App.ViewModels;
using Peak.Core.Configuration;
using Peak.Core.Services;

namespace Peak.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private string _selectedBgColor = "#FF000000";
    private string _selectedAccentColor = "#FF60CDFF";
    private bool _suppressHexEvent;
    private readonly ObservableCollection<NotificationAppToggle> _notificationApps = new();
    private uint _recordedHotkeyModifiers;
    private uint _recordedHotkeyVk;
    private string _recordedHotkeyDisplay = "";

    public SettingsWindow(SettingsManager settingsManager)
    {
        InitializeComponent();
        _settingsManager = settingsManager;
        LoadSettings();
    }

    private void LoadSettings()
    {
        // About
        VersionText.Text = $"v{UpdateService.CurrentVersion}";

        var s = _settingsManager.Settings;
        AlwaysVisibleCheck.IsChecked = s.Behavior == IslandBehavior.AlwaysVisible;
        CollapseSlider.Value = s.AutoCollapseSeconds;
        AutoStartCheck.IsChecked = s.LaunchAtStartup;

        _recordedHotkeyModifiers = s.HotkeyModifiers;
        _recordedHotkeyVk = s.HotkeyVirtualKey;
        _recordedHotkeyDisplay = s.HotkeyDisplay;
        HotkeyBox.Text = s.HotkeyDisplay;
        ShowClockCheck.IsChecked = s.ShowClock;
        ShowMediaCheck.IsChecked = s.ShowMedia;
        ShowSystemCheck.IsChecked = s.ShowSystemMonitor;
        ShowWeatherCheck.IsChecked = s.ShowWeather;
        ShowCalendarCheck.IsChecked = s.ShowCalendar;
        ShowNotificationsCheck.IsChecked = s.ShowNotifications;
        ShowTimerCheck.IsChecked = s.ShowTimer;
        ShowBorderCheck.IsChecked = s.ShowBorder;
        PostalCodeBox.Text = s.WeatherPostalCode;
        CountryCodeBox.Text = s.WeatherCountryCode;

        // Colors
        _selectedBgColor = s.IslandBackground;
        _selectedAccentColor = s.AccentColor;
        HighlightColorCircle(BgColorPanel, _selectedBgColor);
        HighlightColorCircle(AccentColorPanel, _selectedAccentColor);
        UpdateHexBoxes();

        // Theme presets
        BuildThemePresets(s.ThemePreset);

        // Collapsed layout
        LoadCollapsedCombos(s.CollapsedSlots);

        // Audio
        LoadAudioDevices(s.AudioDeviceId);
        SensitivitySlider.Value = s.VisualizerSensitivity;

        // Notification apps
        LoadNotificationApps(s);
    }

    private void LoadNotificationApps(AppSettings s)
    {
        _notificationApps.Clear();
        foreach (var app in s.SeenNotificationApps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            _notificationApps.Add(new NotificationAppToggle
            {
                AppName = app,
                IsEnabled = !s.MutedNotificationApps.Contains(app)
            });
        }
        NotificationAppsList.ItemsSource = _notificationApps;
        NoNotificationAppsHint.Visibility = _notificationApps.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly (CollapsedWidget Value, string Label)[] _collapsedOptions =
    [
        (CollapsedWidget.None, "None"),
        (CollapsedWidget.Clock, "Clock"),
        (CollapsedWidget.Weather, "Weather"),
        (CollapsedWidget.Temperature, "Temperature"),
        (CollapsedWidget.WeatherIcon, "Weather Icon"),
        (CollapsedWidget.Date, "Date"),
        (CollapsedWidget.MediaTitle, "Media Title"),
    ];

    private void LoadCollapsedCombos(CollapsedWidget[] slots)
    {
        var combos = new[] { CollapsedLeftCombo, CollapsedCenterCombo, CollapsedRightCombo };
        for (int i = 0; i < 3; i++)
        {
            combos[i].Items.Clear();
            int selected = 0;
            for (int j = 0; j < _collapsedOptions.Length; j++)
            {
                combos[i].Items.Add(new ComboBoxItem
                {
                    Content = _collapsedOptions[j].Label,
                    Tag = _collapsedOptions[j].Value
                });
                if (slots is { Length: >= 3 } && slots[i] == _collapsedOptions[j].Value)
                    selected = j;
            }
            combos[i].SelectedIndex = selected;
        }
    }

    private void LoadAudioDevices(string savedDeviceId)
    {
        AudioDeviceCombo.Items.Clear();
        AudioDeviceCombo.Items.Add(new ComboBoxItem { Content = "Standard (Default)", Tag = "" });

        var devices = AudioVisualizerService.GetAudioDevices();
        int selectedIndex = 0;

        for (int i = 0; i < devices.Count; i++)
        {
            var item = new ComboBoxItem { Content = devices[i].Name, Tag = devices[i].Id };
            AudioDeviceCombo.Items.Add(item);
            if (devices[i].Id == savedDeviceId)
                selectedIndex = i + 1;
        }

        AudioDeviceCombo.SelectedIndex = selectedIndex;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var s = _settingsManager.Settings;
        s.Behavior = (AlwaysVisibleCheck.IsChecked ?? true) ? IslandBehavior.AlwaysVisible : IslandBehavior.EventOnly;
        s.AutoCollapseSeconds = (int)CollapseSlider.Value;
        s.ShowClock = ShowClockCheck.IsChecked ?? true;
        s.ShowMedia = ShowMediaCheck.IsChecked ?? true;
        s.ShowSystemMonitor = ShowSystemCheck.IsChecked ?? true;
        s.ShowWeather = ShowWeatherCheck.IsChecked ?? true;
        s.ShowCalendar = ShowCalendarCheck.IsChecked ?? true;
        s.ShowNotifications = ShowNotificationsCheck.IsChecked ?? true;
        s.ShowTimer = ShowTimerCheck.IsChecked ?? true;

        s.ShowBorder = ShowBorderCheck.IsChecked ?? true;

        s.IslandBackground = _selectedBgColor;
        s.AccentColor = _selectedAccentColor;
        s.ThemePreset = _selectedThemePreset;
        App.UpdateThemeColors(_selectedBgColor, _selectedAccentColor);

        s.WeatherPostalCode = PostalCodeBox.Text.Trim();
        s.WeatherCountryCode = string.IsNullOrWhiteSpace(CountryCodeBox.Text) ? "DE" : CountryCodeBox.Text.Trim().ToUpper();

        // Collapsed layout
        s.CollapsedSlots =
        [
            (CollapsedLeftCombo.SelectedItem as ComboBoxItem)?.Tag is CollapsedWidget l ? l : CollapsedWidget.None,
            (CollapsedCenterCombo.SelectedItem as ComboBoxItem)?.Tag is CollapsedWidget c ? c : CollapsedWidget.None,
            (CollapsedRightCombo.SelectedItem as ComboBoxItem)?.Tag is CollapsedWidget r ? r : CollapsedWidget.None,
        ];

        // Audio
        s.AudioDeviceId = (AudioDeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        s.VisualizerSensitivity = SensitivitySlider.Value;

        var wantsAutoStart = AutoStartCheck.IsChecked ?? false;
        s.LaunchAtStartup = wantsAutoStart;
        StartupService.SetAutoStart(wantsAutoStart);

        // Hotkey
        s.HotkeyModifiers = _recordedHotkeyModifiers;
        s.HotkeyVirtualKey = _recordedHotkeyVk;
        s.HotkeyDisplay = _recordedHotkeyDisplay;
        ((App)Application.Current).Services
            .GetRequiredService<IslandWindow>()
            .ReRegisterGlobalHotkey();

        // Notification apps: persist muted set from toggles
        s.MutedNotificationApps = new HashSet<string>(
            _notificationApps.Where(a => !a.IsEnabled).Select(a => a.AppName));

        _settingsManager.Save();
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // Restore original colors (undo live preview)
        OnCancelClick_RestoreColors();
        Close();
    }

    private void OnAlwaysVisibleChanged(object sender, RoutedEventArgs e)
    {
        if (BehaviorHint == null) return;
        BehaviorHint.Text = (AlwaysVisibleCheck.IsChecked ?? true)
            ? "Island is always shown on screen"
            : "Island only appears for events (media, notifications)";
    }

    private string _selectedThemePreset = "default";

    private void BuildThemePresets(string activePreset)
    {
        _selectedThemePreset = activePreset;
        ThemePresetsPanel.Children.Clear();

        foreach (var (name, (bg, accent)) in ThemePresets.GetAll())
        {
            var bgColor = (Color)ColorConverter.ConvertFromString(bg);
            var accentColor = (Color)ColorConverter.ConvertFromString(accent);

            var grid = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand };
            grid.Tag = name;
            grid.MouseDown += OnThemePresetClick;

            // Background circle
            var bgCircle = new Ellipse
            {
                Width = 36, Height = 36,
                Fill = new SolidColorBrush(bgColor),
                Stroke = name == activePreset ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = name == activePreset ? 2 : 1
            };
            grid.Children.Add(bgCircle);

            // Accent dot
            var accentDot = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = new SolidColorBrush(accentColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(accentDot);

            // Label
            var label = new TextBlock
            {
                Text = char.ToUpper(name[0]) + name[1..],
                Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -12)
            };
            grid.Children.Add(label);

            ThemePresetsPanel.Children.Add(grid);
        }
    }

    private void OnThemePresetClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || grid.Tag is not string name) return;

        var preset = ThemePresets.GetPreset(name);
        if (preset == null) return;

        _selectedThemePreset = name;
        _selectedBgColor = preset.Value.Background;
        _selectedAccentColor = preset.Value.Accent;

        HighlightColorCircle(BgColorPanel, _selectedBgColor);
        HighlightColorCircle(AccentColorPanel, _selectedAccentColor);
        UpdateHexBoxes();
        App.UpdateThemeColors(_selectedBgColor, _selectedAccentColor);
        BuildThemePresets(name); // refresh highlights
    }

    private void OnBgColorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is string hex)
        {
            _selectedBgColor = hex;
            _selectedThemePreset = "custom";
            HighlightColorCircle(BgColorPanel, hex);
            UpdateHexBoxes();
            App.UpdateThemeColors(_selectedBgColor, _selectedAccentColor);
            BuildThemePresets("custom");
        }
    }

    private void OnAccentColorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is string hex)
        {
            _selectedAccentColor = hex;
            _selectedThemePreset = "custom";
            HighlightColorCircle(AccentColorPanel, hex);
            UpdateHexBoxes();
            App.UpdateThemeColors(_selectedBgColor, _selectedAccentColor);
            BuildThemePresets("custom");
        }
    }

    private void OnBgHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressHexEvent) return;
        var hex = BgHexBox.Text.Trim();
        if (!TryParseColor(hex, out var color)) return;

        _selectedBgColor = hex;
        BgPreview.Fill = new SolidColorBrush(color);
        HighlightColorCircle(BgColorPanel, hex);
        App.UpdateThemeColors(_selectedBgColor, _selectedAccentColor);
    }

    private void OnAccentHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressHexEvent) return;
        var hex = AccentHexBox.Text.Trim();
        if (!TryParseColor(hex, out var color)) return;

        _selectedAccentColor = hex;
        AccentPreview.Fill = new SolidColorBrush(color);
        HighlightColorCircle(AccentColorPanel, hex);
        App.UpdateThemeColors(_selectedBgColor, _selectedAccentColor);
    }

    private void UpdateHexBoxes()
    {
        _suppressHexEvent = true;
        BgHexBox.Text = _selectedBgColor;
        AccentHexBox.Text = _selectedAccentColor;
        try
        {
            BgPreview.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedBgColor));
            AccentPreview.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedAccentColor));
        }
        catch { /* invalid */ }
        _suppressHexEvent = false;
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch { return false; }
    }

    private void HighlightColorCircle(WrapPanel panel, string selectedHex)
    {
        foreach (var child in panel.Children)
        {
            if (child is Ellipse circle && circle.Tag is string hex)
            {
                circle.Stroke = string.Equals(hex, selectedHex, StringComparison.OrdinalIgnoreCase)
                    ? Brushes.White
                    : Brushes.Transparent;
            }
        }
    }

    private void OnCancelClick_RestoreColors()
    {
        var s = _settingsManager.Settings;
        App.UpdateThemeColors(s.IslandBackground, s.AccentColor);
    }

    private void OnSensitivityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SensitivityLabel != null)
            SensitivityLabel.Text = $"{(int)e.NewValue}%";
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";

        try
        {
            var updateService = ((App)Application.Current).Services.GetRequiredService<UpdateService>();
            await updateService.CheckForUpdateAsync();

            if (updateService.UpdateAvailable)
            {
                UpdateStatusText.Text = updateService.IsDownloaded
                    ? $"v{updateService.NewVersion} ready to install!"
                    : $"v{updateService.NewVersion} available, downloading...";
            }
            else
            {
                UpdateStatusText.Text = "You're up to date!";
            }
        }
        catch
        {
            UpdateStatusText.Text = "Check failed.";
        }

        CheckUpdateButton.IsEnabled = true;
    }

    private void OnTestPeekClick(object sender, RoutedEventArgs e)
    {
        var vm = ((App)Application.Current).Services.GetRequiredService<IslandViewModel>();
        vm.NotificationTitle = "Test Notification";
        vm.NotificationBody = "This is a test peek notification from Peak.";
        vm.NotificationApp = "Peak Settings";
        vm.NotificationIcon = null;
        vm.HasNotification = true;
        vm.ShowPeek();
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    // ─── Hotkey capture ──────────────────────────────────────────

    private void OnHotkeyBoxFocused(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.Text = "Press a key combination…";
    }

    private void OnHotkeyBoxBlurred(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.Text = _recordedHotkeyDisplay;
    }

    private void OnHotkeyBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        // Ignore pure modifier presses — wait for a real key
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
                 or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                 or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                 or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
                 or System.Windows.Input.Key.Escape
                 or System.Windows.Input.Key.Tab
                 or System.Windows.Input.Key.System)
            return;

        var mods = System.Windows.Input.Keyboard.Modifiers;
        uint winMods = 0;
        if ((mods & System.Windows.Input.ModifierKeys.Alt) != 0) winMods |= 0x0001;
        if ((mods & System.Windows.Input.ModifierKeys.Control) != 0) winMods |= 0x0002;
        if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0) winMods |= 0x0004;
        if ((mods & System.Windows.Input.ModifierKeys.Windows) != 0) winMods |= 0x0008;

        if (winMods == 0)
        {
            HotkeyBox.Text = "Need at least one modifier";
            return;
        }

        var vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);

        var parts = new List<string>();
        if ((winMods & 0x0002) != 0) parts.Add("Ctrl");
        if ((winMods & 0x0004) != 0) parts.Add("Shift");
        if ((winMods & 0x0001) != 0) parts.Add("Alt");
        if ((winMods & 0x0008) != 0) parts.Add("Win");
        parts.Add(key.ToString());

        _recordedHotkeyModifiers = winMods;
        _recordedHotkeyVk = vk;
        _recordedHotkeyDisplay = string.Join("+", parts);
        HotkeyBox.Text = _recordedHotkeyDisplay;
    }

    private void OnHotkeyResetClick(object sender, RoutedEventArgs e)
    {
        _recordedHotkeyModifiers = 0x0002 | 0x0004; // Ctrl + Shift
        _recordedHotkeyVk = 0x4E;                    // N
        _recordedHotkeyDisplay = "Ctrl+Shift+N";
        HotkeyBox.Text = _recordedHotkeyDisplay;
    }
}

public class NotificationAppToggle : INotifyPropertyChanged
{
    private bool _isEnabled;
    public string AppName { get; set; } = string.Empty;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
