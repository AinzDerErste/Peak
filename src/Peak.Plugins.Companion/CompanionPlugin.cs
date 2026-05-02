using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Wpf;
using Peak.Plugin.Sdk;

namespace Peak.Plugins.Companion;

/// <summary>
/// Renders an animated companion (LED-eyes panel) in the expanded island
/// header — between the Clock slot (left) and the Weather slot (right). The
/// face is a small WebView2 hosting <c>Resources/companion.html</c>; the
/// plugin pushes mood changes to it via <c>window.companion.setMood(name)</c>.
///
/// Two extension points keep customisation friction low without recompiling:
/// <list type="bullet">
///   <item><b>moods.json</b> in the plugin's data folder defines mood rules
///         declaratively (priority + condition expression). Edited at runtime
///         and reloaded via FileSystemWatcher.</item>
///   <item><b>companion.html</b> in the same folder, if present, replaces the
///         embedded HTML — useful for visual tweaks or new mood graphics.</item>
/// </list>
/// </summary>
public class CompanionPlugin : IWidgetPlugin, IIslandIntegrationPlugin, IPluginSettingsProvider
{
    static CompanionPlugin()
    {
        // Fires the very first time any code touches this type — well before
        // Activator.CreateInstance is called by the host. If the plugin DLL
        // itself fails to load (TypeRef resolution etc.) this never runs and
        // we'll see no log entry.
        DiagLog("Static ctor: Companion type loaded");
    }

    /// <summary>
    /// Explicit instance ctor with a DiagLog so we can tell the difference
    /// between "type loaded but instance never created" (only static-ctor line
    /// in the log) and "instance created but Initialize threw" (also has this
    /// line).
    /// </summary>
    public CompanionPlugin()
    {
        DiagLog("Instance ctor: companion plugin instance created");
    }

    public string Id => "peak.plugins.companion";
    public string Name => "Companion";
    public string Description => "Animated companion eyes in the expanded island header. Reacts to your activity.";
    public string Icon => "👀";

    private CompanionSettings _settings = new();
    private IIslandHost? _host;
    private ILogger<CompanionPlugin>? _logger;
    private WebView2? _webView;
    private bool _webViewReady;
    private MoodEngine? _moodEngine;
    private FileSystemWatcher? _moodsWatcher;
    private DateTime _lastMoodsReload = DateTime.MinValue;

    /// <summary>Pixel size of the header panel. 2:1 aspect — the eyes' viewBox is 400×200.
    /// Solid SVG paths scale crisply at any size, so the panel can stay
    /// compact without the LED-matrix smearing problem.</summary>
    private const int HeaderWidth = 130;
    private const int HeaderHeight = 65;

    /// <summary>The plugin's data folder under %AppData% — moods.json,
    /// companion.html overrides, and plugin.log all live here.</summary>
    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Peak", "plugins", "companion");

    private static string MoodsJsonPath  => Path.Combine(DataDir, "moods.json");
    private static string OverrideHtmlPath => Path.Combine(DataDir, "companion.html");

    public void Initialize(IServiceProvider services)
    {
        DiagLog("Initialize: ENTER");
        try
        {
            _logger = services.GetService<ILogger<CompanionPlugin>>();
            DiagLog("Initialize: companion plugin instantiated (logger=" + (_logger != null ? "ok" : "null") + ")");
        }
        catch (Exception ex)
        {
            DiagLog($"Initialize FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Append-only diagnostic log at <c>%AppData%\Peak\plugins\companion\plugin.log</c>.
    /// Used while tracking down lifecycle issues — independent of the host's
    /// ILogger so failures still surface even if logging isn't configured.
    /// </summary>
    private static void DiagLog(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "plugin.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { }
    }

    public void LoadSettings(JsonElement? settings)
    {
        DiagLog("LoadSettings: ENTER (hasValue=" + settings.HasValue + ")");
        if (settings is not { ValueKind: JsonValueKind.Object }) { DiagLog("LoadSettings: no settings object, leaving defaults"); return; }
        try
        {
            _settings = JsonSerializer.Deserialize<CompanionSettings>(settings.Value.GetRawText()) ?? new();
            DiagLog("LoadSettings: deserialized OK");
        }
        catch (Exception ex)
        {
            DiagLog($"LoadSettings FAILED: {ex.GetType().Name}: {ex.Message}");
            _logger?.LogWarning(ex, "Companion: settings deserialize failed, using defaults");
            _settings = new();
        }
    }

    public JsonElement? SaveSettings() => JsonSerializer.SerializeToElement(_settings);

    /// <summary>
    /// The widget-slot view — kept minimal because the companion is meant to live
    /// in the header, not in a slot. Returning a placeholder lets the user see
    /// the plugin in the picker if they want to confirm it's loaded.
    /// </summary>
    public FrameworkElement CreateView(object dataContext) =>
        new TextBlock
        {
            Text = "Companion lives in the header, not a slot",
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

    public void OnActivate() { }
    public void OnDeactivate() { }

    public void AttachToIsland(IIslandHost host)
    {
        DiagLog("AttachToIsland: entering");
        _host = host;

        host.UiDispatcher.Invoke(() =>
        {
            try
            {
                DiagLog("AttachToIsland: building WebView");
                _webView = BuildWebView();
                DiagLog("AttachToIsland: calling SetExpandedHeaderContent");
                host.SetExpandedHeaderContent(_webView);
                DiagLog("AttachToIsland: SetExpandedHeaderContent returned");

                var rules = LoadOrCreateMoodRules();
                _moodEngine = new MoodEngine(host.ViewModel, _settings, rules);
                _moodEngine.Diagnostic += DiagLog;
                _moodEngine.MoodChanged += OnMoodChanged;
                StartMoodsWatcher();

                DiagLog($"AttachToIsland: completed ({rules.Moods.Count} mood rule(s) loaded)");
            }
            catch (Exception ex)
            {
                DiagLog($"AttachToIsland FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                _logger?.LogWarning(ex, "Companion: AttachToIsland failed");
            }
        });
    }

    public void DetachFromIsland()
    {
        try { _moodsWatcher?.Dispose(); } catch { }
        _moodsWatcher = null;
        try { _moodEngine?.Dispose(); } catch { }
        _moodEngine = null;

        _host?.UiDispatcher.Invoke(() =>
        {
            _host?.SetExpandedHeaderContent(null);
            try { _webView?.Dispose(); } catch { }
            _webView = null;
            _webViewReady = false;
        });
        _host = null;
    }

    // ─── moods.json loading & hot-reload ─────────────────────────────

    /// <summary>
    /// Reads <c>moods.json</c> from the data folder; if missing, writes the
    /// default rule set so the user has a starter file to edit.
    /// </summary>
    private MoodRulesConfig LoadOrCreateMoodRules()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            if (!File.Exists(MoodsJsonPath))
            {
                WriteDefaultMoodsJson();
                DiagLog("moods.json: not present — wrote defaults");
                return MoodRulesConfig.Default();
            }

            var json = File.ReadAllText(MoodsJsonPath);
            var cfg = JsonSerializer.Deserialize<MoodRulesConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (cfg == null || cfg.Moods.Count == 0)
            {
                DiagLog("moods.json: empty or invalid — falling back to defaults");
                return MoodRulesConfig.Default();
            }
            DiagLog($"moods.json: loaded {cfg.Moods.Count} rule(s)");
            return cfg;
        }
        catch (Exception ex)
        {
            DiagLog($"moods.json: load FAILED ({ex.GetType().Name}: {ex.Message}) — using defaults");
            _logger?.LogWarning(ex, "Companion: moods.json load failed");
            return MoodRulesConfig.Default();
        }
    }

    /// <summary>
    /// Serialises the factory defaults to <c>moods.json</c>. Used both on
    /// first run and from the "Reset moods.json" settings button.
    /// </summary>
    private static void WriteDefaultMoodsJson()
    {
        Directory.CreateDirectory(DataDir);
        var json = JsonSerializer.Serialize(MoodRulesConfig.Default(), new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            // Default escaper turns >, <, &, ' into \uXXXX sequences which makes
            // the rule expressions unreadable for editing. Relaxed escaping is
            // safe here because the file is never embedded in HTML.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(MoodsJsonPath, json);
    }

    /// <summary>
    /// Watches <c>moods.json</c> for edits and reloads the rule set into the
    /// running MoodEngine. Debounced — many editors fire 2-3 events per save.
    /// </summary>
    private void StartMoodsWatcher()
    {
        try
        {
            _moodsWatcher?.Dispose();
            _moodsWatcher = new FileSystemWatcher(DataDir, "moods.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _moodsWatcher.Changed += OnMoodsFileChanged;
            _moodsWatcher.Created += OnMoodsFileChanged;
            _moodsWatcher.Renamed += OnMoodsFileChanged;
            DiagLog("MoodsWatcher: started");
        }
        catch (Exception ex)
        {
            DiagLog($"MoodsWatcher: start FAILED — {ex.Message}");
        }
    }

    private void OnMoodsFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce — many editors fire several events per save.
        if ((DateTime.UtcNow - _lastMoodsReload).TotalMilliseconds < 250) return;
        _lastMoodsReload = DateTime.UtcNow;

        // The file may still be locked by the editor for a moment.
        Task.Delay(150).ContinueWith(_ =>
        {
            try
            {
                var rules = LoadOrCreateMoodRules();
                _host?.UiDispatcher.Invoke(() => _moodEngine?.UpdateRules(rules));
                DiagLog("MoodsWatcher: rules reloaded");
            }
            catch (Exception ex)
            {
                DiagLog($"MoodsWatcher: reload FAILED — {ex.Message}");
            }
        });
    }

    // ─── WebView setup ────────────────────────────────────────────

    private WebView2 BuildWebView()
    {
        var wv = new WebView2
        {
            Width = HeaderWidth,
            Height = HeaderHeight,
            DefaultBackgroundColor = System.Drawing.Color.Transparent
        };

        // EnsureCoreWebView2Async needs to run before any navigation. The
        // initial environment-load is async; we navigate from the
        // CoreWebView2InitializationCompleted event so we don't race it.
        wv.CoreWebView2InitializationCompleted += (_, e) =>
        {
            DiagLog($"WebView2 init complete: success={e.IsSuccess}, error={e.InitializationException?.Message}");
            if (!e.IsSuccess) return;
            try
            {
                wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                wv.CoreWebView2.Settings.AreDevToolsEnabled = false;
                wv.CoreWebView2.Settings.IsZoomControlEnabled = false;
                wv.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // Override CONFIG before the HTML's IIFE runs. Solid-shape
                // variant — paths scale cleanly so we keep breathing on and a
                // small glow for personality. Mouse-tracking stays off because
                // the header companion shouldn't pull focus.
                wv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    window.COMPANION_CONFIG = {
                        transparent: true,
                        mouseTracking: false,
                        breathing: true,
                        glow: { blur: '1.5px', color: 'rgba(255,255,255,0.4)' }
                    };");

                var html = LoadCompanionHtml();
                DiagLog($"WebView2 init: loaded {html.Length} chars of HTML, navigating");
                wv.NavigateToString(html);
            }
            catch (Exception ex)
            {
                DiagLog($"CoreWebView2 init FAILED: {ex.Message}");
                _logger?.LogWarning(ex, "Companion: CoreWebView2 init failed");
            }
        };

        wv.NavigationCompleted += (_, e) =>
        {
            DiagLog($"WebView2 navigation complete: success={e.IsSuccess}, status={e.HttpStatusCode}");
            _webViewReady = true;
            _moodEngine?.Reevaluate();
        };

        // EnsureCoreWebView2Async is fire-and-forget here. If the runtime is
        // missing the task faults; we surface that via a continuation.
        _ = wv.EnsureCoreWebView2Async().ContinueWith(t =>
        {
            if (t.IsFaulted)
                DiagLog($"EnsureCoreWebView2Async FAULTED: {t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.FromCurrentSynchronizationContext());

        DiagLog("BuildWebView: WebView2 control created, init kicked off");
        return wv;
    }

    /// <summary>
    /// Returns the HTML document for the companion. If the user dropped a
    /// <c>companion.html</c> next to <c>moods.json</c> in the plugin's data
    /// folder, that file wins — otherwise we fall back to the embedded copy.
    /// This is the "advanced override" path: full visual customisation
    /// without recompiling the plugin.
    /// </summary>
    private static string LoadCompanionHtml()
    {
        // 1. User override on disk
        try
        {
            if (File.Exists(OverrideHtmlPath))
            {
                var custom = File.ReadAllText(OverrideHtmlPath);
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    DiagLog($"LoadCompanionHtml: using override at {OverrideHtmlPath}");
                    return custom;
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog($"LoadCompanionHtml: override read failed — {ex.Message}; falling back to embedded");
        }

        // 2. Embedded fallback
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("companion.html", StringComparison.OrdinalIgnoreCase));
        if (name == null) throw new FileNotFoundException("Embedded companion.html not found in plugin assembly");

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ─── Mood plumbing ────────────────────────────────────────────

    private void OnMoodChanged(string mood)
    {
        if (_host == null) return;
        _host.UiDispatcher.Invoke(async () =>
        {
            if (!_webViewReady || _webView?.CoreWebView2 == null) return;
            try
            {
                // The HTML escapes the mood name itself; we still defensively
                // strip quotes to keep the inline-script tidy.
                var safe = mood.Replace("'", "\\'");
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.companion.setMood('{safe}')");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Companion: setMood({Mood}) failed", mood);
            }
        });
    }

    // ─── Settings UI ──────────────────────────────────────────────

    public IReadOnlyList<PluginSettingField> GetSettingsSchema() => new[]
    {
        new PluginSettingField
        {
            Key = "AutoMoodEnabled",
            Label = "Auto mood",
            Description = "Let the companion react to your activity. Turn off to lock it to the manual mood.",
            Kind = PluginSettingFieldKind.Bool,
            CurrentValue = _settings.AutoMoodEnabled.ToString()
        },
        new PluginSettingField
        {
            Key = "ManualMood",
            Label = "Manual / fallback mood",
            Description = "Used when auto-mood is off (or as the fallback when no rule matches). Built-in moods: idle, happy, sad, angry, surprised, suspicious, sleepy, love, wink.",
            Kind = PluginSettingFieldKind.Text,
            CurrentValue = _settings.ManualMood,
            Placeholder = "idle"
        },
        new PluginSettingField
        {
            Key = "EditMoodRules",
            Label = "Edit mood rules…",
            Description = "Opens an in-app editor for moods.json — change priorities, add new triggers, tweak conditions. Saves are hot-reloaded; \"Reset to defaults\" inside the editor restores the built-in rule set.",
            Kind = PluginSettingFieldKind.Button
        },
        new PluginSettingField
        {
            Key = "EditCompanionHtml",
            Label = "Edit companion HTML…",
            Description = "Opens an in-app editor for the override companion.html. Tweak the SVG eyes, animations, or add new mood styles without recompiling. Empty file → falls back to the embedded default.",
            Kind = PluginSettingFieldKind.Button
        },
        new PluginSettingField
        {
            Key = "OpenDataFolder",
            Label = "Open plugin folder",
            Description = "Opens %AppData%\\Peak\\plugins\\companion in Explorer — useful for inspecting plugin.log or backing up the files.",
            Kind = PluginSettingFieldKind.Button
        }
    };

    public void SetSettingValue(string key, string? value)
    {
        switch (key)
        {
            case "AutoMoodEnabled":
                _settings.AutoMoodEnabled = ParseBool(value);
                _moodEngine?.UpdateSettings(_settings);
                break;
            case "ManualMood":
                _settings.ManualMood = (value ?? "idle").Trim().ToLowerInvariant();
                _moodEngine?.UpdateSettings(_settings);
                break;
            case "EditMoodRules":
                OpenMoodRulesEditor();
                break;
            case "EditCompanionHtml":
                OpenHtmlEditor();
                break;
            case "OpenDataFolder":
                OpenDataFolder();
                break;
        }
    }

    /// <summary>
    /// Opens the in-app editor for <c>moods.json</c>. Validates JSON on save
    /// and the editor's "Reset to defaults" button re-seeds from
    /// <see cref="MoodRulesConfig.Default"/>. The FileSystemWatcher picks up
    /// the resulting file write and hot-reloads the running engine — but we
    /// also push directly afterward so a spurious watcher miss doesn't leave
    /// the user wondering why their save didn't take.
    /// </summary>
    private void OpenMoodRulesEditor()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            // Make sure the file exists with defaults if the user has never
            // saved — the editor's loader prefers a real file over the
            // defaults provider.
            if (!File.Exists(MoodsJsonPath)) WriteDefaultMoodsJson();

            ShowEditorDialog(() => new MoodEditorWindow(
                MoodsJsonPath,
                "Companion — Edit mood rules",
                validateJson: true,
                defaultsProvider: SerializeDefaultMoods));

            // Force a reload regardless of whether the watcher fired.
            try
            {
                var rules = LoadOrCreateMoodRules();
                _host?.UiDispatcher.Invoke(() => _moodEngine?.UpdateRules(rules));
            }
            catch (Exception ex) { DiagLog($"OpenMoodRulesEditor: post-save reload failed — {ex.Message}"); }
        }
        catch (Exception ex)
        {
            DiagLog($"OpenMoodRulesEditor FAILED: {ex.Message}");
            _logger?.LogWarning(ex, "Companion: OpenMoodRulesEditor failed");
        }
    }

    /// <summary>
    /// Opens the in-app editor for the optional <c>companion.html</c>
    /// override. If no override exists yet, the editor seeds itself with the
    /// embedded HTML so the user has a complete starting point. Saving an
    /// empty file effectively "removes" the override (LoadCompanionHtml falls
    /// back to embedded for whitespace-only content).
    /// </summary>
    private void OpenHtmlEditor()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            ShowEditorDialog(() => new MoodEditorWindow(
                OverrideHtmlPath,
                "Companion — Edit HTML override",
                validateJson: false,
                defaultsProvider: ReadEmbeddedHtml));

            // HTML changes only take effect on next WebView2 navigation. Reload
            // the navigated page so the user sees their edits immediately,
            // without needing to detach/reattach the plugin.
            _host?.UiDispatcher.Invoke(() =>
            {
                if (_webView?.CoreWebView2 == null) return;
                try
                {
                    var html = LoadCompanionHtml();
                    _webViewReady = false;
                    _webView.NavigateToString(html);
                    DiagLog("OpenHtmlEditor: re-navigated WebView with updated HTML");
                }
                catch (Exception ex) { DiagLog($"OpenHtmlEditor: re-navigate failed — {ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            DiagLog($"OpenHtmlEditor FAILED: {ex.Message}");
            _logger?.LogWarning(ex, "Companion: OpenHtmlEditor failed");
        }
    }

    /// <summary>
    /// Centralised dialog opener — finds a sensible parent window from the
    /// host application's open windows so the editor opens centred over the
    /// Settings window (rather than wandering off to a random monitor).
    /// </summary>
    private void ShowEditorDialog(Func<MoodEditorWindow> factory)
    {
        // Ensure both Window construction and ShowDialog run on the UI thread.
        // SetSettingValue is normally called from the SettingsWindow button
        // click handler (already UI thread), but routing through the host's
        // dispatcher keeps this safe if a future caller invokes it from a
        // background thread.
        _host?.UiDispatcher.Invoke(() =>
        {
            var editor = factory();
            editor.Owner = FindOwnerWindow();
            editor.ShowDialog();
        });
    }

    /// <summary>Pick the topmost active window in the host app as a dialog owner.</summary>
    private static Window? FindOwnerWindow()
    {
        var app = Application.Current;
        if (app == null) return null;
        foreach (Window w in app.Windows)
            if (w.IsActive && w.IsLoaded) return w;
        // Fallback — any visible non-island window, then MainWindow as last resort.
        foreach (Window w in app.Windows)
            if (w.IsLoaded && w.IsVisible) return w;
        return app.MainWindow;
    }

    /// <summary>Default-rules text for the editor's "Reset to defaults" button.</summary>
    private static string SerializeDefaultMoods() =>
        JsonSerializer.Serialize(MoodRulesConfig.Default(), new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    /// <summary>Returns the embedded companion.html — used to seed the HTML editor.</summary>
    private static string ReadEmbeddedHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("companion.html", StringComparison.OrdinalIgnoreCase));
        if (name == null) return "";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Opens the plugin's data folder in Explorer so the user can edit
    /// <c>moods.json</c> or drop in a custom <c>companion.html</c>.
    /// </summary>
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            // Make sure moods.json exists before we open the folder so the user
            // doesn't see an empty directory and wonder where to start.
            if (!File.Exists(MoodsJsonPath)) WriteDefaultMoodsJson();

            Process.Start(new ProcessStartInfo
            {
                FileName = DataDir,
                UseShellExecute = true,
                Verb = "open"
            });
            DiagLog($"OpenDataFolder: opened {DataDir}");
        }
        catch (Exception ex)
        {
            DiagLog($"OpenDataFolder FAILED: {ex.Message}");
            _logger?.LogWarning(ex, "Companion: OpenDataFolder failed");
        }
    }

    private static bool ParseBool(string? v) =>
        bool.TryParse(v, out var b) && b;
}
