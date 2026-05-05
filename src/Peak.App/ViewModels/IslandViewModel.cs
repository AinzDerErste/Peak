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
    Expanded,
    Spotlight
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
    private readonly PomodoroService _pomodoroService;
    private readonly UpdateService _updateService;
    private readonly ClipboardService _clipboardService;
    private readonly NotesService _notesService;
    private readonly VolumeMixerService _volumeMixerService;
    private readonly SearchService _searchService;
    private readonly SettingsManager _settingsManager;
    public SettingsManager SettingsManager => _settingsManager;
    public AppSettings Settings => _settingsManager.Settings;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _autoCollapseTimer;
    private readonly DispatcherTimer _weatherTimer;
    private readonly Dispatcher _dispatcher;
    private readonly Queue<NotificationData> _notificationQueue = new();

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
    [NotifyPropertyChangedFor(nameof(IsLiveStream))]
    private bool _hasMedia;

    // System
    [ObservableProperty] private float _cpuUsage;
    [ObservableProperty] private float _gpuUsage;
    [ObservableProperty] private float _memoryUsage;
    [ObservableProperty] private float? _batteryPercent;
    [ObservableProperty] private bool? _isCharging;
    [ObservableProperty] private bool _hasBattery;

    // Weather
    [ObservableProperty] private string _weatherTemp = string.Empty;
    [ObservableProperty] private string _weatherDescription = string.Empty;
    [ObservableProperty] private string _weatherCity = string.Empty;
    [ObservableProperty] private Geometry? _weatherIconGeometry;

    // Discord (set by plugin)
    [ObservableProperty] private int _discordCallCount;
    [ObservableProperty] private string _discordCallCountDisplay = "—";

    // TeamSpeak (set by plugin)
    [ObservableProperty] private int _teamSpeakCallCount;
    [ObservableProperty] private string _teamSpeakCallCountDisplay = "";

    // Plugin interaction lock (e.g. incoming call overlay)
    [ObservableProperty] private bool _expansionBlocked;

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
    public string CurrentNotificationAumid { get; private set; } = string.Empty;

    public bool PeekShowsMedia => HasMedia && !HasNotification;

    // Timer
    [ObservableProperty] private string _timerText = "00:00";
    [ObservableProperty] private bool _isTimerRunning;
    [ObservableProperty] private int _timerMinutes = 5;

    // Pomodoro
    [ObservableProperty] private string _pomodoroText = "25:00";
    [ObservableProperty] private string _pomodoroPhaseLabel = "Focus";
    [ObservableProperty] private bool _isPomodoroRunning;
    [ObservableProperty] private bool _isPomodoroActive; // running OR paused mid-phase
    [ObservableProperty] private int _pomodoroCompletedSessions;
    [ObservableProperty] private bool _hasPomodoroSessions;
    [ObservableProperty] private double _pomodoroProgress; // 0..1
    [ObservableProperty] private System.Windows.Media.Color _pomodoroPhaseColor = System.Windows.Media.Color.FromArgb(0x55, 0x60, 0xCD, 0xFF);

    // Media progress
    [ObservableProperty] private double _mediaProgress; // 0.0 – 1.0
    [ObservableProperty] private string _mediaPositionText = string.Empty; // e.g. "1:23 / 3:45"
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveStream))]
    private bool _hasMediaProgress; // true when Duration > 0
    /// <summary>
    /// True when something is playing but it has no finite duration — i.e. a
    /// livestream (Twitch / YouTube Live / radio). The Media widget swaps the
    /// empty progress bar for a "LIVE" badge in this case.
    /// </summary>
    public bool IsLiveStream => HasMedia && !HasMediaProgress;

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

    // Clipboard History
    public ObservableCollection<ClipboardEntry> ClipboardHistory { get; } = new();

    // Quick Notes
    public ObservableCollection<NoteItem> Notes { get; } = new();
    [ObservableProperty] private NoteItem? _selectedNote;

    // Volume Mixer
    public ObservableCollection<AudioSession> AudioSessions { get; } = new();

    // Spotlight Search
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchResults))]
    private string _searchQuery = string.Empty;
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    [ObservableProperty] private SearchResult? _selectedSearchResult;
    public bool HasSearchResults => SearchResults.Count > 0;
    public bool ShowNoMatchesHint => !HasSearchResults && !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>
    /// Re-runs the search whenever the bound TextBox text changes.
    /// </summary>
    partial void OnSearchQueryChanged(string value)
    {
        SearchResults.Clear();
        foreach (var r in _searchService.Search(value))
            SearchResults.Add(r);
        SelectedSearchResult = SearchResults.FirstOrDefault();
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowNoMatchesHint));
    }

    /// <summary>
    /// Enters Spotlight mode: clears any prior query/results and signals the
    /// IslandWindow to switch state. The window is then responsible for
    /// activating itself and focusing the search TextBox.
    /// </summary>
    [RelayCommand]
    public void OpenSpotlight()
    {
        SearchQuery = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        CurrentState = IslandState.Spotlight;
    }

    /// <summary>Leaves Spotlight mode and returns to the collapsed island.</summary>
    [RelayCommand]
    public void CloseSpotlight()
    {
        if (CurrentState != IslandState.Spotlight) return;
        SearchQuery = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        CurrentState = IslandState.Collapsed;
    }

    /// <summary>
    /// Launches the given result (or the currently selected one when null) and
    /// closes Spotlight. Bound to Enter / list double-click.
    /// </summary>
    [RelayCommand]
    public void LaunchSearchResult(SearchResult? result)
    {
        result ??= SelectedSearchResult;
        if (result == null) return;
        _searchService.Launch(result);
        CloseSpotlight();
    }

    /// <summary>
    /// Moves the keyboard selection by <paramref name="delta"/> rows (Up/Down arrow keys),
    /// clamped to the visible result list.
    /// </summary>
    public void MoveSearchSelection(int delta)
    {
        if (SearchResults.Count == 0) return;
        var idx = SelectedSearchResult == null ? 0 : SearchResults.IndexOf(SelectedSearchResult);
        idx = Math.Clamp(idx + delta, 0, SearchResults.Count - 1);
        SelectedSearchResult = SearchResults[idx];
    }

    // Collapsed-state slots (Left, Center, Right)
    [ObservableProperty] private CollapsedWidget _collapsedLeft = CollapsedWidget.WeatherIcon;
    [ObservableProperty] private CollapsedWidget _collapsedCenter = CollapsedWidget.Clock;
    [ObservableProperty] private CollapsedWidget _collapsedRight = CollapsedWidget.Weather;

    // Update
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _newVersion = "";

    // Edit mode
    [ObservableProperty] private bool _isEditMode;

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
        PomodoroService pomodoroService,
        UpdateService updateService,
        ClipboardService clipboardService,
        NotesService notesService,
        VolumeMixerService volumeMixerService,
        SearchService searchService,
        SettingsManager settingsManager)
    {
        _mediaService = mediaService;
        _systemMonitorService = systemMonitorService;
        _networkMonitorService = networkMonitorService;
        _notificationService = notificationService;
        _weatherService = weatherService;
        _calendarService = calendarService;
        _timerService = timerService;
        _pomodoroService = pomodoroService;
        _updateService = updateService;
        _clipboardService = clipboardService;
        _notesService = notesService;
        _volumeMixerService = volumeMixerService;
        _searchService = searchService;
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

            // If more notifications are queued, show the next one
            if (_notificationQueue.Count > 0)
            {
                var next = _notificationQueue.Dequeue();
                ShowNotificationData(next);
            }
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

        _pomodoroService.Tick += () => _dispatcher.Invoke(UpdatePomodoroBindings);
        _pomodoroService.PhaseChanged += _ => _dispatcher.Invoke(UpdatePomodoroBindings);
        _pomodoroService.PhaseFinished += phase => _dispatcher.Invoke(() =>
        {
            var next = phase == PomodoroPhase.Work
                ? (_pomodoroService.CompletedWorkSessions % _pomodoroService.LongBreakEvery == 0
                    ? "Long break" : "Short break")
                : "Focus time";
            NotificationApp = "Pomodoro";
            NotificationTitle = $"{PhaseDisplayName(phase)} finished";
            NotificationBody = $"Next: {next}";
            NotificationIcon = null;
            HasNotification = true;
            ShowPeek();
        });
    }

    private void UpdatePomodoroBindings()
    {
        var r = _pomodoroService.Remaining;
        if (r < TimeSpan.Zero) r = TimeSpan.Zero;
        PomodoroText = $"{(int)r.TotalMinutes:D2}:{r.Seconds:D2}";
        PomodoroPhaseLabel = PhaseDisplayName(_pomodoroService.Phase);
        IsPomodoroRunning = _pomodoroService.IsRunning;
        PomodoroCompletedSessions = _pomodoroService.CompletedWorkSessions;
        HasPomodoroSessions = _pomodoroService.CompletedWorkSessions > 0;

        var total = _pomodoroService.Phase switch
        {
            PomodoroPhase.Work => _pomodoroService.WorkDuration,
            PomodoroPhase.ShortBreak => _pomodoroService.ShortBreakDuration,
            PomodoroPhase.LongBreak => _pomodoroService.LongBreakDuration,
            _ => _pomodoroService.WorkDuration
        };
        PomodoroProgress = total.TotalSeconds > 0
            ? 1.0 - r.TotalSeconds / total.TotalSeconds
            : 0;

        // Active = running OR mid-phase pause (progress > 0 and phase not idle)
        IsPomodoroActive = _pomodoroService.IsRunning || PomodoroProgress > 0.0001;

        var accent = GetAccentColor();
        PomodoroPhaseColor = _pomodoroService.Phase switch
        {
            PomodoroPhase.Work => WithAlpha(accent, 0x66),
            PomodoroPhase.ShortBreak => System.Windows.Media.Color.FromArgb(0x66, 0x4A, 0xDE, 0x80),
            PomodoroPhase.LongBreak => System.Windows.Media.Color.FromArgb(0x66, 0x34, 0xD3, 0x99),
            _ => WithAlpha(accent, 0x00)
        };
    }

    private static System.Windows.Media.Color GetAccentColor()
    {
        if (System.Windows.Application.Current?.Resources["AccentBrush"] is System.Windows.Media.SolidColorBrush brush)
            return brush.Color;
        return System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF);
    }

    private static System.Windows.Media.Color WithAlpha(System.Windows.Media.Color c, byte a)
        => System.Windows.Media.Color.FromArgb(a, c.R, c.G, c.B);

    private static string PhaseDisplayName(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Work => "Focus",
        PomodoroPhase.ShortBreak => "Break",
        PomodoroPhase.LongBreak => "Long Break",
        _ => "Focus"
    };

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
        InitializeClipboard();
        InitializeNotes();
        InitializeVolumeMixer();
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

    public WidgetType GetSlot(int index) => index switch
    {
        0 => Slot0, 1 => Slot1, 2 => Slot2,
        3 => Slot3, 4 => Slot4, 5 => Slot5,
        _ => WidgetType.None
    };

    public void SwapSlots(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex || fromIndex < 0 || fromIndex > 5 || toIndex < 0 || toIndex > 5) return;

        var fromType = GetSlot(fromIndex);
        var toType = GetSlot(toIndex);
        SetSlotDirect(fromIndex, toType);
        SetSlotDirect(toIndex, fromType);
        SaveSlotLayout();
    }

    private void SetSlotDirect(int index, WidgetType type)
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

            // Decode the album-art on a background thread — JPEG/PNG decode
            // for a 300×300 thumbnail can take 5-30ms on the UI thread, which
            // shows up as a visible stutter on every track change. Freeze()
            // makes the BitmapImage cross-thread-safe so we can assign the
            // resulting bitmap back to the bound property from the UI thread.
            if (info.Thumbnail is { Length: > 0 } thumb)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = new MemoryStream(thumb);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        _dispatcher.BeginInvoke((Action)(() => AlbumArt = bmp));
                    }
                    catch { /* malformed thumbnail bytes — keep previous art */ }
                });
            }

            // Only peek if collapsed — don't disturb Hidden or Expanded states
            if (CurrentState == IslandState.Collapsed)
                ShowPeek();
        });
    }

    // Cached "previous" snapshot — skip the UI dispatch entirely if nothing
    // visible changed (1Hz tick × seconds rounded → most ticks are no-ops).
    private int _lastPositionSec = -1;
    private int _lastDurationSec = -1;

    private void OnMediaPositionChanged(TimeSpan position, TimeSpan duration)
    {
        var posSec = (int)position.TotalSeconds;
        var durSec = (int)duration.TotalSeconds;
        if (posSec == _lastPositionSec && durSec == _lastDurationSec) return;
        _lastPositionSec = posSec;
        _lastDurationSec = durSec;

        // BeginInvoke (was Invoke) so the background MediaService thread
        // doesn't block waiting for the dispatcher when the UI is busy.
        _dispatcher.BeginInvoke((Action)(() =>
        {
            if (durSec > 0)
            {
                HasMediaProgress = true;
                MediaProgress = Math.Clamp((double)posSec / durSec, 0, 1);
                MediaPositionText = $"{FormatTime(position)} / {FormatTime(duration)}";
            }
            else
            {
                HasMediaProgress = false;
                MediaProgress = 0;
                MediaPositionText = string.Empty;
            }
        }));
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OnStatsUpdated(SystemStats stats)
    {
        // BeginInvoke from the SystemMonitorService background thread —
        // [ObservableProperty] already short-circuits same-value writes, so
        // no extra dedup needed here.
        _dispatcher.BeginInvoke((Action)(() =>
        {
            CpuUsage = stats.CpuUsage;
            GpuUsage = stats.GpuUsage;
            MemoryUsage = stats.MemoryUsagePercent;
            BatteryPercent = stats.BatteryPercent;
            IsCharging = stats.IsCharging;
            HasBattery = stats.BatteryPercent.HasValue;
        }));
    }

    // Bucketed cache so we only push string updates when the formatted speed
    // actually changed. FormatSpeed allocates a new string per call —
    // unnecessary churn on quiet links.
    private long _lastDownloadBucket = -1;
    private long _lastUploadBucket = -1;

    private void OnNetworkStatsUpdated(NetworkStats stats)
    {
        // Round to 1 KB/s buckets — sub-bucket jitter is invisible anyway.
        var dlBucket = (long)(stats.DownloadBytesPerSec / 1024);
        var ulBucket = (long)(stats.UploadBytesPerSec / 1024);
        if (dlBucket == _lastDownloadBucket && ulBucket == _lastUploadBucket) return;
        _lastDownloadBucket = dlBucket;
        _lastUploadBucket = ulBucket;

        _dispatcher.BeginInvoke((Action)(() =>
        {
            DownloadSpeed = NetworkMonitorService.FormatSpeed(stats.DownloadBytesPerSec);
            UploadSpeed = NetworkMonitorService.FormatSpeed(stats.UploadBytesPerSec);
        }));
    }

    private void OnNewNotification(NotificationData data)
    {
        _dispatcher.Invoke(() =>
        {
            var settings = _settingsManager.Settings;

            // Track seen apps so users can mute them in Settings. The save
            // itself runs on a thread-pool thread — JsonSerializer + temp-file
            // + File.Move on the dispatcher used to be a real hitch on first
            // notification from each app.
            if (!string.IsNullOrEmpty(data.AppName) &&
                !settings.SeenNotificationApps.Contains(data.AppName))
            {
                settings.SeenNotificationApps.Add(data.AppName);
                _ = Task.Run(() => _settingsManager.Save());
            }

            // Respect per-app mute
            if (settings.MutedNotificationApps.Contains(data.AppName))
                return;

            // If we're already peeking a notification, queue this one for after the current peek
            if (CurrentState == IslandState.Peek && HasNotification)
            {
                _notificationQueue.Enqueue(data);
                return;
            }

            ShowNotificationData(data);
        });
    }

    /// <summary>
    /// Background-thread-safe layers of the notification-icon resolver:
    ///
    ///   1. The bytes the toast itself ships (<see cref="NotificationData.IconBytes"/>) —
    ///      from <c>UserNotification.AppInfo.DisplayInfo.GetLogo</c>. Most reliable for
    ///      packaged apps (Discord, Spotify, Teams).
    ///   2. <see cref="Helpers.IconExtractor"/> via the AppUserModelId. The same
    ///      <c>IShellItemImageFactory</c> path Peak uses for Spotlight icons —
    ///      catches Win32 apps and UWP apps the toast's DisplayInfo missed.
    ///
    /// Returns null if both layers failed; the caller (UI thread) then falls
    /// back to <see cref="BuildLetterAvatar"/>, which can't run off-thread
    /// because it draws into a <c>RenderTargetBitmap</c> via DrawingVisual.
    /// </summary>
    private static ImageSource? TryResolveNotificationIconOffThread(NotificationData data)
    {
        // Layer 1: bytes embedded in the toast.
        if (data.IconBytes is { Length: > 0 })
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(data.IconBytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze(); // makes the BitmapImage cross-thread-safe
                return bmp;
            }
            catch { /* malformed bytes — fall through to next layer */ }
        }

        // Layer 2: shell:appsFolder lookup keyed by the AppUserModelId. Same
        // mechanism as the Spotlight result icons — pulls whatever Explorer
        // would show for that app from the Shell namespace.
        if (!string.IsNullOrWhiteSpace(data.AppUserModelId))
        {
            try
            {
                var shellIcon = Helpers.IconExtractor.GetIcon($"shell:appsFolder\\{data.AppUserModelId}", 32);
                if (shellIcon is System.Windows.Freezable f && f.CanFreeze && !f.IsFrozen) f.Freeze();
                if (shellIcon != null) return shellIcon;
            }
            catch { /* shell call can throw on broken AUMIDs */ }
        }

        return null;
    }

    /// <summary>
    /// Renders a 32 × 32 circle with a single uppercase letter in the centre,
    /// colour derived from a stable hash of <paramref name="appName"/> so the
    /// same app always lands on the same colour.
    /// </summary>
    private static ImageSource BuildLetterAvatar(string appName)
    {
        const int size = 32;
        var letter = string.IsNullOrWhiteSpace(appName) ? "?" : char.ToUpperInvariant(appName.Trim()[0]).ToString();
        var bg = HashToColor(appName);

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawEllipse(new SolidColorBrush(bg), null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);

            var fmt = new FormattedText(
                letter,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                size * 0.55,
                Brushes.White,
                pixelsPerDip: 1.0);
            var origin = new Point((size - fmt.Width) / 2.0, (size - fmt.Height) / 2.0);
            ctx.DrawText(fmt, origin);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Deterministic but pleasant colour from a string. Uses a simple hash to
    /// pick a hue from a curated set — same input always maps to the same
    /// output, so an app keeps its colour across sessions.
    /// </summary>
    private static Color HashToColor(string text)
    {
        // Hand-picked palette of saturated-but-readable colours. Letters render
        // white on top so we keep the lightness moderate.
        var palette = new[]
        {
            Color.FromRgb(0x60, 0xA5, 0xFA), // blue
            Color.FromRgb(0xF8, 0x71, 0x71), // red
            Color.FromRgb(0x34, 0xD3, 0x99), // green
            Color.FromRgb(0xFB, 0xBF, 0x24), // amber
            Color.FromRgb(0xA7, 0x8B, 0xFA), // violet
            Color.FromRgb(0xF4, 0x72, 0xB6), // pink
            Color.FromRgb(0x2D, 0xD4, 0xBF), // teal
            Color.FromRgb(0xFB, 0x92, 0x3C), // orange
        };

        int hash = 0;
        foreach (var c in text ?? "")
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        return palette[hash % palette.Length];
    }

    private void ShowNotificationData(NotificationData data)
    {
        NotificationTitle = data.Title;
        NotificationBody = data.Body;
        NotificationApp = data.AppName;
        CurrentNotificationAumid = data.AppUserModelId;

        // Resolve the icon on a background thread — Layer 1 (BitmapImage
        // decode) and Layer 2 (shell:appsFolder lookup via IShellItemImage-
        // Factory) are the slow paths and don't need the UI thread. The
        // letter-avatar fallback (Layer 3) draws into a RenderTargetBitmap
        // which DOES need a UI/STA thread, so it stays on the dispatcher.
        // We clear the icon up front so a stale one doesn't peek through
        // while the resolver runs.
        NotificationIcon = null;
        _ = Task.Run(() =>
        {
            var resolved = TryResolveNotificationIconOffThread(data);
            _dispatcher.BeginInvoke((Action)(() =>
            {
                NotificationIcon = resolved ?? BuildLetterAvatar(data.AppName);
            }));
        });

        HasNotification = true;

        if (CurrentState == IslandState.Collapsed)
        {
            ShowPeek();
        }
        else if (CurrentState == IslandState.Expanded)
        {
            // Clear banner after 5 seconds when expanded
            Task.Delay(TimeSpan.FromSeconds(5))
                .ContinueWith(_ => _dispatcher.Invoke(() => HasNotification = false));
        }
    }

    public void PauseAutoCollapse() => _autoCollapseTimer.Stop();

    public void ResumeAutoCollapse()
    {
        _autoCollapseTimer.Stop();
        _autoCollapseTimer.Interval = TimeSpan.FromSeconds(_settingsManager.Settings.AutoCollapseSeconds);
        _autoCollapseTimer.Start();
    }

    public void OpenCurrentNotificationApp()
    {
        var aumid = CurrentNotificationAumid;
        if (string.IsNullOrWhiteSpace(aumid)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{aumid}",
                UseShellExecute = true
            });
        }
        catch { /* ignore — fail silently if app can't be launched */ }
    }

    public void DismissCurrentNotification()
    {
        HasNotification = false;
        _notificationQueue.Clear();
        if (CurrentState == IslandState.Peek)
            CurrentState = IslandState.Collapsed;
        _autoCollapseTimer.Stop();
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
    private void StartPomodoro() => _pomodoroService.Start();

    [RelayCommand]
    private void PausePomodoro() => _pomodoroService.Pause();

    [RelayCommand]
    private void SkipPomodoro() => _pomodoroService.Skip();

    [RelayCommand]
    private void ResetPomodoro() => _pomodoroService.Reset();

    [RelayCommand]
    private void InstallUpdate()
    {
        _updateService.InstallUpdate();
        Application.Current?.Shutdown();
    }

    // ─── Clipboard History ─────────────────────────────────────

    private DispatcherTimer? _clipboardPollTimer;

    private void InitializeClipboard()
    {
        // Wire up STA-thread clipboard delegates
        _clipboardService.GetClipboardText = () =>
        {
            try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
            catch { return null; }
        };
        _clipboardService.ClipboardHasImage = () =>
        {
            try { return System.Windows.Clipboard.ContainsImage(); }
            catch { return false; }
        };
        _clipboardService.GetClipboardImageHash = () =>
        {
            try
            {
                var img = System.Windows.Clipboard.GetImage();
                if (img == null) return null;
                // Cheap fingerprint: dimensions + a 64-byte slice from the
                // first row only. Was allocating the full pixel buffer every
                // poll (could be 8+ MB for a 4K screenshot) just to throw it
                // away after sampling — that's the whole image churning
                // through the LOH on each clipboard tick.
                int bpp = (img.Format.BitsPerPixel + 7) / 8;
                int stride = img.PixelWidth * bpp;
                int sampleBytes = Math.Min(stride, 64);
                var sample = new byte[sampleBytes];
                img.CopyPixels(new Int32Rect(0, 0, sampleBytes / bpp, 1), sample, sampleBytes, 0);
                long hash = img.PixelWidth * 31L + img.PixelHeight;
                for (int i = 0; i < sample.Length; i++) hash = hash * 31 + sample[i];
                return hash.ToString();
            }
            catch { return null; }
        };
        _clipboardService.SaveClipboardImage = dir =>
        {
            try
            {
                var img = System.Windows.Clipboard.GetImage();
                if (img == null) return null;
                var path = System.IO.Path.Combine(dir, $"{Guid.NewGuid()}.png");
                using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                encoder.Save(fs);
                return path;
            }
            catch { return null; }
        };
        _clipboardService.GetClipboardFiles = () =>
        {
            try
            {
                return System.Windows.Clipboard.ContainsFileDropList()
                    ? System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray()
                    : null;
            }
            catch { return null; }
        };
        _clipboardService.SetClipboardText = text =>
        {
            try { System.Windows.Clipboard.SetText(text); } catch { }
        };

        // Load existing history
        RefreshClipboardHistory();

        // Subscribe to changes
        _clipboardService.HistoryChanged += () => _dispatcher.Invoke(RefreshClipboardHistory);

        // Poll clipboard every 500ms on UI thread
        // 1000ms (was 500ms) — clipboard changes are user-triggered, half-
        // second granularity is invisible. Halves the dispatcher load from
        // this poll on its own.
        _clipboardPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _clipboardPollTimer.Tick += (_, _) => _clipboardService.Poll();
        _clipboardPollTimer.Start();
    }

    private void RefreshClipboardHistory()
    {
        ClipboardHistory.Clear();
        foreach (var entry in _clipboardService.GetHistory())
            ClipboardHistory.Add(entry);
    }

    [RelayCommand]
    private void CopyClipboardEntry(ClipboardEntry entry) => _clipboardService.CopyToClipboard(entry);

    [RelayCommand]
    private void RemoveClipboardEntry(ClipboardEntry entry) => _clipboardService.Remove(entry);

    [RelayCommand]
    private void ClearClipboardHistory() => _clipboardService.ClearAll();

    // ─── Quick Notes ────────────────────────────────────────────

    private void InitializeNotes()
    {
        RefreshNotes();
        _notesService.NotesChanged += () => _dispatcher.Invoke(RefreshNotes);
    }

    private void RefreshNotes()
    {
        Notes.Clear();
        foreach (var note in _notesService.GetNotes())
            Notes.Add(note);
    }

    [RelayCommand]
    private void CreateNote()
    {
        var note = _notesService.CreateNote();
        SelectedNote = note;
    }

    [RelayCommand]
    private void DeleteNote(NoteItem note)
    {
        _notesService.DeleteNote(note.Id);
        if (SelectedNote?.Id == note.Id)
            SelectedNote = null;
    }

    [RelayCommand]
    private void SelectNote(NoteItem note)
    {
        SelectedNote = note;
    }

    [RelayCommand]
    private void SaveNote()
    {
        if (SelectedNote != null)
            _notesService.UpdateNote(SelectedNote);
    }

    // ─── Volume Mixer ─────────────────────────────────────────

    private DispatcherTimer? _volumeMixerTimer;

    private void InitializeVolumeMixer()
    {
        _volumeMixerService.Initialize();
        // SessionsChanged now arrives from a thread-pool thread (RefreshAsync
        // runs on Task.Run). BeginInvoke instead of Invoke so the audio service
        // doesn't block on the UI thread when a refresh actually has changes.
        _volumeMixerService.SessionsChanged += () =>
            _dispatcher.BeginInvoke((Action)RefreshAudioSessions);

        // Poll every 2s on a background thread. Was 1s on UI thread; the
        // session enumeration touches Process.MainModule (PE-header read) per
        // session, so even with caching the cost is real and not worth doing
        // more often than 2s for a feature few users actively watch.
        _volumeMixerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _volumeMixerTimer.Tick += (_, _) => _ = _volumeMixerService.RefreshAsync();
        _volumeMixerTimer.Start();

        // Initial refresh — also off-thread; the first scan is the most
        // expensive because the name cache is empty.
        _ = _volumeMixerService.RefreshAsync();
    }

    private void RefreshAudioSessions()
    {
        var sessions = _volumeMixerService.GetSessions();
        AudioSessions.Clear();
        foreach (var s in sessions)
            AudioSessions.Add(s);
    }

    [RelayCommand]
    private void ToggleMute(string sessionId)
    {
        _volumeMixerService.ToggleMute(sessionId);
        // User-initiated → kick an immediate background refresh so the UI
        // reflects the new mute state without waiting for the next 2s tick.
        _ = _volumeMixerService.RefreshAsync();
    }

    [RelayCommand]
    private void SetSessionVolume((string Id, float Volume) args)
    {
        _volumeMixerService.SetVolume(args.Id, args.Volume);
        _ = _volumeMixerService.RefreshAsync();
    }

    public void Cleanup()
    {
        _clockTimer.Stop();
        _autoCollapseTimer.Stop();
        _weatherTimer.Stop();
        _clipboardPollTimer?.Stop();
        _volumeMixerTimer?.Stop();

        // Unsubscribe from service events to prevent leaks
        _mediaService.MediaChanged -= OnMediaChanged;
        _mediaService.PositionChanged -= OnMediaPositionChanged;
        _systemMonitorService.StatsUpdated -= OnStatsUpdated;
        _networkMonitorService.StatsUpdated -= OnNetworkStatsUpdated;
        _notificationService.NewNotification -= OnNewNotification;

        _mediaService.Dispose();
        _systemMonitorService.Dispose();
        _networkMonitorService.Dispose();
        _notificationService.Dispose();
        _clipboardService.Dispose();
        _notesService.Dispose();
        _volumeMixerService.Dispose();
        _timerService.Dispose();
        _pomodoroService.Dispose();
        _updateService.Stop();
    }
}
