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

Plugins implement interfaces from `Peak.Plugin.Sdk`:

- `IWidgetPlugin` — provides a widget view rendered inside the island grid
- `IIslandIntegrationPlugin` — integrates directly with the island (collapsed-slot renderers, visualizer overrides)
- `IPluginSettingsProvider` — exposes settings fields that appear in the Peak settings window

Reference `Peak.Plugin.Sdk` with `<Private>false</Private>` and `<ExcludeAssets>runtime</ExcludeAssets>` so the host's copy of the SDK is used at runtime (avoids type-identity mismatches across `AssemblyLoadContext` boundaries).

## AI Disclaimer

This is a personal hobby project. AI (Claude) was used to assist with implementation since WPF can be verbose and ceremonious.

## License

This project is not licensed for redistribution. All rights reserved.
