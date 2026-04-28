# Changelog

All notable changes to Peak are documented here.

---

## [1.6.0] — 2026-04

### Added
- **Always-on-top overrides** — new Settings section lets you pin Peak above specific apps. Each entry has separate Fullscreen / Maximized checkboxes so the rule fires only for the states you care about. Apps observed running fullscreen or maximized are auto-detected and offered in the picker (mirroring how the notification-apps list grows).
- New `ModernEditableComboBox` style for any future editable dropdowns that want to match the non-editable `ModernComboBox` look.

### Fixed
- **Spotlight: search bar shifts upward when typing certain queries** — single-character substring matches (e.g. typing `/`) flooded the result list and the centered VerticalAlignment briefly clipped the search bar above the pill's top edge during the resize animation. Substring + acronym matching now requires at least 2 characters, and the SpotlightContent is top-anchored so it never escapes the pill.
- **Spotlight resize jank** — rapid CollectionChanged bursts (one per ObservableCollection Add/Clear) used to queue dozens of overlapping animations. Resize requests are now coalesced into a single deferred animation per dispatcher cycle.
- **Visualizer / call bubble spawning at the wrong location** — the helper that places the bubble next to the pill returned early when the bubble was hidden, so its Margin stayed at the default `(0,0,0,0)` and it appeared at the far-left edge of the window the next time it was shown. The Margin is now kept up to date even while invisible, with a layout-pass retry if measurements aren't ready yet.

### Changed
- TopMost override picker now uses the same dropdown style as the audio-device picker — click anywhere to open, no editable text field. Manual entry is no longer needed because auto-detect captures every app the user runs fullscreen or maximized.

---

## [1.5.0] — 2026-04

### Added
- **Spotlight search** — global hotkey overlay (default `Ctrl+Alt+Space`, configurable in Settings) opens a search field over the island with live ranked results from the Windows Shell `shell:appsFolder` namespace plus Start Menu shortcuts. Covers Win32 desktop programs and UWP / Microsoft Store apps in one index.
- **App icons** in result rows via `IShellItemImageFactory` — same icons Windows Explorer shows.
- Dynamic pill resizing — Spotlight starts compact and grows smoothly with each result (capped at 8 visible, scrolls beyond).
- Configurable Spotlight hotkey alongside the existing toggle hotkey, both rebindable in the Settings window.
- Diagnostic log at `%AppData%\Peak\logs\search.log` for troubleshooting index issues.

### Changed
- Spotlight respects the hidden island state — pressing the hotkey while Peak is hidden does nothing instead of forcing the island back.
- Tightened Spotlight chrome — concentric rounded corners (outer 18, inner 12) with uniform 6px padding on all four sides; the dark pill hugs the search bar tightly.
- Result rows mirror the search bar's design language — same height, corner radius, and highlight colour for selection.

---

## [1.4.2] — 2026-04

### Changed
- Replaced 9 `System.Diagnostics.Debug.WriteLine` call sites with structured `ILogger` logging so warnings survive release builds.
- Fixed NAudio COM disposal leaks in `VolumeMixerService` and `AudioVisualizerService`.
- Removed dead `ObservableProperty` declarations (`_memoryText`, `_weatherIcon`) and 6 unused `using` directives across the solution.
- Added explicit `Microsoft.Extensions.Logging.Abstractions` reference and a project `.editorconfig`.

### Added
- **GPU 3D-engine utilisation** as a third row in the System Monitor widget (sums Windows perf counters across `*_engtype_3D` instances).
- XML documentation for Plugin SDK public types and plugin settings classes.
- `CHANGELOG.md` (this file) and a completed `README.md`.

### Fixed
- System Monitor widget bars now stretch to fill the column instead of being pinned to a hardcoded 60px width.
- CS8602 nullable warning in `ClipboardService`.

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
