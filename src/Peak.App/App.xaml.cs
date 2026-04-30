using System.Windows;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using DrawingIcon = System.Drawing.Icon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Peak.App.ViewModels;
using Peak.App.Views;
using Peak.App.Views.Widgets;
using Peak.Core.Configuration;
using Peak.Core.Plugins;
using Peak.Core.Services;

namespace Peak.App;

public partial class App : Application
{
    private static readonly Mutex SingleInstanceMutex = new(true, "Peak_SingleInstance_Mutex");
    private IHost? _host;
    private ILogger<App>? _logger;
    private TaskbarIcon? _trayIcon;
    private IslandWindow? _islandWindow;
    private PluginLoader? _pluginLoader;

    public IServiceProvider Services => _host!.Services;
    public PluginLoader? PluginLoader => _pluginLoader;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!SingleInstanceMutex.WaitOne(TimeSpan.Zero, true))
        {
            MessageBox.Show("Peak is already running.", "Peak", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Prevent auto-shutdown before window is shown
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Setup tray icon once — survives reloads
        SetupTrayIcon();

        await InitializeAppAsync();
    }

    /// <summary>
    /// Build the DI host, load settings + plugins, and show the island window.
    /// Safe to call again after <see cref="TeardownForReload"/>.
    /// </summary>
    private async Task InitializeAppAsync()
    {
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<SettingsManager>();
                    services.AddSingleton<MediaService>();
                    services.AddSingleton<SystemMonitorService>();
                    services.AddSingleton<NetworkMonitorService>();
                    services.AddSingleton<AudioVisualizerService>();
                    services.AddSingleton<NotificationService>();
                    services.AddSingleton<WeatherService>();
                    services.AddSingleton<CalendarService>();
                    services.AddSingleton<TimerService>();
                    services.AddSingleton<ClipboardService>();
                    services.AddSingleton<NotesService>();
                    services.AddSingleton<VolumeMixerService>();
                    services.AddSingleton<PomodoroService>();
                    services.AddSingleton<SearchService>();
                    services.AddSingleton<Peak.Core.Theming.ThemeService>();
                    services.AddSingleton<WidgetRegistry>();
                    services.AddSingleton<IslandViewModel>();
                    services.AddSingleton<IslandWindow>();
                    services.AddSingleton<Plugins.IslandHost>();
                    services.AddSingleton<UpdateService>();
                    services.AddHttpClient("Weather");
                    services.AddHttpClient("Update");
                })
                .Build();

            _logger = _host.Services.GetRequiredService<ILogger<App>>();

            // Load settings
            var settingsManager = _host.Services.GetRequiredService<SettingsManager>();
            settingsManager.Load();

            // Apply theme colors from settings
            UpdateThemeColors(settingsManager.Settings.IslandBackground, settingsManager.Settings.AccentColor);

            // Register built-in widgets and load plugins
            var registry = _host.Services.GetRequiredService<WidgetRegistry>();
            RegisterBuiltInWidgets(registry);
            LoadPlugins(registry, settingsManager, _host.Services);

            // Initialize services (load slot layout before window shows)
            var viewModel = _host.Services.GetRequiredService<IslandViewModel>();
            await viewModel.InitializeAsync();

            // Start update check
            var updateService = _host.Services.GetRequiredService<UpdateService>();
            updateService.StartPeriodicCheck();

            // Build the Spotlight search index in the background so the first hotkey
            // press has something ready (or near-ready) to query.
            var searchService = _host.Services.GetRequiredService<SearchService>();
            searchService.Start();

            // Show island (slots are already loaded from settings)
            _islandWindow = _host.Services.GetRequiredService<IslandWindow>();
            _islandWindow.Show();

            // Wire plugin island-host and let integration plugins attach
            var islandHost = _host.Services.GetRequiredService<Plugins.IslandHost>();
            islandHost.AttachWindow(_islandWindow);
            islandHost.PluginLoader = _pluginLoader;
            _pluginLoader?.AttachIslandIntegrations(islandHost);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup failed:\n{ex}", "Peak Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Dispose host, plugins and window so <see cref="InitializeAppAsync"/> can
    /// build a fresh instance. Keeps the tray icon and the process alive.
    /// </summary>
    private void TeardownForReload()
    {
        try
        {
            var viewModel = _host?.Services.GetService<IslandViewModel>();
            viewModel?.Cleanup();
        }
        catch { }

        try { _pluginLoader?.DetachIslandIntegrations(); } catch { }
        try { _pluginLoader?.Dispose(); } catch { }
        _pluginLoader = null;

        try
        {
            if (_islandWindow != null)
            {
                _islandWindow.Close();
                _islandWindow = null;
            }
        }
        catch { }

        try { _host?.Dispose(); } catch { }
        _host = null;
    }

    /// <summary>
    /// Reload in-process: tear down all services and rebuild them.
    /// Much faster than a full process restart.
    /// </summary>
    private async void ReloadApplication()
    {
        try
        {
            TeardownForReload();
            // Give finalizers / native handles a tick to release.
            await Task.Delay(200);
            await InitializeAppAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Reload failed:\n{ex}", "Peak", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetupTrayIcon()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app-icon.ico"))!.Stream;
        _trayIcon = new TaskbarIcon
        {
            Icon = new DrawingIcon(iconStream),
            ToolTipText = "Peak",
            Visibility = Visibility.Visible
        };

        var menuStyle = (Style)FindResource("Win11MenuItem");
        var sepStyle = (Style)FindResource("Win11Separator");

        var contextMenu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)FindResource("Win11ContextMenu")
        };

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings", Style = menuStyle, Icon = MakeMenuIcon("IconGear") };
        settingsItem.Click += (_, _) => OpenSettings();
        contextMenu.Items.Add(settingsItem);

        var editLayoutItem = new System.Windows.Controls.MenuItem { Header = "Edit Layout", Style = menuStyle, Icon = MakeMenuIcon("IconPen") };
        editLayoutItem.Click += (_, _) =>
        {
            var vm = _host?.Services.GetRequiredService<IslandViewModel>();
            vm?.ToggleEditModeCommand.Execute(null);
        };
        contextMenu.Items.Add(editLayoutItem);

        var hideItem = new System.Windows.Controls.MenuItem { Header = "Hide Island", Style = menuStyle, Icon = MakeMenuIcon("IconEyeSlash") };
        hideItem.Click += (_, _) =>
        {
            var vm = _host?.Services.GetRequiredService<IslandViewModel>();
            vm?.HideIsland();
        };
        contextMenu.Items.Add(hideItem);

        var toggleItem = new System.Windows.Controls.MenuItem { Header = "Toggle Visibility", Style = menuStyle, Icon = MakeMenuIcon("IconArrowsRotate") };
        toggleItem.Click += (_, _) => ToggleVisibility();
        contextMenu.Items.Add(toggleItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator { Style = sepStyle });

        var reloadItem = new System.Windows.Controls.MenuItem { Header = "Reload", Style = menuStyle, Icon = MakeMenuIcon("IconArrowsRotate") };
        reloadItem.Click += (_, _) => ReloadApplication();
        contextMenu.Items.Add(reloadItem);

        var restartItem = new System.Windows.Controls.MenuItem { Header = "Restart", Style = menuStyle, Icon = MakeMenuIcon("IconArrowsRotate") };
        restartItem.Click += (_, _) => RestartApplication();
        contextMenu.Items.Add(restartItem);

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit", Style = menuStyle, Icon = MakeMenuIcon("IconXmark") };
        exitItem.Click += (_, _) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        var versionItem = new System.Windows.Controls.MenuItem
        {
            IsHitTestVisible = false,
            Focusable = false,
            IsEnabled = false,
            Style = menuStyle,
            Header = $"v{UpdateService.CurrentVersion}",
            Foreground = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            FontSize = 10,
        };
        // Override template to center text without icon
        versionItem.Template = CreateVersionTemplate();
        contextMenu.Items.Add(versionItem);

        _trayIcon.ContextMenu = contextMenu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => ToggleVisibility();
    }

    public static void UpdateThemeColors(string bgHex, string accentHex)
    {
        try
        {
            var bg = (Color)ColorConverter.ConvertFromString(bgHex);
            var accent = (Color)ColorConverter.ConvertFromString(accentHex);

            Current.Resources["IslandBackgroundBrush"] = new SolidColorBrush(bg);
            Current.Resources["AccentBrush"] = new SolidColorBrush(accent);
        }
        catch { /* invalid color string — keep defaults */ }
    }

    private static System.Windows.Controls.ControlTemplate CreateVersionTemplate()
    {
        var template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.MenuItem));
        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        border.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(0, 4, 0, 2));

        var text = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        text.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Header") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        text.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)));
        text.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 10.0);
        text.SetValue(System.Windows.Controls.TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, System.Windows.TextAlignment.Center);
        text.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, Current.FindResource("InterFont"));

        border.AppendChild(text);
        template.VisualTree = border;
        return template;
    }

    private static System.Windows.Shapes.Path MakeMenuIcon(string resourceKey)
    {
        var geometry = (System.Windows.Media.Geometry?)Current.TryFindResource(resourceKey);
        return new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            Stretch = System.Windows.Media.Stretch.Uniform,
            Width = 12,
            Height = 12
        };
    }

    private void OpenSettings()
    {
        var settingsWindow = new SettingsWindow(_host!.Services.GetRequiredService<SettingsManager>());
        settingsWindow.ShowDialog();
    }

    private void ToggleVisibility()
    {
        if (_islandWindow == null) return;
        if (_islandWindow.IsVisible)
            _islandWindow.Hide();
        else
            _islandWindow.Show();
    }

    private static void RegisterBuiltInWidgets(WidgetRegistry registry)
    {
        registry.Register("none", "None", _ => null);
        registry.Register("clock", "Clock", dc => new ClockWidget { DataContext = dc });
        registry.Register("weather", "Weather", dc => new WeatherWidget { DataContext = dc });
        registry.Register("media", "Media", dc => new MediaWidget { DataContext = dc });
        registry.Register("systemmonitor", "System Monitor", dc => new SystemMonitorWidget { DataContext = dc });
        registry.Register("calendar", "Calendar", dc => new CalendarWidget { DataContext = dc });
        registry.Register("timer", "Timer", dc => new TimerWidget { DataContext = dc });
        registry.Register("network", "Network", dc => new NetworkWidget { DataContext = dc });
        registry.Register("quickaccess", "Quick Access", dc => new QuickAccessWidget { DataContext = dc });
        registry.Register("clipboard", "Clipboard", dc => new ClipboardWidget { DataContext = dc });
        registry.Register("quicknotes", "Quick Notes", dc => new QuickNotesWidget { DataContext = dc });
        registry.Register("volumemixer", "Volume Mixer", dc => new VolumeMixerWidget { DataContext = dc });
        registry.Register("pomodoro", "Pomodoro", dc => new PomodoroWidget { DataContext = dc });
    }

    private void LoadPlugins(WidgetRegistry registry, SettingsManager settingsManager, IServiceProvider services)
    {
        var pluginsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Peak", "plugins");
        _pluginLoader = new PluginLoader(pluginsDir, services.GetRequiredService<ILogger<PluginLoader>>());

        try
        {
            var plugins = _pluginLoader.LoadAll(services, settingsManager.Settings.PluginSettings, settingsManager.Settings.DisabledPlugins);
            foreach (var plugin in plugins)
            {
                registry.Register(plugin.Id, plugin.Name, dc => plugin.CreateView(dc), isBuiltIn: false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Plugin loading failed");
        }
    }

    private void ExitApplication()
    {
        var viewModel = _host?.Services.GetRequiredService<IslandViewModel>();
        viewModel?.Cleanup();

        _pluginLoader?.DetachIslandIntegrations();
        _pluginLoader?.Dispose();
        _trayIcon?.Dispose();
        _host?.Dispose();
        try { SingleInstanceMutex.ReleaseMutex(); } catch { }
        Shutdown();
    }

    private void RestartApplication()
    {
        try
        {
            var exePath = Environment.ProcessPath
                          ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                MessageBox.Show("Could not determine executable path for restart.", "Peak", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Write a tiny .bat helper into temp that waits for our PID to exit,
            // then launches the exe. Running it via ShellExecute (UseShellExecute=true)
            // ensures it is fully detached from our process — it survives our shutdown
            // even if we are inside a job object.
            var currentPid = Environment.ProcessId;
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "peak_restart.log");
            var batPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"peak_restart_{Guid.NewGuid():N}.bat");
            var bat = $@"@echo off
echo [%date% %time%] restart helper starting, waiting for pid {currentPid} > ""{logPath}""
:waitloop
tasklist /FI ""PID eq {currentPid}"" 2>nul | find ""{currentPid}"" >nul
if not errorlevel 1 (
    ping -n 2 127.0.0.1 >nul
    goto waitloop
)
echo [%date% %time%] pid gone, starting exe >> ""{logPath}""
start """" ""{exePath}""
echo [%date% %time%] done >> ""{logPath}""
(goto) 2>nul & del ""%~f0""
";
            System.IO.File.WriteAllText(batPath, bat);

            // Clean shutdown of services first
            try { var viewModel = _host?.Services.GetService<IslandViewModel>(); viewModel?.Cleanup(); } catch { }
            try { _pluginLoader?.DetachIslandIntegrations(); } catch { }
            try { _pluginLoader?.Dispose(); } catch { }
            try { _islandWindow?.Close(); } catch { }
            try { _trayIcon?.Dispose(); } catch { }
            try { _host?.Dispose(); } catch { }

            // Release the single-instance mutex so the new process can acquire it
            try { SingleInstanceMutex.ReleaseMutex(); } catch { }

            // Launch helper detached via ShellExecute
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(psi);

            // Force-exit: plugin background threads and native handles can keep
            // Shutdown() from actually terminating the process, leaving the notch
            // visible. Environment.Exit guarantees the PID is gone so the helper
            // script can start the fresh instance.
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Restart failed:\n{ex.Message}", "Peak", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
