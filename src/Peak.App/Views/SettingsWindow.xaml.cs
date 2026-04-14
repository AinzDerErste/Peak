using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using Peak.App.ViewModels;
using Peak.Core.Configuration;
using Peak.Core.Plugins;
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

    // Plugin settings edit state: (pluginId, fieldKey) → TextBox
    private readonly List<(string PluginId, string Key, TextBox Box)> _pluginFieldBoxes = new();

    // Plugin enable/disable state: pluginId → CheckBox (true = enabled)
    private readonly Dictionary<string, CheckBox> _pluginEnabledChecks = new();
    private readonly HashSet<string> _initialDisabledPlugins = new();

    private UpdateService? _updateService;

    public SettingsWindow(SettingsManager settingsManager)
    {
        InitializeComponent();
        _settingsManager = settingsManager;
        LoadSettings();
        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_updateService != null)
            _updateService.UpdateStatusChanged -= OnUpdateStatusChanged;
    }

    private void OnUpdateStatusChanged()
    {
        // Raised from the download thread — marshal to UI without blocking the caller.
        Dispatcher.BeginInvoke(() => RefreshUpdateUI(_updateService!));
    }

    private void LoadSettings()
    {
        VersionText.Text = $"v{UpdateService.CurrentVersion}";
        _updateService = ((App)Application.Current).Services.GetRequiredService<UpdateService>();
        _updateService.UpdateStatusChanged += OnUpdateStatusChanged;
        RefreshUpdateUI(_updateService);

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

        // Network graph style
        if (s.NetworkGraphStyle == NetworkGraphStyle.Bars)
            NetGraphBarsRadio.IsChecked = true;
        else
            NetGraphLineRadio.IsChecked = true;
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

        // Plugins (inline: toggle + settings per card)
        BuildPluginList();
    }

    private void BuildPluginList()
    {
        PluginList.Children.Clear();
        _pluginEnabledChecks.Clear();
        _pluginFieldBoxes.Clear();
        _initialDisabledPlugins.Clear();

        // Remove the old separate PluginsCard — everything is inline now
        PluginsCard.Visibility = Visibility.Collapsed;

        var disabled = _settingsManager.Settings.DisabledPlugins;
        foreach (var id in disabled) _initialDisabledPlugins.Add(id);

        // Discover all plugins on disk (including disabled ones)
        var pluginsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Peak", "plugins");
        var tempLoader = new PluginLoader(pluginsDir);
        var discovered = tempLoader.DiscoverAll();

        // Also include currently loaded plugins (they have richer info)
        var loader = ((App)Application.Current).PluginLoader;
        var loadedIds = new HashSet<string>();
        if (loader != null)
            foreach (var p in loader.LoadedPlugins)
                loadedIds.Add(p.Id);

        // Build settings schemas lookup (only for loaded/enabled plugins)
        var schemasMap = new Dictionary<string, PluginSettingsInfo>();
        if (loader != null)
        {
            foreach (var schema in loader.GetPluginSettingsSchemas())
                schemasMap[schema.PluginId] = schema;
        }

        // Merge: show loaded plugins first, then any discovered-but-disabled ones
        var allPlugins = new List<(string Id, string Name)>();
        if (loader != null)
            foreach (var p in loader.LoadedPlugins)
                allPlugins.Add((p.Id, p.Name));
        foreach (var (id, name) in discovered)
            if (!loadedIds.Contains(id))
                allPlugins.Add((id, name));

        if (allPlugins.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "No plugins installed. Place plugin DLLs into %APPDATA%\\Peak\\plugins\\<name>\\",
                Style = (Style)FindResource("SubLabel"),
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap
            };
            PluginList.Children.Add(hint);
            return;
        }

        var labelStyle = (Style)FindResource("Label");
        var subLabelStyle = (Style)FindResource("SubLabel");
        var cardStyle = (Style)FindResource("Card");

        foreach (var (id, name) in allPlugins)
        {
            bool isEnabled = !disabled.Contains(id);
            bool hasSettings = schemasMap.TryGetValue(id, out var schema) && schema.Fields.Count > 0;

            var card = new Border { Style = cardStyle };
            var cardStack = new StackPanel();

            // ── Header row: name + toggle ──
            var header = new DockPanel();
            var nameBlock = new TextBlock
            {
                Text = name,
                Style = labelStyle,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var toggle = new CheckBox
            {
                Style = (Style)FindResource("ToggleSwitch"),
                IsChecked = isEnabled,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _pluginEnabledChecks[id] = toggle;

            DockPanel.SetDock(nameBlock, Dock.Left);
            header.Children.Add(nameBlock);
            header.Children.Add(toggle);
            cardStack.Children.Add(header);

            // ── Inline settings (only for enabled/loaded plugins with settings) ──
            if (isEnabled && hasSettings && schema != null)
            {
                // Separator
                var sep = new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    Margin = new Thickness(-16, 12, -16, 8)
                };
                cardStack.Children.Add(sep);

                foreach (var field in schema.Fields)
                {
                    // Button fields render as a clickable button, not a text box
                    if (field.Kind == 4) // Button
                    {
                        var pluginId = schema.PluginId;
                        var fieldKey = field.Key;
                        var btn = new Button
                        {
                            Content = field.Label,
                            Style = (Style)FindResource("SubtleButton"),
                            Padding = new Thickness(16, 6, 16, 6),
                            Margin = new Thickness(0, 8, 0, 2),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        btn.Click += (_, _) =>
                        {
                            // Trigger SetSettingValue on the plugin via reflection
                            var loader = ((App)Application.Current).PluginLoader;
                            var plugin = loader?.LoadedPlugins
                                .FirstOrDefault(p => p.Id == pluginId);
                            if (plugin != null)
                            {
                                var iface = plugin.Instance.GetType().GetInterfaces()
                                    .FirstOrDefault(i => i.FullName == "Peak.Plugin.Sdk.IPluginSettingsProvider");
                                var setter = iface?.GetMethod("SetSettingValue");
                                setter?.Invoke(plugin.Instance, [fieldKey, null]);
                            }
                        };
                        cardStack.Children.Add(btn);

                        if (!string.IsNullOrWhiteSpace(field.Description))
                        {
                            var desc = new TextBlock
                            {
                                Text = field.Description,
                                Style = subLabelStyle,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 2, 0, 0)
                            };
                            cardStack.Children.Add(desc);
                        }
                        continue;
                    }

                    var fieldLabel = new TextBlock
                    {
                        Text = field.Label,
                        Style = subLabelStyle,
                        Margin = new Thickness(0, 6, 0, 2)
                    };
                    cardStack.Children.Add(fieldLabel);

                    TextBox box;
                    if (field.Kind == 1) // Password
                    {
                        box = new TextBox
                        {
                            FontFamily = new FontFamily("Consolas"),
                            Text = field.CurrentValue ?? "",
                            Tag = "password"
                        };
                    }
                    else
                    {
                        box = new TextBox { Text = field.CurrentValue ?? "" };
                    }

                    box.FontSize = 13;
                    box.Padding = new Thickness(8, 6, 8, 6);
                    box.Margin = new Thickness(0, 0, 0, 2);
                    box.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
                    box.Foreground = Brushes.White;
                    box.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                    box.BorderThickness = new Thickness(1);
                    box.CaretBrush = Brushes.White;

                    cardStack.Children.Add(box);
                    _pluginFieldBoxes.Add((schema.PluginId, field.Key, box));

                    if (!string.IsNullOrWhiteSpace(field.Description))
                    {
                        var desc = new TextBlock
                        {
                            Text = field.Description,
                            Style = subLabelStyle,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 2, 0, 0)
                        };
                        cardStack.Children.Add(desc);
                    }
                }
            }
            else if (!isEnabled)
            {
                var disabledHint = new TextBlock
                {
                    Text = "Enable plugin to configure settings",
                    Style = subLabelStyle,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                cardStack.Children.Add(disabledHint);
            }

            card.Child = cardStack;
            PluginList.Children.Add(card);
        }
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
        (CollapsedWidget.DiscordCallCount, "Discord Call Count"),
        (CollapsedWidget.TeamSpeakCallCount, "TeamSpeak Call Count"),
        (CollapsedWidget.VoiceCallCount, "Voice Call Count"),
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

        s.NetworkGraphStyle = (NetGraphBarsRadio.IsChecked == true)
            ? NetworkGraphStyle.Bars
            : NetworkGraphStyle.Line;

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

        // Push the new collapsed slot values into the live IslandViewModel and
        // trigger a re-render — otherwise the island keeps showing the layout
        // that was active at startup until the app is reloaded.
        try
        {
            var app = (App)Application.Current;
            var vm = app.Services.GetRequiredService<IslandViewModel>();
            vm.CollapsedLeft = s.CollapsedSlots[0];
            vm.CollapsedCenter = s.CollapsedSlots[1];
            vm.CollapsedRight = s.CollapsedSlots[2];
            app.Services.GetRequiredService<IslandWindow>().RenderCollapsedSlots();
        }
        catch { /* island may not be fully initialized in edge cases */ }

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

        // Plugin enable/disable
        var newDisabled = new HashSet<string>();
        foreach (var (id, check) in _pluginEnabledChecks)
        {
            if (check.IsChecked != true)
                newDisabled.Add(id);
        }
        s.DisabledPlugins = newDisabled;

        bool pluginStateChanged = !newDisabled.SetEquals(_initialDisabledPlugins);

        // Plugin settings: push every field value back into its plugin, then
        // collect the serialized blobs and replace PluginSettings entirely.
        var loader = ((App)Application.Current).PluginLoader;
        if (loader != null && _pluginFieldBoxes.Count > 0)
        {
            foreach (var (pluginId, key, box) in _pluginFieldBoxes)
                loader.SetPluginSetting(pluginId, key, box.Text);

            var collected = loader.CollectAllSettings();
            // Merge (overwrite) rather than replace — keeps entries from plugins
            // that haven't been touched during this session.
            foreach (var kvp in collected)
                s.PluginSettings[kvp.Key] = kvp.Value;
        }

        _settingsManager.Save();
        Close();

        if (pluginStateChanged)
        {
            var result = MessageBox.Show(
                "Plugin changes require a reload to take effect.\nReload now?",
                "Peak",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                // Trigger in-process reload
                typeof(App).GetMethod("ReloadApplication",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(Application.Current, null);
            }
        }
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

    private void SetUpdateStatus(string text, string brushKey, bool showInstallButton)
    {
        UpdateStatusText.Text = text;
        UpdateStatusText.Foreground = (Brush)FindResource(brushKey);
        InstallUpdateButton.Visibility = showInstallButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshUpdateUI(UpdateService svc)
    {
        if (svc.IsDownloaded)
            SetUpdateStatus($"v{svc.NewVersion} ready to install!", "AccentBrush", true);
        else if (svc.IsDownloading)
            SetUpdateStatus($"Downloading v{svc.NewVersion}... {svc.DownloadProgress}%", "TextDim", false);
        else if (svc.DownloadFailed)
            SetUpdateStatus($"v{svc.NewVersion} download failed. Click again to retry.", "TextDim", false);
        else if (svc.UpdateAvailable)
            SetUpdateStatus($"v{svc.NewVersion} available", "TextDim", false);
        else
            SetUpdateStatus("", "TextDim", false);
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateService == null) return;

        CheckUpdateButton.IsEnabled = false;
        SetUpdateStatus("Checking...", "TextDim", false);

        try
        {
            await _updateService.CheckForUpdateAsync();

            if (!_updateService.UpdateAvailable)
                SetUpdateStatus("You're up to date!", "TextDim", false);
        }
        catch
        {
            SetUpdateStatus("Check failed.", "TextDim", false);
        }

        CheckUpdateButton.IsEnabled = true;
    }

    private void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateService == null) return;
        _updateService.InstallUpdate();
        Application.Current.Shutdown();
    }

    private void OnNetGraphStyleChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var style = (NetGraphBarsRadio.IsChecked == true)
            ? NetworkGraphStyle.Bars
            : NetworkGraphStyle.Line;
        _settingsManager.Settings.NetworkGraphStyle = style;
        // Trigger live redraw on any currently-mounted NetworkWidget
        foreach (Window w in Application.Current.Windows)
            RefreshNetworkWidgets(w);
    }

    private static void RefreshNetworkWidgets(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Widgets.NetworkWidget nw)
                nw.RedrawGraph();
            RefreshNetworkWidgets(child);
        }
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

    // ─── Tab crossfade animation ────────────────────────────────

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles from inner ComboBoxes/ListBoxes —
        // only react when the TabControl itself is the source.
        if (e.OriginalSource != sender) return;
        if (sender is not TabControl tabs) return;
        if (tabs.SelectedContent is not UIElement selected) return;

        selected.BeginAnimation(OpacityProperty, null);
        selected.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        selected.BeginAnimation(OpacityProperty, fadeIn);
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
