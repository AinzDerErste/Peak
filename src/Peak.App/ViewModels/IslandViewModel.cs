using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Peak.Core.Configuration;
using Peak.Core.Models;
using Peak.Core.Services;

namespace Peak.App.ViewModels;

public enum IslandState
{
    Hidden,
    Collapsed,
    Peek,
    Expanded
}

public partial class IslandViewModel : ObservableObject
{
    private readonly MediaService _mediaService;
    private readonly SystemMonitorService _systemMonitorService;
    private readonly NetworkMonitorService _networkMonitorService;
    private readonly NotificationService _notificationService;
    private readonly WeatherService _weatherService;
    private readonly CalendarService _calendarService;
    private readonly TimerService _timerService;
    private readonly UpdateService _updateService;
    private readonly SettingsManager _settingsManager;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _autoCollapseTimer;
    private readonly DispatcherTimer _weatherTimer;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty] private IslandState _currentState = IslandState.Collapsed;
    [ObservableProperty] private string _currentTime = DateTime.Now.ToString("HH:mm");
    [ObservableProperty] private string _currentDate = DateTime.Now.ToString("ddd, dd MMM");

    // Media
    [ObservableProperty] private string _mediaTitle = string.Empty;
    [ObservableProperty] private string _mediaArtist = string.Empty;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private BitmapImage? _albumArt;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeekShowsMedia))]
    private bool _hasMedia;

    // System
    [ObservableProperty] private float _cpuUsage;
    [ObservableProperty] private float _memoryUsage;
    [ObservableProperty] private string _memoryText = string.Empty;
    [ObservableProperty] private float? _batteryPercent;
    [ObservableProperty] private bool? _isCharging;
    [ObservableProperty] private bool _hasBattery;

    // Weather
    [ObservableProperty] private string _weatherTemp = string.Empty;
    [ObservableProperty] private string _weatherIcon = "\u2601";
    [ObservableProperty] private string _weatherDescription = string.Empty;
    [ObservableProperty] private string _weatherCity = string.Empty;
    [ObservableProperty] private Geometry? _weatherIconGeometry;

    // SVG Icon helpers
    public Geometry? PlayPauseIcon => IsPlaying
        ? (Geometry?)Application.Current?.TryFindResource("IconPause")
        : (Geometry?)Application.Current?.TryFindResource("IconPlay");

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseIcon));

    // Calendar
    [ObservableProperty] private string _nextEventText = string.Empty;
    [ObservableProperty] private bool _hasNextEvent;

    // Notification
    [ObservableProperty] private string _notificationTitle = string.Empty;
    [ObservableProperty] private string _notificationBody = string.Empty;
    [ObservableProperty] private string _notificationApp = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeekShowsMedia))]
    private bool _hasNotification;
    [ObservableProperty] private ImageSource? _notificationIcon;

    public bool PeekShowsMedia => HasMedia && !HasNotification;

    // Timer
    [ObservableProperty] private string _timerText = "00:00";
    [ObservableProperty] private bool _isTimerRunning;
    [ObservableProperty] private int _timerMinutes = 5;

    // Media progress
    [ObservableProperty] private double _mediaProgress; // 0.0 – 1.0
    [ObservableProperty] private string _mediaPositionText = string.Empty; // e.g. "1:23 / 3:45"
    [ObservableProperty] private bool _hasMediaProgress; // true when Duration > 0

    // Network
    [ObservableProperty] private string _downloadSpeed = "0 B/s";
    [ObservableProperty] private string _uploadSpeed = "0 B/s";

    // Visibility
    [ObservableProperty] private bool _isVisible = true;

    // Widget Grid Slots (6 slots: 2 columns × 3 rows)
    [ObservableProperty] private WidgetType _slot0 = WidgetType.Clock;
    [ObservableProperty] private WidgetType _slot1 = WidgetType.Weather;
    [ObservableProperty] private WidgetType _slot2 = WidgetType.Media;
    [ObservableProperty] private WidgetType _slot3 = WidgetType.SystemMonitor;
    [ObservableProperty] private WidgetType _slot4 = WidgetType.Calendar;
    [ObservableProperty] private WidgetType _slot5 = WidgetType.Timer;

    // Row modes (each row: TwoSlots or Wide)
    [ObservableProperty] private RowMode _row0Mode = RowMode.TwoSlots;
    [ObservableProperty] private RowMode _row1Mode = RowMode.TwoSlots;
    [ObservableProperty] private RowMode _row2Mode = RowMode.TwoSlots;

    // Quick Access
    public ObservableCollection<QuickAccessItem> QuickAccessItems { get; } = new();

    // Collapsed-state slots (Left, Center, Right)
    [ObservableProperty] private CollapsedWidget _collapsedLeft = CollapsedWidget.WeatherIcon;
    [ObservableProperty] private CollapsedWidget _collapsedCenter = CollapsedWidget.Clock;
    [ObservableProperty] private CollapsedWidget _collapsedRight = CollapsedWidget.Weather;

    // Update
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _newVersion = "";

    // Edit mode
    [ObservableProperty] private bool _isEditMode;

    public AppSettings Settings => _settingsManager.Settings;

    public static WidgetType[] AvailableWidgets { get; } =
        Enum.GetValues<WidgetType>();

    public IslandViewModel(
        MediaService mediaService,
        SystemMonitorService systemMonitorService,
        NetworkMonitorService networkMonitorService,
        NotificationService notificationService,
        WeatherService weatherService,
        CalendarService calendarService,
        TimerService timerService,
        UpdateService updateService,
        SettingsManager settingsManager)
    {
        _mediaService = mediaService;
        _systemMonitorService = systemMonitorService;
        _networkMonitorService = networkMonitorService;
        _notificationService = notificationService;
        _weatherService = weatherService;
        _calendarService = calendarService;
        _timerService = timerService;
        _updateService = updateService;
        _settingsManager = settingsManager;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Listen for update status changes
        _updateService.UpdateStatusChanged += () => _dispatcher.Invoke(() =>
        {
            UpdateAvailable = _updateService.UpdateAvailable;
            NewVersion = _updateService.NewVersion;
        });

        // Clock
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            CurrentTime = DateTime.Now.ToString("HH:mm");
            CurrentDate = DateTime.Now.ToString("ddd, dd MMM");
        };

        // Auto-collapse
        _autoCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_settingsManager.Settings.AutoCollapseSeconds)
        };
        _autoCollapseTimer.Tick += (_, _) =>
        {
            _autoCollapseTimer.Stop();
            if (CurrentState is IslandState.Peek or IslandState.Expanded)
                CurrentState = IslandState.Collapsed;
        };

        // Weather polling
        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();

        // Service events
        _mediaService.MediaChanged += OnMediaChanged;
        _mediaService.PositionChanged += OnMediaPositionChanged;
        _mediaService.SessionClosed += () => _dispatcher.Invoke(() => HasMedia = false);
        _systemMonitorService.StatsUpdated += OnStatsUpdated;
        _networkMonitorService.StatsUpdated += OnNetworkStatsUpdated;
        _notificationService.NewNotification += OnNewNotification;
        _timerService.Tick += remaining => _dispatcher.Invoke(() =>
            TimerText = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}");
        _timerService.Finished += () => _dispatcher.Invoke(() =>
        {
            TimerText = "Done!";
            IsTimerRunning = false;
            ShowPeek();
        });
    }

    public async Task InitializeAsync()
    {
        LoadSlotLayout();
        _clockTimer.Start();
        _weatherTimer.Start();

        await _mediaService.InitializeAsync();
        _systemMonitorService.Start();
        _networkMonitorService.Start();
        await _notificationService.InitializeAsync();
        _notificationService.StartPolling();
        await RefreshWeatherAsync();
        await RefreshCalendarAsync();
    }

    private void LoadSlotLayout()
    {
        var slots = _settingsManager.Settings.WidgetSlots;
        if (slots is { Length: >= 6 })
        {
            Slot0 = slots[0]; Slot1 = slots[1];
            Slot2 = slots[2]; Slot3 = slots[3];
            Slot4 = slots[4]; Slot5 = slots[5];
        }

        var modes = _settingsManager.Settings.RowModes;
        if (modes is { Length: >= 3 })
        {
            Row0Mode = modes[0]; Row1Mode = modes[1]; Row2Mode = modes[2];
        }

        var collapsed = _settingsManager.Settings.CollapsedSlots;
        if (collapsed is { Length: >= 3 })
        {
            CollapsedLeft = collapsed[0]; CollapsedCenter = collapsed[1]; CollapsedRight = collapsed[2];
        }

        // Load quick access items
        QuickAccessItems.Clear();
        foreach (var path in _settingsManager.Settings.QuickAccessPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var isFolder = Directory.Exists(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;
            QuickAccessItems.Add(new QuickAccessItem { Path = path, Name = name, IsFolder = isFolder });
        }
    }

    private void SaveSlotLayout()
    {
        _settingsManager.Settings.WidgetSlots = [Slot0, Slot1, Slot2, Slot3, Slot4, Slot5];
        _settingsManager.Settings.RowModes = [Row0Mode, Row1Mode, Row2Mode];
        _settingsManager.Settings.CollapsedSlots = [CollapsedLeft, CollapsedCenter, CollapsedRight];
        _settingsManager.Settings.QuickAccessPaths = QuickAccessItems.Select(i => i.Path).ToArray();
        _settingsManager.Save();
    }

    public void SaveQuickAccess()
    {
        _settingsManager.Settings.QuickAccessPaths = QuickAccessItems.Select(i => i.Path).ToArray();
        _settingsManager.Save();
    }

    public void SetSlot(int index, WidgetType type)
    {
        switch (index)
        {
            case 0: Slot0 = type; break;
            case 1: Slot1 = type; break;
            case 2: Slot2 = type; break;
            case 3: Slot3 = type; break;
            case 4: Slot4 = type; break;
            case 5: Slot5 = type; break;
        }
        SaveSlotLayout();
    }

    public void SetRowMode(int row, RowMode mode)
    {
        switch (row)
        {
            case 0: Row0Mode = mode; break;
            case 1: Row1Mode = mode; break;
            case 2: Row2Mode = mode; break;
        }

        // When switching to Wide, clear the right slot
        if (mode == RowMode.Wide)
        {
            var rightSlotIndex = row * 2 + 1;
            SetSlot(rightSlotIndex, WidgetType.None);
        }

        SaveSlotLayout();
    }

    public RowMode GetRowMode(int row) => row switch
    {
        0 => Row0Mode, 1 => Row1Mode, 2 => Row2Mode,
        _ => RowMode.TwoSlots
    };

    [RelayCommand]
    private void AddQuickAccess()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add Quick Access",
            Filter = "All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.FileNames)
            {
                if (QuickAccessItems.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var name = Path.GetFileName(path);
                QuickAccessItems.Add(new QuickAccessItem { Path = path, Name = name, IsFolder = false });
            }
            SaveSlotLayout();
        }
    }

    [RelayCommand]
    private void RemoveQuickAccess(QuickAccessItem? item)
    {
        if (item == null) return;
        QuickAccessItems.Remove(item);
        SaveSlotLayout();
    }

    [RelayCommand]
    private void OpenQuickAccess(QuickAccessItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true
            });
        }
        catch { /* File not found or access denied */ }
    }

    [RelayCommand]
    private void ToggleEditMode()
    {
        IsEditMode = !IsEditMode;
        if (IsEditMode)
            Expand();
    }

    [RelayCommand]
    private void ExitEditMode()
    {
        IsEditMode = false;
    }

    private void OnMediaChanged(MediaInfo info)
    {
        _dispatcher.Invoke(() =>
        {
            MediaTitle = info.Title;
            MediaArtist = info.Artist;
            IsPlaying = info.IsPlaying;
            HasMedia = true;

            if (info.Thumbnail != null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(info.Thumbnail);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                AlbumArt = bmp;
            }

            // Only peek if collapsed — don't disturb Hidden or Expanded states
            if (CurrentState == IslandState.Collapsed)
                ShowPeek();
        });
    }

    private void OnMediaPositionChanged(TimeSpan position, TimeSpan duration)
    {
        _dispatcher.Invoke(() =>
        {
            if (duration.TotalSeconds > 0)
            {
                HasMediaProgress = true;
                MediaProgress = Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
                MediaPositionText = $"{FormatTime(position)} / {FormatTime(duration)}";
            }
            else
            {
                HasMediaProgress = false;
                MediaProgress = 0;
                MediaPositionText = string.Empty;
            }
        });
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OnStatsUpdated(SystemStats stats)
    {
        _dispatcher.Invoke(() =>
        {
            CpuUsage = stats.CpuUsage;
            MemoryUsage = stats.MemoryUsagePercent;
            MemoryText = $"{stats.MemoryUsedMB / 1024.0:F1}/{stats.MemoryTotalMB / 1024.0:F1} GB";
            BatteryPercent = stats.BatteryPercent;
            IsCharging = stats.IsCharging;
            HasBattery = stats.BatteryPercent.HasValue;
        });
    }

    private void OnNetworkStatsUpdated(NetworkStats stats)
    {
        _dispatcher.Invoke(() =>
        {
            DownloadSpeed = NetworkMonitorService.FormatSpeed(stats.DownloadBytesPerSec);
            UploadSpeed = NetworkMonitorService.FormatSpeed(stats.UploadBytesPerSec);
        });
    }

    private void OnNewNotification(NotificationData data)
    {
        _dispatcher.Invoke(() =>
        {
            NotificationTitle = data.Title;
            NotificationBody = data.Body;
            NotificationApp = data.AppName;

            if (data.IconBytes is { Length: > 0 })
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(data.IconBytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                NotificationIcon = bmp;
            }
            else
            {
                NotificationIcon = null;
            }

            HasNotification = true;

            if (CurrentState == IslandState.Collapsed)
            {
                // Auto-peek to show the notification
                ShowPeek();
            }
            else if (CurrentState == IslandState.Peek)
            {
                // Already peeking — restart the auto-collapse timer
                _autoCollapseTimer.Stop();
                _autoCollapseTimer.Interval = TimeSpan.FromSeconds(_settingsManager.Settings.AutoCollapseSeconds);
                _autoCollapseTimer.Start();
            }
            else if (CurrentState == IslandState.Expanded)
            {
                // Clear banner after 5 seconds when expanded
                Task.Delay(TimeSpan.FromSeconds(5))
                    .ContinueWith(_ => _dispatcher.Invoke(() => HasNotification = false));
            }
        });
    }

    private async Task RefreshWeatherAsync()
    {
        var s = _settingsManager.Settings;
        var weather = await _weatherService.FetchSmartAsync(s.WeatherPostalCode, s.WeatherCountryCode, s.WeatherLat, s.WeatherLon);
        if (weather != null)
        {
            _dispatcher.Invoke(() =>
            {
                WeatherTemp = $"{weather.Temperature:F0}°C";
                WeatherIcon = weather.Icon;
                WeatherDescription = weather.Description;
                WeatherCity = weather.CityName;
                WeatherIconGeometry = MapWeatherIconToGeometry(weather.Icon);
            });
        }
    }

    private static Geometry? MapWeatherIconToGeometry(string icon)
    {
        var key = icon switch
        {
            "☀️" or "🌞" or "☀" => "IconSun",
            "⛅" or "🌤" or "🌤️" => "IconCloudSun",
            "☁️" or "☁" or "🌥" or "🌥️" => "IconCloud",
            "🌧" or "🌧️" or "🌦" or "🌦️" => "IconCloudRain",
            "⛈" or "⛈️" or "🌩" or "🌩️" => "IconBolt",
            "❄️" or "❄" or "🌨" or "🌨️" => "IconSnowflake",
            "🌫" or "🌫️" => "IconCloud",
            _ => "IconCloudSun"
        };
        return (Geometry?)Application.Current?.TryFindResource(key);
    }

    private async Task RefreshCalendarAsync()
    {
        var evt = await _calendarService.GetNextEventAsync();
        _dispatcher.Invoke(() =>
        {
            if (evt != null)
            {
                NextEventText = $"{evt.StartTime:HH:mm} {evt.Subject}";
                HasNextEvent = true;
            }
            else
            {
                HasNextEvent = false;
            }
        });
    }

    public void ShowPeek()
    {
        if (_settingsManager.Settings.Behavior == IslandBehavior.EventOnly && CurrentState == IslandState.Collapsed)
            IsVisible = true;

        CurrentState = IslandState.Peek;
        _autoCollapseTimer.Stop();
        _autoCollapseTimer.Interval = TimeSpan.FromSeconds(_settingsManager.Settings.AutoCollapseSeconds);
        _autoCollapseTimer.Start();
    }

    public void Expand()
    {
        _autoCollapseTimer.Stop();
        CurrentState = IslandState.Expanded;
    }

    public void Collapse()
    {
        _autoCollapseTimer.Stop();
        CurrentState = IslandState.Collapsed;

        if (_settingsManager.Settings.Behavior == IslandBehavior.EventOnly)
            IsVisible = false;
    }

    public void HideIsland()
    {
        _autoCollapseTimer.Stop();
        CurrentState = IslandState.Hidden;
    }

    [RelayCommand]
    private async Task PlayPause() => await _mediaService.PlayPauseAsync();

    [RelayCommand]
    private async Task NextTrack() => await _mediaService.NextAsync();

    [RelayCommand]
    private async Task PreviousTrack() => await _mediaService.PreviousAsync();

    [RelayCommand]
    private void StartTimer()
    {
        _timerService.Start(TimeSpan.FromMinutes(TimerMinutes));
        IsTimerRunning = true;
    }

    [RelayCommand]
    private void StopTimer()
    {
        _timerService.Stop();
        IsTimerRunning = false;
        TimerText = "00:00";
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        _updateService.InstallUpdate();
        Application.Current?.Shutdown();
    }

    public void Cleanup()
    {
        _clockTimer.Stop();
        _autoCollapseTimer.Stop();
        _weatherTimer.Stop();
        _mediaService.Dispose();
        _systemMonitorService.Dispose();
        _networkMonitorService.Dispose();
        _notificationService.Dispose();
        _timerService.Dispose();
        _updateService.Stop();
    }
}
