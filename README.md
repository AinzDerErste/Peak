# Peak — Dynamic Island for Windows

![Peak](Peak.png)

A macOS/iOS-style Dynamic Island for Windows, built as a hobby project.

## Features

- **Dynamic Island** — collapsible notch at the top of your screen with Collapsed, Peek, and Expanded states
- **Widgets** — Clock, Weather, Media Controls, System Monitor, Network Stats, Calendar, Timer, Quick Access, Clipboard, Quick Notes, Volume Mixer, Pomodoro
- **Configurable Collapsed State** — 3 slots (Left, Center, Right) with selectable widgets
- **Audio Visualizer** — real-time music visualization as a separate circle next to the notch
- **Windows Notifications** — toast notifications peek through the island
- **Plugin System** — drop a plugin DLL into `%AppData%\Peak\plugins\<name>\` to extend the island with new widgets and integrations
  - **Discord** — shows voice-channel participants and highlights the active speaker
  - **TeamSpeak** — shows TeamSpeak voice-channel participants and the active speaker
- **Auto-Update** — checks GitHub Releases for new versions
- **Installer** — Inno Setup based installer with autostart support

## Tech Stack

- **WPF** / **.NET 8** / **C#**
- WASAPI (NAudio) for audio capture
- Windows Runtime APIs for media & notifications
- `Microsoft.Extensions.Hosting` for dependency injection and structured logging

## Plugin Development

Peak loads plugins as separate .NET assemblies dropped into `%AppData%\Peak\plugins\<name>\`. Each plugin runs in its own `AssemblyLoadContext` (ALC) so its dependencies don't bleed into the host. The shipped **Discord** and **TeamSpeak** plugins are the canonical examples — both live in `src/Peak.Plugins.*` and are excellent reading material.

### Architecture in one paragraph

The host (`Peak.App`) starts up, builds a DI container, then asks `PluginLoader` to scan the plugins directory. Each found `.dll` is loaded into its own `AssemblyLoadContext` and reflected over to find types implementing `IWidgetPlugin`. The host calls into your plugin **only via the SDK interfaces** (`Peak.Plugin.Sdk`) — there's no direct reference to your assembly's concrete types. Your plugin calls back into the host **only via `IIslandHost`** — there's no direct access to host internals. This separation lets the host evolve without breaking plugins (and vice versa).

### 1. Project setup

Create a .NET 8 class-library project with WPF enabled. Note: even if your plugin is non-visual, `<UseWPF>true</UseWPF>` is required because the SDK references `FrameworkElement`.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <AssemblyName>MyCompany.Peak.Plugins.Hello</AssemblyName>
    <RootNamespace>MyCompany.Peak.Plugins.Hello</RootNamespace>

    <!-- Auto-deploy after every build (Windows-only path). -->
    <PluginOutputDir>$(AppData)\Peak\plugins\hello\</PluginOutputDir>
  </PropertyGroup>

  <ItemGroup>
    <!-- IMPORTANT: don't ship a second copy of Peak.Plugin.Sdk.dll —
         the host already loaded it and we want type identity preserved. -->
    <ProjectReference Include="..\..\Peak\src\Peak.Plugin.Sdk\Peak.Plugin.Sdk.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>

    <!-- Same trick for any package the host already references.
         Microsoft.Extensions.Logging.Abstractions is the most common one. -->
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>

  <Target Name="CopyToPluginsDir" AfterTargets="Build">
    <MakeDir Directories="$(PluginOutputDir)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(PluginOutputDir)" SkipUnchangedFiles="true" />
  </Target>
</Project>
```

**Why the `<Private>false</Private>` + `<ExcludeAssets>runtime</ExcludeAssets>` dance?** Each ALC has its own type space. If your plugin ships its own `Peak.Plugin.Sdk.dll` next to its DLL, the loader will resolve `IWidgetPlugin` from *that* file — a different `Type` identity from the host's `IWidgetPlugin`. The host's reflection-based check `if (type.GetInterfaces().Any(i => i.FullName == "Peak.Plugin.Sdk.IWidgetPlugin"))` will still find it (we match by name), but the cast `(IWidgetPlugin)Activator.CreateInstance(type)` will fail. The two attributes together stop MSBuild from copying the DLL into your output, so the loader resolves the SDK from the host instead. **Same applies to every NuGet package the host already loads.**

### 2. Minimum viable plugin

The smallest working `IWidgetPlugin`. Drop the resulting DLL into `%AppData%\Peak\plugins\hello\`, restart Peak, and pick "Hello" in any widget slot.

```csharp
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Peak.Plugin.Sdk;

namespace MyCompany.Peak.Plugins.Hello;

public class HelloPlugin : IWidgetPlugin
{
    // Stable identifier. Use reverse-DNS to avoid collisions.
    public string Id => "com.mycompany.hello";

    // Shown in the widget picker.
    public string Name => "Hello";
    public string Description => "A minimal demo widget.";
    public string Icon => "👋";

    public void Initialize(IServiceProvider services) { }

    public void LoadSettings(JsonElement? settings) { }
    public JsonElement? SaveSettings() => null;

    public FrameworkElement CreateView(object dataContext) =>
        new TextBlock
        {
            Text = "Hello, Peak!",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 13
        };

    public void OnActivate() { }
    public void OnDeactivate() { }
}
```

### 3. Lifecycle

The host invokes plugin hooks in this order:

| # | Hook | When | What it's for |
|---|------|------|---------------|
| 1 | `Initialize(services)` | Once, at app start, after DI is built | Cache the `ILogger`, look up host services, kick off background work that doesn't need the island window yet. |
| 2 | `LoadSettings(json)` | Once, after `Initialize`, only if persisted settings exist | Deserialize what you returned from a previous `SaveSettings`. |
| 3 | `AttachToIsland(host)` | Once, only if you implement `IIslandIntegrationPlugin`, after the island window exists | Stash the `host` reference, register collapsed renderers, push initial values into the ViewModel. |
| 4 | `OnActivate()` | Every time your widget is placed in a slot | Start polling, subscribe to events. May fire multiple times. |
| 5 | `OnDeactivate()` | Every time your widget is removed from a slot | Mirror of `OnActivate` — stop polling, unsubscribe. May fire multiple times. |
| 6 | `SaveSettings()` | When the user clicks Save in Settings, or when you call `host.RequestSettingsSave()` | Serialize current state to a `JsonElement`. Return `null` to leave settings untouched. |
| 7 | `DetachFromIsland()` | Once, only if you implement `IIslandIntegrationPlugin`, at shutdown or when the user disables you | Mirror of `AttachToIsland` — dispose resources, cancel background tasks, unregister renderers. |

`CreateView(dataContext)` is called **lazily**, every time your widget is rendered into a slot — could be 0, 1, or N times depending on user behaviour. Don't put one-time setup there.

### 4. The four interfaces

#### `IWidgetPlugin` (required)

Provides metadata + the WPF view that renders inside the island grid. See the minimum-viable example above. Three things to keep in mind:

- **`Id` is the lookup key everywhere** — settings, "disabled plugins" list, runtime registry. Pick something stable; changing it later orphans existing user settings.
- **`CreateView` runs on the UI thread** but is called multiple times. Build a fresh element each call; don't cache and re-attach the same instance.
- **`Initialize(services)` gets the host's `IServiceProvider`** — pull anything you need (ILogger, HttpClient, …) once and stash it on a field.

```csharp
private ILogger<HelloPlugin>? _logger;

public void Initialize(IServiceProvider services)
{
    // GetService is nullable-friendly; Peak provides ILogger<T> via the
    // standard Hosting bootstrap, so this is safe even on minimal hosts.
    _logger = services.GetService<ILogger<HelloPlugin>>();
}
```

#### `IIslandIntegrationPlugin` (optional)

Implement this when you want to push state directly to the island — collapsed slots, the visualizer bubble, the shared ViewModel.

```csharp
public class HelloPlugin : IWidgetPlugin, IIslandIntegrationPlugin
{
    private IIslandHost? _host;

    public void AttachToIsland(IIslandHost host)
    {
        _host = host;

        // Register a collapsed-slot renderer. Return null from the callback
        // for any kind your plugin doesn't handle.
        host.SetCollapsedRenderer(kind =>
            kind == CollapsedWidgetKind.MediaTitle
                ? new TextBlock { Text = "👋 Hi", Foreground = Brushes.White }
                : null);
    }

    public void DetachFromIsland()
    {
        _host?.SetCollapsedRenderer(null);   // un-register
        _host?.SetVisualizerOverride(null);  // restore default visualizer
        _host = null;
    }
}
```

#### `IPluginSettingsProvider` (optional)

Adds simple settings fields (Text / Password / Bool / Number / Button) to the standard Peak Settings window — no custom XAML. Each field is rendered as a labelled row, value bound to your plugin via `SetSettingValue`.

```csharp
public class HelloPlugin : IWidgetPlugin, IPluginSettingsProvider
{
    private string _greeting = "Hello, Peak!";

    public IReadOnlyList<PluginSettingField> GetSettingsSchema() => new[]
    {
        new PluginSettingField
        {
            Key = "Greeting",
            Label = "Greeting text",
            Description = "Shown in the widget. Default: Hello, Peak!",
            Kind = PluginSettingFieldKind.Text,
            CurrentValue = _greeting,
            Placeholder = "Hello, Peak!"
        },
        new PluginSettingField
        {
            Key = "Reset",
            Label = "Reset to default",
            Kind = PluginSettingFieldKind.Button
        }
    };

    public void SetSettingValue(string key, string? value)
    {
        switch (key)
        {
            case "Greeting":
                _greeting = string.IsNullOrWhiteSpace(value) ? "Hello, Peak!" : value!;
                break;
            case "Reset":
                _greeting = "Hello, Peak!";
                break;
        }
    }
}
```

For richer settings (lists, multi-step pickers, image selection), expose a `Button` field that opens a custom `Window` you build in code — see the **TeamSpeak AFK picker** (`src/Peak.Plugins.TeamSpeak/AfkChannelsDialog.xaml.cs`) for a worked example that side-steps the BAML-loading issue described in the Caveats.

#### `IIslandHost` (the API back to Peak)

The interface your plugin holds onto from `AttachToIsland`. Full reference:

| Method | Purpose | Common usage |
|--------|---------|--------------|
| `SetVisualizerOverride(UIElement?)` | Replace the audio-visualizer circle right of the pill with your own UI. `null` restores the default music visualizer. | Show a Discord speaker avatar; show a TS headset glyph when in a voice channel. |
| `SetCollapsedRenderer(Func<CollapsedWidgetKind, FrameworkElement?>?)` | Register a callback that can supply a custom WPF element for any `CollapsedWidgetKind` value. Return `null` from the callback to fall back to Peak's default renderer for that kind. | Discord shows a phone icon + call count for `DiscordCallCount`; the same plugin's renderer returns `null` for everything else. |
| `RefreshCollapsedSlots()` | Force the collapsed pill to re-render. Call after your plugin's state has changed in a way that affects what `SetCollapsedRenderer` returns. | After joining/leaving a Discord call. |
| `SetViewModelProperty(string, object?)` | Set a property on the shared `IslandViewModel` by name. Marshals to the UI thread automatically. | `host.SetViewModelProperty("DiscordCallCount", 3);` |
| `ViewModel` | Raw ref to the IslandViewModel (typed as `object`). Cast via reflection if you need it. | Rarely needed — prefer `SetViewModelProperty`. |
| `UiDispatcher` | The WPF dispatcher. Use it from background threads. | `host.UiDispatcher.Invoke(() => myImage.Source = bitmap);` |
| `RequestSettingsSave()` | Ask Peak to collect all plugin settings and write them to disk. | After an OAuth token refresh, after a custom dialog mutates state. |
| `SetExpansionBlocked(bool)` | Prevent / allow the user from expanding the island via hover/click. | While showing a full-pill overlay. |
| `SetCollapsedOverlay(UIElement?)` | Stretch a custom element across the full collapsed pill, hiding the slot widgets. `null` removes the overlay. | Incoming call notification, "Press X to confirm" prompts. |

### 5. Settings persistence

Peak stores all plugin settings as a single JSON dictionary inside `%AppData%\Peak\settings.json`:

```json
{
  "PluginSettings": {
    "com.mycompany.hello": { "greeting": "Hi there!" },
    "peak.plugins.discord": { "ClientId": "...", "AccessToken": "..." }
  }
}
```

Your plugin sees only its own slot. The host calls `LoadSettings` with the value at your plugin's key (or `null` if you've never persisted before), and writes back whatever `SaveSettings` returns.

A typical implementation:

```csharp
private MyPluginSettings _settings = new();

public void LoadSettings(JsonElement? json)
{
    if (json is not { ValueKind: JsonValueKind.Object }) return;
    try { _settings = JsonSerializer.Deserialize<MyPluginSettings>(json.Value.GetRawText()) ?? new(); }
    catch { _settings = new(); }  // corrupt JSON → start fresh
}

public JsonElement? SaveSettings() => JsonSerializer.SerializeToElement(_settings);

public class MyPluginSettings
{
    public string Greeting { get; set; } = "Hello, Peak!";
    public int RefreshSeconds { get; set; } = 30;
}
```

When you mutate settings **outside** the Settings UI (background OAuth refresh, in-plugin dialog, …), call `host.RequestSettingsSave()` so the changes hit disk.

### 6. Logging

Peak uses `Microsoft.Extensions.Logging`. Pull an `ILogger<TYourPlugin>` from the service provider in `Initialize` and call it normally. Log entries appear in Peak's host log and in any sinks the host has configured.

```csharp
private ILogger<HelloPlugin>? _logger;

public void Initialize(IServiceProvider services)
{
    _logger = services.GetService<ILogger<HelloPlugin>>();
}

private void DoSomething()
{
    try { /* … */ }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "Hello plugin: doing something failed");
    }
}
```

For ad-hoc debugging while developing the plugin, write your own append-only log file:

```csharp
private static readonly string LogPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Peak", "plugins", "hello", "plugin.log");

private static void Trace(string line)
{
    try
    {
        File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
    }
    catch { /* disk full / locked — ignore */ }
}
```

This is exactly what the Discord and TeamSpeak plugins do — see `DiscordLog.cs` and `TeamSpeakLog.cs`.

### 7. Common patterns / recipes

**Update a counter on the island in real time.** Push values into the shared ViewModel by name; Peak does the dispatch.

```csharp
host.SetViewModelProperty("DiscordCallCount", currentCount);
host.SetViewModelProperty("DiscordCallCountDisplay", currentCount > 0 ? $"{currentCount}" : "—");
```

**Replace the visualizer bubble with your own UI.**

```csharp
host.UiDispatcher.Invoke(() =>
{
    var avatar = new Border
    {
        Width = 22, Height = 22,
        CornerRadius = new CornerRadius(11),
        Background = new ImageBrush(myBitmap) { Stretch = Stretch.UniformToFill }
    };
    host.SetVisualizerOverride(avatar);
});
```

**Take over the collapsed pill (e.g. an incoming-call banner).**

```csharp
host.SetExpansionBlocked(true);                          // disable click-to-expand
host.SetCollapsedOverlay(BuildIncomingCallUi(callerName)); // covers the whole pill

// On dismiss:
host.SetExpansionBlocked(false);
host.SetCollapsedOverlay(null);
```

**Background polling that respects shutdown.**

```csharp
private CancellationTokenSource? _cts;

public void AttachToIsland(IIslandHost host)
{
    _cts = new CancellationTokenSource();
    _ = Task.Run(() => PollLoop(_cts.Token));
}

public void DetachFromIsland()
{
    try { _cts?.Cancel(); } catch { }
    _cts?.Dispose();
    _cts = null;
}

private async Task PollLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // … do work …
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return; }
    }
}
```

### 8. Debugging

- **Plugin not appearing in the picker?** Check `%AppData%\Peak\plugins\<your-folder>\` actually contains your DLL. Restart Peak (it doesn't hot-reload). Look at Peak's host log for an `IWidgetPlugin`-related entry — usually a missing public constructor or a type-identity mismatch.
- **Reflection-based load failures** (the host can't find your plugin) almost always mean you accidentally shipped your own copy of `Peak.Plugin.Sdk.dll`. Check your output folder — only your plugin's DLL should be there (no `Peak.Plugin.Sdk.dll`, no `Microsoft.Extensions.*` DLLs).
- **Crashes inside your plugin** are caught and logged to Peak's host log but won't crash the app. Add a `try`/`catch` + your own log file (see §6) for diagnostic detail.
- **Iterating during development:** point `<PluginOutputDir>` at the live `%AppData%\Peak\plugins\<name>\` directory. Build → Peak picks it up on next restart. (Hot-reload isn't supported because plugins live in their own ALC.)

### 9. Pitfalls / FAQ

- **No XAML-based plugin Windows.** WPF's `InitializeComponent` resolves BAML resources via pack-URIs scoped to the host's assembly. That lookup fails for plugin-side BAML loaded into a different ALC, and you'll get a runtime exception when your dialog opens. **Build any custom Window / UserControl in code** — see `Peak.Plugins.TeamSpeak/AfkChannelsDialog.xaml.cs` for a worked example that builds a 200-line dialog programmatically.
- **Don't reach for `Application.Current.Dispatcher`.** Use `host.UiDispatcher` instead — it's the explicit contract and survives any future host refactors.
- **Plugins can't reference `Peak.Core` or `Peak.App`.** Anything you need from the host travels through `IIslandHost`. If you find yourself wanting access to an internal type, that's a signal the SDK should grow a method — open an issue.
- **State changes during transitions.** Peak may call `OnActivate` / `OnDeactivate` rapidly while the user reshuffles slots. Don't assume `OnActivate` ⇒ `OnDeactivate` happen in pairs without other lifecycle events between them — keep activation logic idempotent.
- **The host doesn't sandbox plugins.** Plugin code runs with full host privileges (file system, network, COM). Vet anything you load.

## Theme Development

Peak ships with seven built-in themes (`default`, `midnight`, `forest`, `sunset`, `ocean`, `rose`, `snow`). **User themes are pure JSON files** dropped into `%AppData%\Peak\themes\` — no compiler, no Visual Studio, no DLLs. Restart Peak (or hit Reload in Settings) and your theme appears in the picker.

### What a theme controls

Two colours, used everywhere on the island:

| Colour | Where you'll see it |
|--------|---------------------|
| **`background`** | The pill itself — the rounded dark surface behind every widget. Setting it to a low alpha (`#80…`) makes the island translucent. |
| **`accent`** | Progress bars (media + system monitor), the active widget slot's selection ring, the Save button in Settings, and any "primary action" highlight in plugins that respect the theme. |

That's it for now — Peak is intentionally minimal. Future versions may add more semantic slots (text dim, divider, danger), and the JSON format will gain optional fields without breaking older theme files.

### Format reference

Create `%AppData%\Peak\themes\my-theme.json`:

```json
{
  "id": "my-theme",
  "name": "My Theme",
  "background": "#FF101820",
  "accent": "#FFFFB454"
}
```

| Field | Required | Default | Description |
|-------|:--------:|---------|-------------|
| `id` | no | filename without `.json` | Stable lookup key written to `settings.json` when the user picks the theme. |
| `name` | no | same as `id` | Display label shown next to the swatch in Settings. |
| `background` | **yes** | — | Pill background colour. |
| `accent` | **yes** | — | Accent / highlight colour. |

JSON property names are matched case-insensitively, so `Background`, `background`, and `BACKGROUND` all work.

### Colour format

Peak parses colours via WPF's `ColorConverter`, which accepts:

| Form | Example | Notes |
|------|---------|-------|
| `#AARRGGBB` | `#FF101820` | **Recommended.** Explicit alpha first, then RGB. |
| `#RRGGBB` | `#101820` | Implicit alpha = `FF` (fully opaque). |
| `#RGB` | `#102` | Shorthand. Each digit is duplicated (`#102` ≡ `#110022`). |
| `#ARGB` | `#8102` | Shorthand with alpha. |
| Named colour | `"Black"`, `"Coral"` | The full WPF named-colour list. Useful for prototyping; less precise than hex. |

Alpha is always **first** in the AARRGGBB form. `#80FF0000` is *50 % opaque red*, not "fully-opaque red with extra `80` ignored". Mistakes here are the most common cause of "my theme doesn't show up" — Peak parses successfully but the pill is invisible.

### Step-by-step: your first theme

1. Open Peak → **Settings** → scroll to **THEME PRESETS**.
2. Click **Open themes folder** — Explorer launches at `%AppData%\Peak\themes\`.
3. Create `cyberpunk.json`:
   ```json
   {
     "id": "cyberpunk",
     "name": "Cyberpunk",
     "background": "#FF0F0F1E",
     "accent": "#FFFF00C8"
   }
   ```
4. Save the file.
5. Back in Peak's Settings, click **Reload** (no app restart needed).
6. A new swatch appears at the end of the row — black-ish background with a bright magenta accent dot, ringed in magenta to mark it as a user theme.
7. Click the swatch to apply, click **Save** to persist.

### Ready-to-copy themes

Drop any of these into `%AppData%\Peak\themes\`:

**`amber.json`** — warm low-light theme
```json
{ "id": "amber", "name": "Amber", "background": "#FF1A1208", "accent": "#FFFFB000" }
```

**`mint.json`** — fresh daytime palette
```json
{ "id": "mint", "name": "Mint", "background": "#FF0E1A14", "accent": "#FF00E0A4" }
```

**`grayscale.json`** — pure monochrome
```json
{ "id": "grayscale", "name": "Grayscale", "background": "#FF0E0E0E", "accent": "#FFE0E0E0" }
```

**`translucent.json`** — semi-transparent dark glass
```json
{ "id": "translucent", "name": "Glass", "background": "#CC000000", "accent": "#FF7DD3FC" }
```

**`light.json`** — light background (use carefully — most text in Peak assumes dark)
```json
{ "id": "light", "name": "Light", "background": "#FFEEEEEE", "accent": "#FF1E40AF" }
```

### Loading & switching

- **Reload in Settings** picks up newly-added JSON files without restarting Peak. The active theme stays selected through a reload as long as its `id` still exists.
- **Restart** is also fine — `ThemeService.Refresh()` runs at startup.
- A user theme whose `id` matches a built-in (`default`, `midnight`, etc.) is **silently ignored**. Built-ins are protected so the dropdown always has the defaults available.
- Custom themes are visually distinguished from built-ins by a thin **outer ring in the accent colour** — built-ins use a plain white ring.

### Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| Theme file doesn't appear in Settings after Reload | Filename doesn't end in `.json`, or the file isn't directly in `%AppData%\Peak\themes\` (subfolders are not scanned). |
| Theme appears but the pill becomes invisible / very dim | `background` has low alpha (e.g. `#0F101820`). Use `#FF…` for fully opaque. |
| JSON looks valid but Peak silently skips the file | Trailing commas, unescaped backslashes, or BOM-encoded UTF-8 with extra noise. Re-save as plain UTF-8. Peak logs the parse error via `ILogger` — look in the host log if you have logging configured. |
| `id` collision warning in the log | Your `id` matches a built-in. Rename it (e.g. `my-default` instead of `default`). |
| Picked the theme but Save didn't persist | Did you click **Save** in Settings, or just click the swatch? Clicking the swatch only previews — Save writes to disk. |

### Inspiration

Browse `src/Peak.Core/Configuration/ThemePresets.cs` for the seven built-ins. Copy any of them into a `.json` file as a starting point, tweak the hex values, drop into `themes\`, hit Reload.

## AI Disclaimer

This is a personal hobby project. AI (Claude) was used to assist with implementation since WPF can be verbose and ceremonious.

## License

This project is not licensed for redistribution. All rights reserved.
