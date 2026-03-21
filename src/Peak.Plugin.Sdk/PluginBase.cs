using System.Text.Json;
using System.Windows;

namespace Peak.Plugin.Sdk;

/// <summary>
/// Convenience base class for widget plugins. Override only what you need.
/// </summary>
public abstract class PluginBase : IWidgetPlugin
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string Description => "";
    public virtual string Icon => "\U0001F9E9"; // puzzle piece

    public abstract FrameworkElement CreateView(object dataContext);

    public virtual void OnActivate() { }
    public virtual void OnDeactivate() { }
    public virtual void Initialize(IServiceProvider services) { }
    public virtual void LoadSettings(JsonElement? settings) { }
    public virtual JsonElement? SaveSettings() => null;
}
