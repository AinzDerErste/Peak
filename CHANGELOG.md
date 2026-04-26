# Changelog

All notable changes to Peak are documented here.

---

## [1.4.1] — 2025-04

### Removed
- Discord incoming-call overlay — Discord's Local RPC API does not emit call-ring events for DM or group calls during the ring phase. `NOTIFICATION_CREATE` only fires for message notifications (type 0), not for call notifications (type 3). The feature was removed rather than shipped broken.

### Changed
- Replaced all `System.Diagnostics.Debug.WriteLine` calls with structured `ILogger` logging so warnings survive release builds.
- Removed 7 legacy widget-visibility boolean properties from `AppSettings` (`ShowClock`, `ShowMedia`, `ShowSystemMonitor`, `ShowWeather`, `ShowCalendar`, `ShowNotifications`, `ShowTimer`) and their dead UI counterparts in the Settings window — these were superseded by the widget slot system.

---

## [1.4.0] — 2025-03

### Added
- **Discord plugin** — voice-channel participant list, active-speaker avatar overlay, and collapsed-slot call-count badge. Uses Discord Local RPC with OAuth2 (`rpc.voice.read` scope).
- **TeamSpeak plugin** — voice-channel participant list and active-speaker display via the TeamSpeak 3 Client Query interface.
- `IIslandIntegrationPlugin` SDK interface — plugins can now attach collapsed-slot renderers and override the audio visualizer.
- `SetExpansionBlocked` / `SetCollapsedOverlay` on `IIslandHost` for full-width collapsed overlays.
- `VoiceCallCount` collapsed-slot type — shows whichever voice app is active (Discord, TeamSpeak, or both).

---

## [1.3.0] — 2025-01

### Added
- **Plugin system** — drop a DLL into `%AppData%\Peak\plugins\<name>\` to add new widgets and island integrations. Plugins are isolated in their own `AssemblyLoadContext`.
- `IWidgetPlugin`, `IIslandHost`, `IPluginSettingsProvider` SDK interfaces (`Peak.Plugin.Sdk`).
- Per-plugin settings editor in the Settings window (generated from `IPluginSettingsProvider.GetSettingsSchema()`).
- Plugin enable/disable toggles in Settings.
- `PluginLoader.DiscoverAll()` for listing plugins (including disabled ones) without fully initialising them.

---

## [1.2.0] — 2024-11

### Added
- **Pomodoro widget** — 25/5-minute work/break timer with session counter.
- **Volume Mixer widget** — per-app audio session volume sliders.
- **Quick Notes widget** — persistent plain-text scratchpad.
- **Clipboard History widget** — tracks the last 25 clipboard entries (text, images, file drops) with one-click re-copy.

---

## [1.1.0] — 2024-09

### Added
- **Network widget** — real-time download/upload graph (line or bar style, configurable).
- **Quick Access widget** — pinned file/folder shortcuts that open in Explorer.
- **Auto-hide** — island slides behind the top edge after a configurable idle timeout.
- **Theme presets** — several built-in colour combinations selectable in Settings.
- Global toggle hotkey (default: Ctrl+Shift+N), fully rebindable in Settings.

---

## [1.0.0] — 2024-07

### Added
- Initial release.
- Collapsed / Peek / Expanded island states with smooth WPF animations.
- Built-in widgets: Clock, Weather (Open-Meteo), Media Controls (SMTC), System Monitor (CPU/RAM/GPU), Calendar, Timer.
- Audio visualizer circle rendered next to the notch via WASAPI loopback capture.
- Windows toast-notification integration — notifications peek through the island.
- Configurable 2×3 widget grid with Wide-row support.
- 3-slot collapsed bar (Left, Center, Right) with per-slot widget selection.
- Settings window — monitor selection, appearance, weather location, startup, notification muting.
- Auto-update via GitHub Releases.
- Inno Setup installer with autostart registry entry.
