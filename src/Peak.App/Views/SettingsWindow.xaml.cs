using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using Peak.Core.Configuration;
using Peak.Core.Services;

namespace Peak.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private string _selectedBgColor = "#FF000000";
    private string _selectedAccentColor = "#FF60CDFF";
    private bool _suppressHexEvent;

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

        // Collapsed layout
        LoadCollapsedCombos(s.CollapsedSlots);

        // Audio
        LoadAudioDevices(s.AudioDeviceId);
        SensitivitySlider.Value = s.VisualizerSensitivity;
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

    private void OnBgColorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is string hex)
        {
            _selectedBgColor = hex;
            HighlightColorCircle(BgColorPanel, hex);
            UpdateHexBoxes();
            App.UpdateThemeColors(_selectedBgColor, _selectedAccentColor);
        }
    }

    private void OnAccentColorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is string hex)
        {
            _selectedAccentColor = hex;
            HighlightColorCircle(AccentColorPanel, hex);
            UpdateHexBoxes();
            App.UpdateThemeColors(_selectedBgColor, _selectedAccentColor);
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

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
}
