# Changelog

All notable changes to Peak are documented here.

---

## [1.10.7] — 2026-05

Hardening pass — second-round audit looked specifically for resource lifecycle, thread safety, and async robustness issues outside the dispatcher/perf scope of 1.10.2.

### Fixed
- **VolumeMixerService — COM session enumeration race** — `SetVolume` / `ToggleMute` (UI thread) and `RefreshCore` (worker thread) both enumerated `AudioSessionManager.Sessions` without coordination. NAudio's RCWs aren't documented thread-safe; concurrent enumerate-while-enumerate threw `RPC_E_*` from the COM marshaller, was swallowed, and the user saw sliders snap back. All three call sites now serialize through the existing `_lock`.
- **WeatherService — no HTTP timeout, no cancellation** — the "Weather" `HttpClient` registration relied on the default 100 s timeout. A network blackhole on any of the four chained endpoints could pile up a worst-case 400 s wait and a second `_weatherTimer` Tick before the first returned. Added explicit 15 s timeout on the named client + plumbed `CancellationToken` from a new `_lifetimeCts` through `FetchSmartAsync` / `FetchAsync`. Cleanup cancels the CTS so in-flight calls unwind on reload/exit.
- **WeatherService — async-void Tick crash** — `_weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync()` is implicitly async-void via `EventHandler`. Any unhandled exception (e.g. a `TaskCanceledException` from `_dispatcher.Invoke` during shutdown) became an unobserved task → process termination. Routed through `SafeRefreshWeatherAsync` which catches everything.
- **WeatherService — IP geolocation now HTTPS** — the `ip-api.com` geolocation fallback was using `http://`, allowing MITM to spoof the user's location. Switched to `https://`.
- **NotesService — timer churn + non-atomic save** — `MarkDirty` (called per keystroke when typing in QuickNotes) disposed and reallocated `System.Timers.Timer` on every call, and `Save()` did a direct `File.WriteAllText` that could leave a zero-byte file on AV / power-loss interference. Now reuses a single timer (Stop+Start), serializes a snapshot under `_lock` so the worker-thread Elapsed handler can't race UI-thread mutations, and writes via temp-file + `File.Move` (matching `SettingsManager`'s atomic-write pattern).
- **SystemMonitorService — startup crash on broken perf-counter registry** — `Start()` constructed `PerformanceCounter("Processor", ...)` unguarded. On systems where the counter database needed `lodctr /R`, this threw `InvalidOperationException` all the way up to `App.OnStartup` → "Startup failed" message, app refused to launch. Now wrapped in try/catch; `PollAsync` already null-checks `_cpuCounter` so a missing counter degrades to "CPU graph reads 0 %".
- **MediaService — WinRT manager reference held across reload** — `Dispose` unsubscribed the manager's event but kept the reference, so `TeardownForReload` accumulated a stale `GlobalSystemMediaTransportControlsSessionManager` RCW per reload until the next GC. Now nulls the field too.
- **Discord/TeamSpeak plugins — `_rpc` / `_client` torn-read NREs in event handlers** — the connect-loop worker assigns `_rpc = null` (or `_client = null`) on disconnect; events already in flight on the WebSocket read-loop could race the assignment between a null-check and the next field read, throwing NRE in fire-and-forget code → process down. Every handler now snapshots the field to a local on entry and uses the snapshot exclusively. Same idiom applied to the `SetCollapsedRenderer` delegates and the TeamSpeak `IsLocalUserInAfkChannel` helper.

### Notes
Audit found ~23 candidate issues; 9 verified as real and fixed in this release. The remaining items were either correct as-written (already locked, already null-checked, or operating on inherently thread-safe types like `ConcurrentDictionary`) or stylistic and intentionally left alone.

---

## [1.10.6] — 2026-05

### Changed
- **Settings window — categorical tabs.** Single mega-tab containing every section split into *General* (behaviour + hotkeys + version/update info), *Appearance* (border + theme + colours + collapsed slot layout), *Widgets* (audio device / network graph / weather location), *Apps* (always-on-top + notification mute list), and the existing *Plugins* tab.
- **Media widget — LIVE badge** — sits 3 px lower (`-7` instead of `-4`) so the ribbon reads as its own tag below the cover instead of crowding the album-art frame edge.

---

## [1.10.5] — 2026-05

### Fixed
- **Media — Play button felt "dead" after pause** — `TryTogglePlayPauseAsync` is racy on apps that don't propagate pause state to SMTC instantly (Spotify especially); Toggle reads stale "playing" and pauses again. Now reads `GetPlaybackInfo().PlaybackStatus` first and dispatches to the explicit `TryPlayAsync` / `TryPauseAsync` branch. Toggle remains as a fallback when status read throws.
- **Greeting could bleed into Expanded state** — the welcome greeting shown after Hide → Unhide unconditionally restored `CollapsedContent.Visibility = Visible` after its 5 s fade-out, even if the user had expanded the island in the meantime. Now gated on `_currentAnimatedState is Collapsed or Hidden && _collapsedOverlay == null`, mirroring the same guard `SetCollapsedOverlay` already uses.

---

## [1.10.4] — 2026-05

### Fixed
- **Media — position text could freeze on the previous track after a skip.**
  - Subscribed to SMTC's `TimelinePropertiesChanged` event (was previously polling-only). Pushes a single `PositionChanged` emit on every timeline update so position refreshes immediately after track changes / seeks instead of waiting for the next 1 Hz poll tick.
  - Push a fresh position snapshot directly after `RefreshAsync` so metadata + timeline land in the same dispatch cycle.
  - Moved the VM's position-bucket dedup inside the `BeginInvoke` action so it runs deterministically ordered against `OnMediaChanged`'s cache reset (BeginInvoke is FIFO per dispatcher). The previous out-of-order race could silently drop the first position emit for a new track.

---

## [1.10.3] — 2026-05

### Fixed
- **Media — SMTC race conditions hardened.**
  - **Overlapping refreshes**: each `RefreshAsync` captures a generation token; results are discarded if another session swap or refresh landed during any await.
  - **Stale event handlers**: every handler filters by `sender == _currentSession` under a lock; `SessionClosed` properly tears down listeners (was leaking subscriptions on the gone session).
  - **Position-poller leak**: each polling task captures `(session, generation)` at start and exits as soon as either changes, so it can't emit a final `PositionChanged` for a session that has just been replaced.
  - VM-side: `MediaInfo.TrackKey` (`Title|Artist|Album`) lets the VM detect "actually a new track" vs "same track, metadata refresh"; new keys clear `AlbumArt` immediately so a new title never appears under the previous track's cover.
  - VM-side: background bitmap decodes are gated by an `_albumArtToken` so out-of-order completions can't overwrite the latest art.
  - VM-side: `OnMediaSessionClosed` resets every cached scrap of the previous session.

---

## [1.10.2] — 2026-05

### Performance
- **VolumeMixerService — refresh moved to thread-pool** with PID→name cache + fingerprint-based change detection; was the biggest single stutter source (read `Process.MainModule.FileVersionInfo` per session every second on the UI thread). Poll interval bumped 1 s → 2 s.
- **Album-art / notification-icon decoding moved off the UI thread** — JPEG/PNG decode for thumbnails can take 5–30 ms; `BitmapImage.Freeze()` makes the result cross-thread-safe.
- **Synchronous `SettingsManager.Save()` calls offloaded to `Task.Run`** for first-seen fullscreen and notification apps.
- **Background → UI dispatch swapped from `Invoke` to `BeginInvoke`** across SystemMonitor / Network / Media handlers + bucket dedup so we don't churn the dispatcher on no-op ticks.
- **DoScaleTransition cancels previous opacity animations on every content panel up front** — rapid Peek↔Expanded toggles were stacking fade-ins on the same element with overlapping completion handlers (the "hangs in expanded state" symptom). Per-call `DispatcherTimer` for the fade delay replaced with `BeginTime` on the fade-in animation itself.
- **OnViewModelPropertyChanged** dropped redundant `Dispatcher.Invoke` (all VM property changes arrive on UI thread already) + fast early-out on unhandled property names.
- **Visualizer levels** check `_visualizerRunning` before queueing dispatcher work.
- **Clipboard image hash** samples a 64-byte slice from the first row instead of allocating the whole pixel buffer per poll. Poll interval raised 500 ms → 1000 ms.

---

## [1.10.1] — 2026-05

### Fixed
- **Companion plugin missing from installer** — v1.10.0 shipped the source and built the DLL in CI but the Inno Setup script wasn't updated to package the files into `PeakSetup.exe`. Added the `companion` task with whole-folder bundle (excluding `*.pdb` / `*.xml`) so the WebView2 managed assemblies, native runtime loaders, and `.deps.json` ship together.

---

## [1.10.0] — 2026-05

### Added
- **Companion plugin** — animated WebView2 face in the expanded header between Clock and Weather. Reactive moods (happy/angry/love/suspicious/sleepy/surprised/wink/idle) driven by `IslandViewModel` state via reflection. Mood logic is fully declarative — `moods.json` + a tiny expression DSL (`HasMedia && IsPlaying`, `CpuUsage > 90`, `Hour >= 22`, …) parsed by `MoodExpression` with hot-reload via `FileSystemWatcher`. In-app editor (`MoodEditorWindow`) for both `moods.json` and an optional `companion.html` override; both files live in `%AppData%\Peak\plugins\companion\`.
- **SDK** — `IIslandHost.SetExpandedHeaderContent(UIElement?)` lets plugins drop a small UI fragment into the expanded header overlay spanning the Clock/Weather columns.

### Fixed
- `ExpandedHeaderHost` visibility is now toggled around state transitions (hidden during scale animations, hidden in `IslandState.Hidden`) so airspace controls like WebView2 don't paint stale content while the pill is mid-animation or off-screen.
- `PluginLoader` writes per-type instantiation failures to `plugin-load-error.log` so silent plugin-init crashes are debuggable in production builds.
- Album-art thumbnail in the peek state re-positioned with a `-7` left margin so the visible left gap matches the `~9 px` ring above and below the 38×38 image.

---

## [1.9.0] — 2026-04

### Added
- **User-supplied themes** — drop a JSON file into `%AppData%\Peak\themes\` to add a custom theme. Format: `{ "id", "name", "background", "accent" }`. New `ThemeService` merges built-ins with user themes; user themes get a coloured outer ring in the picker so they're distinguishable.
- **"Open themes folder" / "Reload"** buttons in Settings → Theme Presets — no restart required to pick up a new JSON.
- **Theme authoring guide** in `README.md` covering format, loading, and tips.

---

## [1.8.0] — 2026-04

### Added
- **Media: livestream badge** — when the currently-playing item has no finite duration (Twitch / YouTube Live / radio), the progress bar is replaced by a Twitch-style red "LIVE" pill overlaid on the bottom of the album art.
- **Media: smooth progress animation** — the fill bar now interpolates between the once-per-second `MediaProgress` samples via a 900 ms cubic-ease-out animation so the playhead glides instead of ticking.
- **Plugin development guide** — full walkthrough in `README.md`: project setup boilerplate, lifecycle hooks, the `IIslandHost` API surface, settings persistence, and the gotchas around `AssemblyLoadContext` + WPF BAML loading.

### Changed
- **Media: fixed-width time column (80 px, right-aligned)** — switching tracks no longer reflows the progress bar as the digit count changes.
- **Media: progress bar redesigned** — bumped from 3 px to 5 px tall, and the fill now uses an animated `Width` (not a `ScaleTransform`) so both ends stay properly rounded at low progress values.
- **Media: time text** — bumped from 9 pt @ 40 % opacity to 10 pt @ 60 % opacity for readability.
- **Media slot sizing** — 100 px when a track is playing, automatically shaved to 90 px during a livestream so the missing progress bar doesn't leave a visible gap below the album art.

---

## [1.7.0] — 2026-04

### Added
- **TeamSpeak — per-server AFK channels** — flag up to 3 channels of any TS server as "AFK". While you're sitting in one of them, Peak suppresses the call counter and the active-speaker visualizer. The picker is reachable from the plugin settings via a new "Manage AFK channels…" button (with a location-pin glyph) and from the plugin's widget view.
- TeamSpeak plugin now caches the full channel tree per server — needed for the AFK picker, also opens the door for richer per-channel features later.

### Fixed
- **TeamSpeak — server identity now stable across reconnects** — the plugin previously keyed per-server settings by the per-session `connectionId`, which changes every time TeamSpeak reconnects. Switched to the cryptographic `serverUid` (extracted from `connectStatusChanged.info` AND `connections[].properties.uniqueIdentifier` so both auth flows work).
- TeamSpeak plugin now handles the standalone `channels` event TS6 emits during cached-API-key reconnects — without this the channel cache stayed empty and the AFK picker showed "channels not ready".

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
