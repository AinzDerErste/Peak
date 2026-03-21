namespace Peak.Core.Plugins;

/// <summary>
/// Central registry for all widgets (built-in and plugins).
/// Uses object instead of FrameworkElement to avoid WPF dependency in Peak.Core.
/// </summary>
public class WidgetRegistry
{
    private readonly Dictionary<string, WidgetRegistration> _widgets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a widget factory.</summary>
    public void Register(string id, string displayName, Func<object, object?> factory, bool isBuiltIn = true)
    {
        _widgets[id] = new WidgetRegistration(id, displayName, factory, isBuiltIn);
    }

    /// <summary>Get all registered widget descriptors.</summary>
    public IReadOnlyList<WidgetDescriptor> GetAll()
    {
        return _widgets.Values
            .Select(r => new WidgetDescriptor(r.Id, r.DisplayName, r.IsBuiltIn))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Create a widget by ID. Returns a FrameworkElement (as object).</summary>
    public object? CreateWidget(string id, object dataContext)
    {
        return _widgets.TryGetValue(id, out var reg) ? reg.Factory(dataContext) : null;
    }

    /// <summary>Check if a widget ID is registered.</summary>
    public bool IsRegistered(string id) => _widgets.ContainsKey(id);

    private record WidgetRegistration(string Id, string DisplayName, Func<object, object?> Factory, bool IsBuiltIn);
}
