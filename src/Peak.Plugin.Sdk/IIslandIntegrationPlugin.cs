namespace Peak.Plugin.Sdk;

/// <summary>
/// Optional interface for plugins that need to integrate with the island window
/// (e.g. Discord plugin that overrides the visualizer bubble and provides a
/// custom collapsed widget).
///
/// Plugins implementing this are called by <c>PluginLoader</c> after
/// <see cref="IWidgetPlugin.Initialize"/> once the main island window is ready.
/// </summary>
public interface IIslandIntegrationPlugin
{
    /// <summary>
    /// Called once after the island window is created. The plugin may
    /// keep a reference to <paramref name="host"/> and use it for the
    /// lifetime of the app.
    /// </summary>
    void AttachToIsland(IIslandHost host);

    /// <summary>
    /// Called when Peak shuts down or when the plugin is disabled. Plugins
    /// should dispose resources and unregister event handlers.
    /// </summary>
    void DetachFromIsland();
}
