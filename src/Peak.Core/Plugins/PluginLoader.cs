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
    public IReadOnlyList<LoadedPlugin> LoadAll(
        IServiceProvider services,
        Dictionary<string, JsonElement>? savedSettings = null,
        HashSet<string>? disabledPlugins = null)
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
                    var loaded = LoadAssembly(dll, services, savedSettings, disabledPlugins);
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

    /// <summary>
    /// Scans all plugin directories and returns discovered plugin IDs and names
    /// without fully initializing them. Used by the settings UI to show all
    /// plugins (including disabled ones) with enable/disable toggles.
    /// </summary>
    public IReadOnlyList<(string Id, string Name)> DiscoverAll()
    {
        var result = new List<(string Id, string Name)>();
        if (!Directory.Exists(_pluginsDir)) return result;

        foreach (var pluginDir in Directory.GetDirectories(_pluginsDir))
        {
            try
            {
                var dlls = Directory.GetFiles(pluginDir, "*.dll");
                foreach (var dll in dlls)
                {
                    try
                    {
                        var ctx = new PluginLoadContext(dll);
                        var asm = ctx.LoadFromAssemblyPath(dll);
                        var pluginTypes = asm.GetTypes()
                            .Where(t => !t.IsAbstract && !t.IsInterface &&
                                        t.GetInterfaces().Any(i => i.FullName == "Peak.Plugin.Sdk.IWidgetPlugin"));

                        foreach (var type in pluginTypes)
                        {
                            try
                            {
                                var instance = Activator.CreateInstance(type);
                                if (instance == null) continue;
                                var id = type.GetProperty("Id")?.GetValue(instance) as string ?? type.FullName ?? type.Name;
                                var name = type.GetProperty("Name")?.GetValue(instance) as string ?? type.Name;
                                result.Add((id, name));
                            }
                            catch { }
                        }

                        ctx.Unload();
                    }
                    catch { }
                }
            }
            catch { }
        }

        return result;
    }

    private List<LoadedPlugin> LoadAssembly(string dllPath, IServiceProvider services, Dictionary<string, JsonElement>? savedSettings, HashSet<string>? disabledPlugins)
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

                // Skip disabled plugins BEFORE initializing them
                if (disabledPlugins is { Count: > 0 } && disabledPlugins.Contains(id))
                    continue;

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

    /// <summary>
    /// Calls <c>AttachToIsland(host)</c> on every loaded plugin that implements
    /// <c>Peak.Plugin.Sdk.IIslandIntegrationPlugin</c>. Uses reflection so core
    /// does not need to reference the plugin SDK types directly.
    /// </summary>
    public void AttachIslandIntegrations(object host)
    {
        foreach (var plugin in LoadedPlugins)
        {
            try
            {
                var type = plugin.Instance.GetType();
                var iface = type.GetInterfaces()
                    .FirstOrDefault(i => i.FullName == "Peak.Plugin.Sdk.IIslandIntegrationPlugin");
                if (iface == null) continue;

                var attach = iface.GetMethod("AttachToIsland");
                attach?.Invoke(plugin.Instance, [host]);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AttachToIsland failed for {plugin.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Returns the editable settings schema for every plugin that implements
    /// <c>Peak.Plugin.Sdk.IPluginSettingsProvider</c>.
    /// </summary>
    public IReadOnlyList<PluginSettingsInfo> GetPluginSettingsSchemas()
    {
        var list = new List<PluginSettingsInfo>();
        foreach (var plugin in LoadedPlugins)
        {
            try
            {
                var type = plugin.Instance.GetType();
                var iface = type.GetInterfaces()
                    .FirstOrDefault(i => i.FullName == "Peak.Plugin.Sdk.IPluginSettingsProvider");
                if (iface == null) continue;

                var getSchema = iface.GetMethod("GetSettingsSchema");
                var raw = getSchema?.Invoke(plugin.Instance, null) as System.Collections.IEnumerable;
                if (raw == null) continue;

                var fields = new List<PluginSettingFieldDto>();
                foreach (var f in raw)
                {
                    var ft = f.GetType();
                    string GetStr(string prop) => ft.GetProperty(prop)?.GetValue(f) as string ?? "";
                    string? GetStrN(string prop) => ft.GetProperty(prop)?.GetValue(f) as string;
                    int GetInt(string prop) => Convert.ToInt32(ft.GetProperty(prop)?.GetValue(f) ?? 0);
                    fields.Add(new PluginSettingFieldDto(
                        Key: GetStr("Key"),
                        Label: GetStr("Label"),
                        Description: GetStrN("Description"),
                        Kind: GetInt("Kind"),
                        CurrentValue: GetStrN("CurrentValue"),
                        Placeholder: GetStrN("Placeholder")
                    ));
                }
                list.Add(new PluginSettingsInfo(plugin.Id, plugin.Name, fields));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetSettingsSchema failed for {plugin.Id}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>
    /// Applies a single field value to a plugin's settings provider.
    /// </summary>
    public void SetPluginSetting(string pluginId, string key, string? value)
    {
        var plugin = LoadedPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin == null) return;

        var type = plugin.Instance.GetType();
        var iface = type.GetInterfaces()
            .FirstOrDefault(i => i.FullName == "Peak.Plugin.Sdk.IPluginSettingsProvider");
        var setter = iface?.GetMethod("SetSettingValue");
        setter?.Invoke(plugin.Instance, [key, value]);
    }

    /// <summary>
    /// Calls <c>SaveSettings()</c> on every plugin and returns the combined
    /// dictionary keyed by plugin ID (intended to be assigned to
    /// <c>AppSettings.PluginSettings</c>).
    /// </summary>
    public Dictionary<string, JsonElement> CollectAllSettings()
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var plugin in LoadedPlugins)
        {
            try
            {
                var el = plugin.SaveSettings();
                if (el.HasValue) result[plugin.Id] = el.Value;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CollectAllSettings: {plugin.Id}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Calls <c>DetachFromIsland()</c> on every island-integration plugin.
    /// </summary>
    public void DetachIslandIntegrations()
    {
        foreach (var plugin in LoadedPlugins)
        {
            try
            {
                var type = plugin.Instance.GetType();
                var iface = type.GetInterfaces()
                    .FirstOrDefault(i => i.FullName == "Peak.Plugin.Sdk.IIslandIntegrationPlugin");
                if (iface == null) continue;

                var detach = iface.GetMethod("DetachFromIsland");
                detach?.Invoke(plugin.Instance, []);
            }
            catch { }
        }
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

public record PluginSettingsInfo(
    string PluginId,
    string PluginName,
    IReadOnlyList<PluginSettingFieldDto> Fields
);

public record PluginSettingFieldDto(
    string Key,
    string Label,
    string? Description,
    int Kind,          // 0=Text, 1=Password, 2=Bool, 3=Number (mirrors PluginSettingFieldKind)
    string? CurrentValue,
    string? Placeholder
);

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
