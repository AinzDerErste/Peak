using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Peak.Plugin.Sdk;

namespace Peak.Plugins.TeamSpeak;

public class TeamSpeakPlugin : IWidgetPlugin, IIslandIntegrationPlugin, IPluginSettingsProvider
{
    public string Id => "peak.plugins.teamspeak";
    public string Name => "TeamSpeak";
    public string Description => "Shows TeamSpeak 6 voice channel participants.";
    public string Icon => "\uD83C\uDF99\uFE0F"; // microphone emoji

    private TeamSpeakSettings _settings = new();
    private TeamSpeakWsClient? _client;
    private IIslandHost? _host;
    private CancellationTokenSource? _reconnectCts;
    private readonly HashSet<string> _speakingNow = new();

    public void Initialize(IServiceProvider services) { }

    public void LoadSettings(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object }) return;
        try
        {
            _settings = JsonSerializer.Deserialize<TeamSpeakSettings>(settings.Value.GetRawText()) ?? new();
        }
        catch { _settings = new(); }
    }

    public JsonElement? SaveSettings()
    {
        return JsonSerializer.SerializeToElement(_settings);
    }

    public FrameworkElement CreateView(object dataContext)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = "TeamSpeak plugin ready",
            Foreground = Brushes.White,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(label);
        return panel;
    }

    public void OnActivate() { }
    public void OnDeactivate() { }

    // --- IPluginSettingsProvider ---

    public IReadOnlyList<PluginSettingField> GetSettingsSchema() =>
    [
        new PluginSettingField
        {
            Key = "Port",
            Label = "Remote Apps Port",
            Description = "TeamSpeak 6 Remote Applications WebSocket port (default: 5899). Check TS6 Settings > Remote Apps.",
            Kind = PluginSettingFieldKind.Number,
            CurrentValue = _settings.Port.ToString(),
            Placeholder = "5899"
        }
    ];

    public void SetSettingValue(string key, string? value)
    {
        if (key == "Port" && int.TryParse(value, out var port) && port is > 0 and < 65536)
        {
            _settings.Port = port;
        }
    }

    // --- IIslandIntegrationPlugin ---

    public void AttachToIsland(IIslandHost host)
    {
        _host = host;

        // Register collapsed renderer for the TeamSpeakCallCount slot
        host.SetCollapsedRenderer(kind =>
        {
            if (kind != CollapsedWidgetKind.TeamSpeakCallCount) return null;
            if (_client == null || string.IsNullOrEmpty(_client.CurrentChannelId)) return null;
            if (_client.Participants.Count == 0) return null;
            return BuildCallCountUi();
        });

        _reconnectCts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoopAsync(_reconnectCts.Token));
    }

    public void DetachFromIsland()
    {
        try { _reconnectCts?.Cancel(); } catch { }
        _host?.SetCollapsedRenderer(null);
        _host?.SetVisualizerOverride(null);
        _client?.Dispose();
        _client = null;
        _host = null;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        TeamSpeakLog.Write($"ConnectLoop: starting (port={_settings.Port}, apiKey set={!string.IsNullOrEmpty(_settings.ApiKey)})");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _client = new TeamSpeakWsClient(_settings.Port)
                {
                    ApiKey = _settings.ApiKey
                };
                _client.Connected += OnConnected;
                _client.Disconnected += OnDisconnected;
                _client.VoiceChannelChanged += OnVoiceChannelChanged;
                _client.ParticipantsChanged += OnParticipantsChanged;
                _client.SpeakingChanged += OnSpeakingChanged;
                _client.ApiKeyReceived += OnApiKeyReceived;

                if (await _client.ConnectAsync(ct).ConfigureAwait(false))
                {
                    await _client.AuthenticateAsync(ct).ConfigureAwait(false);

                    // Wait while connected
                    while (_client.IsConnected && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                }
                else
                {
                    TeamSpeakLog.Write("ConnectLoop: ConnectAsync returned false.");
                }
            }
            catch (Exception ex)
            {
                TeamSpeakLog.Write($"ConnectLoop error: {ex.Message}");
            }
            finally
            {
                try { _client?.Dispose(); } catch { }
                _client = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch { break; }
        }
        TeamSpeakLog.Write("ConnectLoop: exiting.");
    }

    // --- Event Handlers ---

    private void OnConnected()
    {
        TeamSpeakLog.Write("Plugin: connected to TeamSpeak.");
    }

    private void OnDisconnected()
    {
        _speakingNow.Clear();
        _host?.SetViewModelProperty("TeamSpeakCallCount", 0);
        _host?.SetViewModelProperty("TeamSpeakCallCountDisplay", "");
        _host?.SetVisualizerOverride(null);
        _host?.RefreshCollapsedSlots();
    }

    private void OnVoiceChannelChanged()
    {
        if (_client == null) return;
        _speakingNow.Clear();

        if (string.IsNullOrEmpty(_client.CurrentChannelId))
        {
            _host?.SetVisualizerOverride(null);
        }
        else
        {
            ShowIdle();
        }
        _host?.RefreshCollapsedSlots();
    }

    private void OnParticipantsChanged()
    {
        if (_client == null) return;
        int count = _client.Participants.Count;
        _host?.SetViewModelProperty("TeamSpeakCallCount", count);
        _host?.SetViewModelProperty("TeamSpeakCallCountDisplay", count > 0 ? count.ToString() : "");
        _host?.RefreshCollapsedSlots();
    }

    private void OnSpeakingChanged(string clientId, bool speaking)
    {
        if (_client == null || string.IsNullOrEmpty(clientId)) return;

        if (speaking)
        {
            _speakingNow.Add(clientId);
            if (_client.Participants.TryGetValue(clientId, out var p))
                ShowSpeaker(p.Nickname);
        }
        else
        {
            _speakingNow.Remove(clientId);
            if (_speakingNow.Count == 0)
            {
                if (!string.IsNullOrEmpty(_client.CurrentChannelId))
                    ShowIdle();
                else
                    _host?.SetVisualizerOverride(null);
            }
            else
            {
                // Show another active speaker
                var other = _speakingNow.First();
                if (_client.Participants.TryGetValue(other, out var p))
                    ShowSpeaker(p.Nickname);
            }
        }
    }

    private void OnApiKeyReceived(string apiKey)
    {
        _settings.ApiKey = apiKey;
        try { _host?.RequestSettingsSave(); }
        catch (Exception ex) { TeamSpeakLog.Write($"RequestSettingsSave failed: {ex.Message}"); }
    }

    // --- UI Helpers ---

    // TeamSpeak logo path (simplified microphone icon, viewBox 0 0 384 512)
    // FontAwesome Free – microphone solid
    private const string MicrophonePath =
        "M192 0C139 0 96 43 96 96V256c0 53 43 96 96 96s96-43 96-96V96c0-53-43-96-96-96z" +
        "M64 216c0-13.3-10.7-24-24-24S16 202.7 16 216v40c0 89.1 66.2 162.7 152 174.4V464H120" +
        "c-13.3 0-24 10.7-24 24s10.7 24 24 24h144c13.3 0 24-10.7 24-24s-10.7-24-24-24H216V430.4" +
        "c85.8-11.7 152-85.3 152-174.4V216c0-13.3-10.7-24-24-24s-24 10.7-24 24v40c0 70.7-57.3 128-128 128" +
        "s-128-57.3-128-128V216z";

    // FontAwesome Free – headset, viewBox 0 0 512 512
    private const string HeadsetPath =
        "M256 48C141.1 48 48 141.1 48 256v40c0 13.3-10.7 24-24 24s-24-10.7-24-24V256" +
        "C0 114.6 114.6 0 256 0S512 114.6 512 256v40c0 13.3-10.7 24-24 24s-24-10.7-24-24V256" +
        "c0-114.9-93.1-208-208-208zM80 352c0-35.3 28.7-64 64-64h16c17.7 0 32 14.3 32 32v128" +
        "c0 17.7-14.3 32-32 32H144c-35.3 0-64-28.7-64-64V352zm288-64c35.3 0 64 28.7 64 64v64" +
        "c0 35.3-28.7 64-64 64h-16c-17.7 0-32-14.3-32-32V320c0-17.7 14.3-32 32-32h16z";

    /// <summary>Idle placeholder: headset icon when in channel but nobody speaking.</summary>
    private void ShowIdle()
    {
        if (_host == null) return;
        _host.UiDispatcher.Invoke(() =>
        {
            var geometry = Geometry.Parse(HeadsetPath);
            var iconPath = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))
            };
            var canvas = new Canvas { Width = 512, Height = 512 };
            canvas.Children.Add(iconPath);

            var box = new Viewbox
            {
                Child = canvas,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6)
            };
            var grid = new Grid();
            grid.Children.Add(box);
            _host?.SetVisualizerOverride(grid);
        });
    }

    /// <summary>Show the active speaker's name as initials in the bubble.</summary>
    private void ShowSpeaker(string nickname)
    {
        if (_host == null) return;
        _host.UiDispatcher.Invoke(() =>
        {
            // Show first 2 characters as initials
            var initials = nickname.Length >= 2
                ? nickname[..2].ToUpperInvariant()
                : nickname.ToUpperInvariant();

            var text = new TextBlock
            {
                Text = initials,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            var border = new Border
            {
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB3)), // TS blue
                Child = text,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _host?.SetVisualizerOverride(border);
        });
    }

    private FrameworkElement BuildCallCountUi()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(MicrophonePath),
            Fill = Brushes.White,
            Stretch = Stretch.Uniform
        };
        var iconCanvas = new Canvas { Width = 384, Height = 512 };
        iconCanvas.Children.Add(iconPath);
        var icon = new Viewbox
        {
            Child = iconCanvas,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };

        var count = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        count.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding("TeamSpeakCallCountDisplay") { Source = _host?.ViewModel });

        panel.Children.Add(icon);
        panel.Children.Add(count);
        return panel;
    }
}
