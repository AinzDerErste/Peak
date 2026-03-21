using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Peak.Core.Plugins;

/// <summary>
/// Loads plugin assemblies from the plugins directory.
/// Each plugin lives in its own subdirectory with a .dll file.
/// </summary>
public class PluginLoader : IDisposable
{
    private readonly string _pluginsDir;
    private readonly List<PluginContext> _contexts = new();

    public IReadOnlyList<LoadedPlugin> LoadedPlugins { get; private set; } = [];

    public PluginLoader(string pluginsDir)
    {
        _pluginsDir = pluginsDir;
        Directory.CreateDirectory(_pluginsDir);
    }

    /// <summary>
    /// Scan the plugins directory and load all plugin assemblies.
    /// Returns discovered IWidgetPlugin instances.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> LoadAll(IServiceProvider services, Dictionary<string, JsonElement>? savedSettings = null)
    {
        var plugins = new List<LoadedPlugin>();

        if (!Directory.Exists(_pluginsDir)) return plugins;

        foreach (var pluginDir in Directory.GetDirectories(_pluginsDir))
        {
            try
            {
                var dlls = Directory.GetFiles(pluginDir, "*.dll");
                foreach (var dll in dlls)
                {
                    var loaded = LoadAssembly(dll, services, savedSettings);
                    plugins.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load plugin from {pluginDir}: {ex.Message}");
            }
        }

        LoadedPlugins = plugins.AsReadOnly();
        return LoadedPlugins;
    }

    private List<LoadedPlugin> LoadAssembly(string dllPath, IServiceProvider services, Dictionary<string, JsonElement>? savedSettings)
    {
        var results = new List<LoadedPlugin>();

        var context = new PluginLoadContext(dllPath);
        _contexts.Add(new PluginContext(context, dllPath));

        var assembly = context.LoadFromAssemblyPath(dllPath);

        // Find the IWidgetPlugin interface by name (cross-assembly matching)
        var pluginTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface &&
                        t.GetInterfaces().Any(i => i.FullName == "Peak.Plugin.Sdk.IWidgetPlugin"));

        foreach (var type in pluginTypes)
        {
            try
            {
                var instance = Activator.CreateInstance(type);
                if (instance == null) continue;

                // Use reflection to call methods (cross-assembly interface)
                var idProp = type.GetProperty("Id");
                var nameProp = type.GetProperty("Name");
                var initMethod = type.GetMethod("Initialize");
                var loadSettingsMethod = type.GetMethod("LoadSettings");
                var createViewMethod = type.GetMethod("CreateView");
                var onActivateMethod = type.GetMethod("OnActivate");
                var onDeactivateMethod = type.GetMethod("OnDeactivate");
                var saveSettingsMethod = type.GetMethod("SaveSettings");

                var id = idProp?.GetValue(instance) as string ?? type.FullName ?? type.Name;
                var name = nameProp?.GetValue(instance) as string ?? type.Name;

                // Initialize
                initMethod?.Invoke(instance, [services]);

                // Load settings if available
                if (savedSettings != null && savedSettings.TryGetValue(id, out var settings))
                    loadSettingsMethod?.Invoke(instance, [settings]);
                else
                    loadSettingsMethod?.Invoke(instance, [null]);

                results.Add(new LoadedPlugin(
                    Id: id,
                    Name: name,
                    Instance: instance,
                    CreateView: dc => createViewMethod?.Invoke(instance, [dc]),
                    OnActivate: () => onActivateMethod?.Invoke(instance, []),
                    OnDeactivate: () => onDeactivateMethod?.Invoke(instance, []),
                    SaveSettings: () => saveSettingsMethod?.Invoke(instance, []) as JsonElement?
                ));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to instantiate plugin {type.FullName}: {ex.Message}");
            }
        }

        return results;
    }

    public void Dispose()
    {
        foreach (var ctx in _contexts)
        {
            try { ctx.Context.Unload(); } catch { }
        }
        _contexts.Clear();
    }

    private record PluginContext(AssemblyLoadContext Context, string DllPath);
}

public record LoadedPlugin(
    string Id,
    string Name,
    object Instance,
    Func<object, object?> CreateView,
    Action OnActivate,
    Action OnDeactivate,
    Func<JsonElement?> SaveSettings
);

internal class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
