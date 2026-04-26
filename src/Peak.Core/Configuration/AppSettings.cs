using System.Text.Json;

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
    QuickAccess,
    Clipboard,
    QuickNotes,
    VolumeMixer,
    Pomodoro
}

public enum RowMode
{
    TwoSlots,
    Wide
}

public enum NetworkGraphStyle
{
    Line,
    Bars
}

public enum CollapsedWidget
{
    None,
    Clock,
    Weather,       // Icon + Temperature combined
    Temperature,   // Temperature only
    WeatherIcon,   // Icon only
    Date,
    MediaTitle,
    DiscordCallCount,
    TeamSpeakCallCount,
    VoiceCallCount // Combined: shows whichever voice app is active (Discord / TeamSpeak / both)
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

    // Appearance
    public bool ShowBorder { get; set; } = true;
    public string IslandBackground { get; set; } = "#FF000000";
    public string AccentColor { get; set; } = "#FF60CDFF";
    public string ThemePreset { get; set; } = "default";

    // Auto-hide: slide island behind top edge after idle
    public bool AutoHideEnabled { get; set; } = false;
    public int AutoHideSeconds { get; set; } = 10;

    public string WeatherCity { get; set; } = string.Empty;
    public string WeatherPostalCode { get; set; } = string.Empty; // e.g. "10115" — auto-resolves to lat/lon
    public string WeatherCountryCode { get; set; } = "DE"; // ISO 3166-1 alpha-2
    public double WeatherLat { get; set; } = 52.52; // Berlin default
    public double WeatherLon { get; set; } = 13.405;
    public bool LaunchAtStartup { get; set; }

    // Notifications: apps ever seen (for settings UI) and apps muted by the user
    public List<string> SeenNotificationApps { get; set; } = new();
    public HashSet<string> MutedNotificationApps { get; set; } = new();

    // Global toggle hotkey (defaults to Ctrl+Shift+N)
    // Modifiers are Win32 MOD_* flags: ALT=1, CTRL=2, SHIFT=4, WIN=8
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0004; // CTRL + SHIFT
    public uint HotkeyVirtualKey { get; set; } = 0x4E;            // N
    public string HotkeyDisplay { get; set; } = "Ctrl+Shift+N";

    // Collapsed-state slots: Left, Center, Right
    public CollapsedWidget[] CollapsedSlots { get; set; } =
        [CollapsedWidget.None, CollapsedWidget.Clock, CollapsedWidget.Weather];

    // Network widget graph style
    public NetworkGraphStyle NetworkGraphStyle { get; set; } = NetworkGraphStyle.Line;

    // Audio visualizer
    public string AudioDeviceId { get; set; } = "";
    public double VisualizerSensitivity { get; set; } = 50; // 0-100, maps to amplification factor

    // Plugin settings (key = plugin ID)
    public Dictionary<string, JsonElement> PluginSettings { get; set; } = new();

    // Plugins the user has explicitly disabled (by plugin ID).
    // Default = empty → all discovered plugins are loaded.
    public HashSet<string> DisabledPlugins { get; set; } = new();

}
