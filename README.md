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

Peak loads plugins as separate .NET assemblies from `%AppData%\Peak\plugins\<name>\`. Each plugin runs in its own `AssemblyLoadContext`, so dependencies don't bleed into the host. The `Discord` and `TeamSpeak` plugins shipped in this repo are the canonical examples.

### 1. Project setup

Create a new .NET 8 class-library project with WPF enabled:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <AssemblyName>MyCompany.Peak.Plugins.Hello</AssemblyName>
    <RootNamespace>MyCompany.Peak.Plugins.Hello</RootNamespace>
    <!-- Auto-deploy after build -->
    <PluginOutputDir>$(AppData)\Peak\plugins\hello\</PluginOutputDir>
  </PropertyGroup>

  <ItemGroup>
    <!-- The host already loads Peak.Plugin.Sdk; never ship a second copy. -->
    <ProjectReference Include="..\..\Peak\src\Peak.Plugin.Sdk\Peak.Plugin.Sdk.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

  <Target Name="CopyToPluginsDir" AfterTargets="Build">
    <MakeDir Directories="$(PluginOutputDir)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(PluginOutputDir)" SkipUnchangedFiles="true" />
  </Target>
</Project>
```

The `<Private>false</Private>` + `<ExcludeAssets>runtime</ExcludeAssets>` combo is critical — without it your plugin will ship its own copy of `Peak.Plugin.Sdk.dll` and the host's `IWidgetPlugin` type will differ from your plugin's `IWidgetPlugin` type (different `AssemblyLoadContext` ⇒ different `Type` identity ⇒ Peak silently won't recognise it).

If you depend on packages the host already references (e.g. `Microsoft.Extensions.Logging.Abstractions`), use the same trick:

```xml
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2">
  <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
```

### 2. Minimum viable plugin

```csharp
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Peak.Plugin.Sdk;

namespace MyCompany.Peak.Plugins.Hello;

public class HelloPlugin : IWidgetPlugin
{
    public string Id => "com.mycompany.hello";
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
            HorizontalAlignment = HorizontalAlignment.Center
        };

    public void OnActivate() { }
    public void OnDeactivate() { }
}
```

Build it, drop the resulting DLL into `%AppData%\Peak\plugins\hello\`, restart Peak — your plugin shows up in the widget picker.

### 3. The interfaces

| Interface | When to implement |
|-----------|-------------------|
| `IWidgetPlugin` | **Required.** Defines metadata + the WPF view rendered inside an island slot. |
| `IIslandIntegrationPlugin` | When you want to push state into the island itself — visualizer bubble overrides, collapsed-slot renderers, or property updates on the shared ViewModel. |
| `IPluginSettingsProvider` | When you want fields rendered into Peak's Settings window (text, password, bool, number, button). |

The host calls plugin lifecycle hooks in this order:

1. `Initialize(services)` — once at app start
2. `LoadSettings(json)` — once after Initialize, if persisted settings exist
3. `AttachToIsland(host)` — only if you implement `IIslandIntegrationPlugin`, after the island window is created
4. `OnActivate()` / `OnDeactivate()` — every time the widget enters/leaves a slot
5. `SaveSettings()` — when the user saves settings or `IIslandHost.RequestSettingsSave()` is called
6. `DetachFromIsland()` — only on `IIslandIntegrationPlugin`, on shutdown / disable

### 4. The `IIslandHost` API

Stash the `host` reference from `AttachToIsland`. Useful methods:

- **`SetVisualizerOverride(UIElement?)`** — replace the audio-visualizer circle with your own UI (e.g. an avatar bubble). `null` restores the default.
- **`SetCollapsedRenderer(Func<CollapsedWidgetKind, FrameworkElement?>?)`** — provide custom UI for one or more `CollapsedWidgetKind` values; return `null` from the callback to fall back to default rendering.
- **`SetViewModelProperty(name, value)`** — push a value into the shared `IslandViewModel` (e.g. `DiscordCallCount`); marshals to the UI thread for you.
- **`SetExpansionBlocked(bool)`** + **`SetCollapsedOverlay(UIElement?)`** — full-width takeover of the collapsed pill (used historically for incoming-call overlays).
- **`RequestSettingsSave()`** — ask Peak to call `SaveSettings()` on every plugin and persist the result. Call this when you mutate settings outside the Settings UI (e.g. OAuth token refresh).
- **`RefreshCollapsedSlots()`** — force the island to re-evaluate the registered collapsed renderers; call after your plugin's state changes affect what should be shown.
- **`UiDispatcher`** — the WPF dispatcher; use `host.UiDispatcher.Invoke(() => …)` from background threads when you touch UI.

### 5. Settings persistence

Peak stores plugin settings as a JSON blob inside `AppSettings.PluginSettings[<your plugin Id>]` (file: `%AppData%\Peak\settings.json`).

- `LoadSettings(JsonElement?)` — deserialize whatever you handed back from a previous `SaveSettings`.
- `SaveSettings()` — return a `JsonElement` representing your current state; Peak writes it to the central settings file.
- For an editable Settings UI, also implement `IPluginSettingsProvider` (text/password/bool/number/button fields, no custom layout). For richer configuration, expose a button that opens your own `Window` (see `Peak.Plugins.TeamSpeak/AfkChannelsDialog.xaml.cs` for a pattern that avoids XAML BAML loading across `AssemblyLoadContext` boundaries — build the dialog in code).

### 6. Caveats

- **No XAML-based plugin Windows.** WPF's `InitializeComponent` resolves BAML resources via pack-URIs scoped to the host's `AssemblyLoadContext` — that lookup fails for plugin-side BAML. Build any custom Windows / UserControls in code (see the TeamSpeak AFK picker as a worked example).
- **Type identity matters.** Always reference `Peak.Plugin.Sdk` with `<Private>false</Private>` + `<ExcludeAssets>runtime</ExcludeAssets>` so the host's SDK is used. Same goes for any other assembly the host already loads.
- **No `Application.Current` assumptions.** Use `host.UiDispatcher`, not `Application.Current.Dispatcher`, to thread-marshal — the latter works today but ties you to Peak's process layout.
- **Plugins can't reference `Peak.Core` or `Peak.App`.** Anything you need from the host travels through `IIslandHost`. If you find yourself reaching for an internal type, that's a signal to add an SDK method — open an issue.

## AI Disclaimer

This is a personal hobby project. AI (Claude) was used to assist with implementation since WPF can be verbose and ceremonious.

## License

This project is not licensed for redistribution. All rights reserved.
