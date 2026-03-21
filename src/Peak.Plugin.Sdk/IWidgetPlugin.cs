using System.Text.Json;
using System.Windows;

namespace Peak.Plugin.Sdk;

/// <summary>
/// Interface for Peak widget plugins. Implement this in your plugin assembly.
/// </summary>
public interface IWidgetPlugin
{
    /// <summary>Unique plugin identifier, e.g. "com.example.pomodoro".</summary>
    string Id { get; }

    /// <summary>Display name shown in the widget selector.</summary>
    string Name { get; }

    /// <summary>Short description of the widget.</summary>
    string Description { get; }

    /// <summary>Emoji or icon character for the widget.</summary>
    string Icon { get; }

    /// <summary>Create the widget's visual element.</summary>
    FrameworkElement CreateView(object dataContext);

    /// <summary>Called when the widget is placed in a slot.</summary>
    void OnActivate();

    /// <summary>Called when the widget is removed from a slot.</summary>
    void OnDeactivate();

    /// <summary>Called once during plugin loading with the app's service provider.</summary>
    void Initialize(IServiceProvider services);

    /// <summary>Load plugin-specific settings.</summary>
    void LoadSettings(JsonElement? settings);

    /// <summary>Save plugin-specific settings. Return null if none.</summary>
    JsonElement? SaveSettings();
}
