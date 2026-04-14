using System.Windows;
using System.Windows.Threading;

namespace Peak.Plugin.Sdk;

/// <summary>
/// Facade exposed to plugins that want to integrate with Peak's island window
/// (collapsed widgets, visualizer bubble, shared ViewModel).
/// </summary>
public interface IIslandHost
{
    /// <summary>
    /// Replace the visualizer bubble contents with a custom element.
    /// Pass null to restore the default audio visualizer.
    /// </summary>
    void SetVisualizerOverride(UIElement? content);

    /// <summary>
    /// Register a renderer that can provide custom UI for collapsed widget slots.
    /// Return null from the callback to let the default renderer handle the slot.
    /// Pass null here to clear a previously registered renderer.
    /// </summary>
    void SetCollapsedRenderer(Func<CollapsedWidgetKind, FrameworkElement?>? renderer);

    /// <summary>
    /// The IslandViewModel instance. Plugins may cast to the concrete type via reflection
    /// or set properties by name (see <see cref="SetViewModelProperty"/>).
    /// </summary>
    object ViewModel { get; }

    /// <summary>
    /// Convenience: set a property on the IslandViewModel by name.
    /// Marshals to the UI thread automatically.
    /// </summary>
    void SetViewModelProperty(string propertyName, object? value);

    /// <summary>Dispatcher for the UI thread.</summary>
    Dispatcher UiDispatcher { get; }

    /// <summary>
    /// Forces the island to re-evaluate and re-render its collapsed slots.
    /// Call this when plugin state changes affect whether a collapsed widget should be visible.
    /// </summary>
    void RefreshCollapsedSlots();

    /// <summary>
    /// Ask Peak to collect settings from all plugins (including the caller) and
    /// persist them to disk. Use this when a plugin mutates its own settings
    /// outside of the Settings UI (e.g. an OAuth token refresh).
    /// </summary>
    void RequestSettingsSave();

    /// <summary>
    /// Block or unblock mouse-triggered expansion of the island.
    /// Use during plugin overlays (e.g. incoming call) that should prevent
    /// the island from expanding when clicked.
    /// </summary>
    void SetExpansionBlocked(bool blocked);

    /// <summary>
    /// Show a full-width overlay on top of the collapsed island slots.
    /// Pass null to remove the overlay and restore normal collapsed content.
    /// </summary>
    void SetCollapsedOverlay(UIElement? overlay);
}

/// <summary>
/// Mirror of Peak.Core's CollapsedWidget enum for plugin consumption.
/// Values MUST match the core enum numerically.
/// </summary>
public enum CollapsedWidgetKind
{
    None = 0,
    Clock = 1,
    Weather = 2,
    Temperature = 3,
    WeatherIcon = 4,
    Date = 5,
    MediaTitle = 6,
    DiscordCallCount = 7,
    TeamSpeakCallCount = 8,
    VoiceCallCount = 9
}
