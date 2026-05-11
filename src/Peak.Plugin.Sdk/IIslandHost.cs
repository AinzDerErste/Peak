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

    /// <summary>
    /// Place a plugin-supplied element in the center of the expanded-state
    /// header (between the clock on the left and the weather on the right).
    /// Pass null to clear it and restore the normal header layout.
    ///
    /// Intended for small accessory widgets — companion eyes, status pills,
    /// activity indicators. The host gives the element a fixed-size cell;
    /// elements that need to scale should use Stretch / Viewbox internally.
    /// </summary>
    void SetExpandedHeaderContent(UIElement? content);

    /// <summary>
    /// Attach a banner element below the island that tracks the island's
    /// current width (collapsed pill width when collapsed, expanded width
    /// when expanded, etc.). Use it for transient status strips — a
    /// download progress bar, "joining call…", a flash message. Pass
    /// <c>null</c> to clear the banner.
    ///
    /// <para><b>Single slot</b>: there's exactly one banner; if a second
    /// plugin calls this while a banner is already showing, it overwrites
    /// the previous one. Plugins that want to be polite should null their
    /// banner out when they're done so the next caller gets a clean slot.</para>
    ///
    /// <para><b>Width binding</b>: the host stretches the banner to the
    /// IslandBorder's <c>ActualWidth</c>, so the strip resizes naturally
    /// as the island animates between Collapsed/Peek/Expanded states.</para>
    /// </summary>
    void SetIslandBanner(UIElement? content);

    /// <summary>
    /// Register a set of plugin-supplied action buttons for the expanded
    /// MediaWidget — they appear next to play/pause/skip and let plugins
    /// hook into "do something with the currently-playing track" without
    /// having to render their own widget. Pass null (or an empty list)
    /// to remove a plugin's previously-registered actions.
    ///
    /// <para>The host renders one icon-button per <see cref="MediaAction"/>
    /// using the standard MediaWidget button style. Buttons from multiple
    /// plugins are concatenated; ordering inside the row is by registration
    /// order (first registered → leftmost extra button).</para>
    ///
    /// <para><paramref name="pluginId"/> namespaces the registration so a
    /// plugin can update its own action set without affecting another
    /// plugin's buttons. Pass your <see cref="IWidgetPlugin.Id"/>.</para>
    /// </summary>
    void SetMediaActions(string pluginId, IReadOnlyList<MediaAction>? actions);
}

/// <summary>
/// A single plugin-contributed button shown next to the MediaWidget's
/// play/pause/skip controls. Designed to be cheap to construct so plugins
/// can re-register on every state change without worrying about churn.
/// </summary>
public class MediaAction
{
    /// <summary>Stable id within the registering plugin. Used by the host
    /// for diff-against-previous when a plugin re-registers. Must be unique
    /// across the actions a single plugin registers.</summary>
    public string Id { get; set; } = "";

    /// <summary>Hover tooltip — keep it short ("Download", "Share", …).</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>
    /// SVG path data for the button glyph (the same string format you'd
    /// drop into a WPF <c>Path.Data</c>). The host renders it inside the
    /// 12×12 area used by the play/skip buttons. Default: a download glyph.
    /// </summary>
    public string IconPathData { get; set; } =
        "M12 3v12m0 0l-4-4m4 4l4-4M5 21h14"; // generic download arrow

    /// <summary>
    /// What runs on click. Always invoked on the UI thread. The action
    /// should return quickly — kick off long work via Task.Run if needed.
    /// </summary>
    public Action OnClick { get; set; } = () => { };
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
