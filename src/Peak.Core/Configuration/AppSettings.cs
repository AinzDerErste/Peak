namespace Peak.Core.Configuration;

public enum IslandBehavior
{
    AlwaysVisible,
    EventOnly
}

public enum WidgetType
{
    None,
    Clock,
    Weather,
    Media,
    SystemMonitor,
    Calendar,
    Timer,
    Network,
    QuickAccess
}

public enum RowMode
{
    TwoSlots,
    Wide
}

public enum CollapsedWidget
{
    None,
    Clock,
    Weather,       // Icon + Temperature combined
    Temperature,   // Temperature only
    WeatherIcon,   // Icon only
    Date,
    MediaTitle
}

public class AppSettings
{
    public IslandBehavior Behavior { get; set; } = IslandBehavior.AlwaysVisible;
    public int AutoCollapseSeconds { get; set; } = 5;
    public int MonitorIndex { get; set; } = 0;

    // Grid-Slot Layout: 6 Slots (2 columns × 3 rows)
    // Index: 0=TopLeft, 1=TopRight, 2=MiddleLeft, 3=MiddleRight, 4=BottomLeft, 5=BottomRight
    public WidgetType[] WidgetSlots { get; set; } =
    [
        WidgetType.Clock, WidgetType.Weather,
        WidgetType.Media, WidgetType.SystemMonitor,
        WidgetType.Calendar, WidgetType.Timer
    ];

    // Row modes: each row can be TwoSlots (2 normal) or Wide (1 full-width)
    public RowMode[] RowModes { get; set; } = [RowMode.TwoSlots, RowMode.TwoSlots, RowMode.TwoSlots];

    // Quick Access: pinned file/folder paths
    public string[] QuickAccessPaths { get; set; } = [];

    // Legacy toggles (kept for backward compat)
    public bool ShowClock { get; set; } = true;
    public bool ShowMedia { get; set; } = true;
    public bool ShowSystemMonitor { get; set; } = true;
    public bool ShowWeather { get; set; } = true;
    public bool ShowCalendar { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool ShowTimer { get; set; } = true;

    // Appearance
    public bool ShowBorder { get; set; } = true;
    public string IslandBackground { get; set; } = "#FF000000";
    public string AccentColor { get; set; } = "#FF60CDFF";

    // Auto-hide: slide island behind top edge after idle
    public bool AutoHideEnabled { get; set; } = false;
    public int AutoHideSeconds { get; set; } = 10;

    public string WeatherCity { get; set; } = string.Empty;
    public string WeatherPostalCode { get; set; } = string.Empty; // e.g. "10115" — auto-resolves to lat/lon
    public string WeatherCountryCode { get; set; } = "DE"; // ISO 3166-1 alpha-2
    public double WeatherLat { get; set; } = 52.52; // Berlin default
    public double WeatherLon { get; set; } = 13.405;
    public bool LaunchAtStartup { get; set; }

    // Collapsed-state slots: Left, Center, Right
    public CollapsedWidget[] CollapsedSlots { get; set; } =
        [CollapsedWidget.None, CollapsedWidget.Clock, CollapsedWidget.Weather];

    // Audio visualizer
    public string AudioDeviceId { get; set; } = "";
    public double VisualizerSensitivity { get; set; } = 50; // 0-100, maps to amplification factor
}
