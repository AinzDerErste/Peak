using System.Windows;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using DrawingIcon = System.Drawing.Icon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    private TaskbarIcon? _trayIcon;
    private IslandWindow? _islandWindow;
    private PluginLoader? _pluginLoader;

    public IServiceProvider Services => _host!.Services;

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
                    services.AddSingleton<WidgetRegistry>();
                    services.AddSingleton<IslandViewModel>();
                    services.AddSingleton<IslandWindow>();
                    services.AddSingleton<UpdateService>();
                    services.AddHttpClient("Weather");
                    services.AddHttpClient("Update");
                })
                .Build();

            // Load settings
            var settingsManager = _host.Services.GetRequiredService<SettingsManager>();
            settingsManager.Load();

            // Apply theme colors from settings
            UpdateThemeColors(settingsManager.Settings.IslandBackground, settingsManager.Settings.AccentColor);

            // Register built-in widgets and load plugins
            var registry = _host.Services.GetRequiredService<WidgetRegistry>();
            RegisterBuiltInWidgets(registry);
            LoadPlugins(registry, settingsManager, _host.Services);

            // Setup tray icon
            SetupTrayIcon();

            // Initialize services (load slot layout before window shows)
            var viewModel = _host.Services.GetRequiredService<IslandViewModel>();
            await viewModel.InitializeAsync();

            // Start update check
            var updateService = _host.Services.GetRequiredService<UpdateService>();
            updateService.StartPeriodicCheck();

            // Show island (slots are already loaded from settings)
            _islandWindow = _host.Services.GetRequiredService<IslandWindow>();
            _islandWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup failed:\n{ex}", "Peak Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
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

        var contextMenu = new System.Windows.Controls.ContextMenu();

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => OpenSettings();
        contextMenu.Items.Add(settingsItem);

        var editLayoutItem = new System.Windows.Controls.MenuItem { Header = "Edit Layout" };
        editLayoutItem.Click += (_, _) =>
        {
            var vm = _host?.Services.GetRequiredService<IslandViewModel>();
            vm?.ToggleEditModeCommand.Execute(null);
        };
        contextMenu.Items.Add(editLayoutItem);

        var hideItem = new System.Windows.Controls.MenuItem { Header = "Hide Island" };
        hideItem.Click += (_, _) =>
        {
            var vm = _host?.Services.GetRequiredService<IslandViewModel>();
            vm?.HideIsland();
        };
        contextMenu.Items.Add(hideItem);

        var toggleItem = new System.Windows.Controls.MenuItem { Header = "Toggle Visibility" };
        toggleItem.Click += (_, _) => ToggleVisibility();
        contextMenu.Items.Add(toggleItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();
        contextMenu.Items.Add(exitItem);

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
    }

    private void LoadPlugins(WidgetRegistry registry, SettingsManager settingsManager, IServiceProvider services)
    {
        var pluginsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Peak", "plugins");
        _pluginLoader = new PluginLoader(pluginsDir);

        try
        {
            var plugins = _pluginLoader.LoadAll(services, settingsManager.Settings.PluginSettings);
            foreach (var plugin in plugins)
            {
                registry.Register(plugin.Id, plugin.Name, dc => plugin.CreateView(dc), isBuiltIn: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Plugin loading failed: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        var viewModel = _host?.Services.GetRequiredService<IslandViewModel>();
        viewModel?.Cleanup();

        _pluginLoader?.Dispose();
        _trayIcon?.Dispose();
        _host?.Dispose();
        SingleInstanceMutex.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
