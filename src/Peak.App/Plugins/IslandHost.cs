using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Peak.App.ViewModels;
using Peak.App.Views;
using Peak.Core.Configuration;
using Peak.Core.Plugins;
using Peak.Plugin.Sdk;

namespace Peak.App.Plugins;

/// <summary>
/// Host implementation exposed to plugins. Routes calls to the active
/// <see cref="IslandWindow"/> and <see cref="IslandViewModel"/>.
/// </summary>
public class IslandHost : IIslandHost
{
    private readonly IslandViewModel _viewModel;
    private readonly SettingsManager _settingsManager;
    private readonly ILogger<IslandHost> _logger;
    private IslandWindow? _window;

    public IslandHost(IslandViewModel viewModel, SettingsManager settingsManager, ILogger<IslandHost> logger)
    {
        _viewModel = viewModel;
        _settingsManager = settingsManager;
        _logger = logger;
    }

    /// <summary>Set by App.xaml.cs after plugin loading.</summary>
    public PluginLoader? PluginLoader { get; set; }

    /// <summary>Called by App.xaml.cs once the island window exists.</summary>
    public void AttachWindow(IslandWindow window) => _window = window;

    public object ViewModel => _viewModel;

    public Dispatcher UiDispatcher => Application.Current.Dispatcher;

    public void SetVisualizerOverride(UIElement? content)
    {
        if (_window == null) return;
        UiDispatcher.Invoke(() => _window.SetVisualizerOverride(content));
    }

    /// <summary>Maps each SDK renderer delegate to its wrapped Core-side delegate for list management.</summary>
    private readonly Dictionary<Func<CollapsedWidgetKind, FrameworkElement?>, Func<CollapsedWidget, FrameworkElement?>>
        _rendererMap = new();

    public void SetCollapsedRenderer(Func<CollapsedWidgetKind, FrameworkElement?>? renderer)
    {
        if (_window == null) return;

        UiDispatcher.Invoke(() =>
        {
            if (renderer == null)
            {
                // Plugin detaching — the renderer's closure will return null naturally
                // since the plugin clears its internal state, so no action needed.
                _window.RenderCollapsedSlots();
                return;
            }

            // If this exact renderer is already registered, skip
            if (_rendererMap.ContainsKey(renderer))
            {
                _window.RenderCollapsedSlots();
                return;
            }

            // Wrap SDK enum → Core enum and register
            Func<CollapsedWidget, FrameworkElement?> wrapped = w =>
            {
                var kind = (CollapsedWidgetKind)(int)w;
                return renderer(kind);
            };
            _rendererMap[renderer] = wrapped;
            _window.ExternalCollapsedRenderers.Add(wrapped);
            _window.RenderCollapsedSlots();
        });
    }

    public void RefreshCollapsedSlots()
    {
        if (_window == null) return;
        UiDispatcher.Invoke(() => _window.RenderCollapsedSlots());
    }

    public void RequestSettingsSave()
    {
        try
        {
            var loader = PluginLoader;
            if (loader != null)
            {
                var all = loader.CollectAllSettings();
                _settingsManager.Settings.PluginSettings = all;
            }
            _settingsManager.Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RequestSettingsSave failed");
        }
    }

    public void SetExpansionBlocked(bool blocked)
    {
        UiDispatcher.Invoke(() => _viewModel.ExpansionBlocked = blocked);
    }

    public void SetCollapsedOverlay(UIElement? overlay)
    {
        if (_window == null) return;
        UiDispatcher.Invoke(() => _window.SetCollapsedOverlay(overlay));
    }

    public void SetExpandedHeaderContent(UIElement? content)
    {
        if (_window == null) return;
        UiDispatcher.Invoke(() => _window.SetExpandedHeaderContent(content));
    }

    public void SetViewModelProperty(string propertyName, object? value)
    {
        UiDispatcher.Invoke(() =>
        {
            var prop = typeof(IslandViewModel).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite) return;

            try
            {
                var converted = value != null && !prop.PropertyType.IsInstanceOfType(value)
                    ? Convert.ChangeType(value, prop.PropertyType)
                    : value;
                prop.SetValue(_viewModel, converted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SetViewModelProperty({PropertyName}) failed", propertyName);
            }
        });
    }
}
