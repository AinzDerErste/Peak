using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Peak.Plugin.Sdk;

namespace Peak.Plugins.Discord;

public class DiscordPlugin : IWidgetPlugin, IIslandIntegrationPlugin, IPluginSettingsProvider
{
    public string Id => "peak.plugins.discord";
    public string Name => "Discord";
    public string Description => "Shows Discord call participants and active speaker.";
    public string Icon => "\uD83C\uDFA7";

    private DiscordPluginSettings _settings = new();
    private DiscordRpcClient? _rpc;
    private AvatarCache? _avatars;
    private IIslandHost? _host;
    private CancellationTokenSource? _reconnectCts;
    private readonly HashSet<string> _speakingNow = new();

    // Incoming call state
    private string? _incomingCallChannelId;
    private CancellationTokenSource? _callDismissCts;

    public void Initialize(IServiceProvider services)
    {
        _avatars = new AvatarCache();
    }

    public void LoadSettings(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object }) return;
        try
        {
            _settings = JsonSerializer.Deserialize<DiscordPluginSettings>(settings.Value.GetRawText()) ?? new();
        }
        catch { _settings = new(); }
    }

    public JsonElement? SaveSettings()
    {
        var json = JsonSerializer.SerializeToElement(_settings);
        return json;
    }

    public FrameworkElement CreateView(object dataContext)
    {
        // A small in-notch status widget showing participant avatars
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = "Discord plugin ready",
            Foreground = Brushes.White,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(label);
        return panel;
    }

    public void OnActivate() { }
    public void OnDeactivate() { }

    // ─── IPluginSettingsProvider ─────────────────────────────

    public IReadOnlyList<PluginSettingField> GetSettingsSchema() => new[]
    {
        new PluginSettingField
        {
            Key = "ClientId",
            Label = "Client ID",
            Description = "Discord Application Client ID (discord.com/developers → Applications)",
            Kind = PluginSettingFieldKind.Text,
            CurrentValue = _settings.ClientId,
            Placeholder = "e.g. 1234567890123456789"
        },
        new PluginSettingField
        {
            Key = "ClientSecret",
            Label = "Client Secret",
            Description = "OAuth2 Client Secret — used to exchange the authorization code for an access token.",
            Kind = PluginSettingFieldKind.Password,
            CurrentValue = _settings.ClientSecret,
            Placeholder = "Keep secret"
        }
    };

    public void SetSettingValue(string key, string? value)
    {
        switch (key)
        {
            case "ClientId":
                if (_settings.ClientId != (value ?? ""))
                {
                    _settings.ClientId = value ?? "";
                    _settings.AccessToken = null; // re-auth next run
                }
                break;
            case "ClientSecret":
                _settings.ClientSecret = string.IsNullOrWhiteSpace(value) ? null : value;
                break;
        }
    }

    // ─── IIslandIntegrationPlugin ─────────────────────────────

    public void AttachToIsland(IIslandHost host)
    {
        _host = host;

        // Register collapsed renderer for the DiscordCallCount slot
        host.SetCollapsedRenderer(kind =>
        {
            if (kind != CollapsedWidgetKind.DiscordCallCount && kind != CollapsedWidgetKind.VoiceCallCount) return null;
            // Only render when actively in a call with participants
            if (_rpc == null || string.IsNullOrEmpty(_rpc.CurrentChannelId)) return null;
            if (_rpc.Participants.Count == 0) return null;
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
        _rpc?.Dispose();
        _rpc = null;
        _host = null;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        DiscordLog.Write($"ConnectLoop: starting (clientId set={!string.IsNullOrWhiteSpace(_settings.ClientId)}, secret set={!string.IsNullOrWhiteSpace(_settings.ClientSecret)}, token cached={!string.IsNullOrWhiteSpace(_settings.AccessToken)})");
        while (!ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(_settings.ClientId))
            {
                DiscordLog.Write("ConnectLoop: no ClientId yet, waiting…");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                _rpc = new DiscordRpcClient(_settings.ClientId, _settings.ClientSecret)
                {
                    AccessToken = _settings.AccessToken
                };
                _rpc.Connected += OnRpcConnected;
                _rpc.Disconnected += OnRpcDisconnected;
                _rpc.VoiceChannelChanged += OnVoiceChannelChanged;
                _rpc.ParticipantsChanged += OnParticipantsChanged;
                _rpc.SpeakingChanged += OnSpeakingChanged;
                _rpc.TokenRefreshed += OnTokenRefreshed;
                _rpc.IncomingCallDetected += OnIncomingCall;
                _rpc.IncomingCallDismissed += OnIncomingCallDismissed;

                if (await _rpc.ConnectAsync(ct).ConfigureAwait(false))
                {
                    try { await _rpc.AuthenticateAsync(ct).ConfigureAwait(false); }
                    catch (Exception ex) { DiscordLog.Write($"Discord auth failed: {ex.Message}"); }

                    // Wait while connected
                    while (_rpc.IsConnected && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                }
                else
                {
                    DiscordLog.Write("ConnectLoop: ConnectAsync returned false.");
                }
            }
            catch (Exception ex)
            {
                DiscordLog.Write($"ConnectLoop error: {ex.Message}");
            }
            finally
            {
                try { _rpc?.Dispose(); } catch { }
                _rpc = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch { break; }
        }
        DiscordLog.Write("ConnectLoop: exiting.");
    }

    // ─── Event handlers ─────────────────────────────

    private void OnRpcConnected() { }
    private void OnRpcDisconnected()
    {
        _host?.SetViewModelProperty("DiscordCallCount", 0);
        _host?.SetViewModelProperty("DiscordCallCountDisplay", "—");
        _host?.SetVisualizerOverride(null);
        _host?.RefreshCollapsedSlots();
    }

    private void OnVoiceChannelChanged()
    {
        if (_rpc == null) return;
        _speakingNow.Clear();

        // If we joined a channel, dismiss any active incoming call overlay
        if (!string.IsNullOrEmpty(_rpc.CurrentChannelId))
            DismissCallOverlay();

        if (string.IsNullOrEmpty(_rpc.CurrentChannelId))
        {
            // Left a call → drop the override entirely, back to the audio visualizer.
            _host?.SetVisualizerOverride(null);
        }
        else
        {
            // Joined a call but nobody speaking yet → idle placeholder.
            ShowIdle();
        }
        _host?.RefreshCollapsedSlots();
    }

    private void OnParticipantsChanged()
    {
        if (_rpc == null) return;
        int count = _rpc.Participants.Count;
        _host?.SetViewModelProperty("DiscordCallCount", count);
        _host?.SetViewModelProperty("DiscordCallCountDisplay", count > 0 ? count.ToString() : "—");
        _host?.RefreshCollapsedSlots();
    }

    private void OnSpeakingChanged(string userId, bool speaking)
    {
        if (_rpc == null || string.IsNullOrEmpty(userId)) return;

        if (speaking)
        {
            _speakingNow.Add(userId);
            if (_rpc.Participants.TryGetValue(userId, out var p))
                _ = ShowAvatarAsync(userId, p.AvatarHash, p.Discriminator);
        }
        else
        {
            _speakingNow.Remove(userId);
            if (_speakingNow.Count == 0)
            {
                // No one is speaking → idle placeholder (not the last avatar).
                if (!string.IsNullOrEmpty(_rpc.CurrentChannelId))
                    ShowIdle();
                else
                    _host?.SetVisualizerOverride(null);
            }
            else
            {
                // Someone else is still speaking → show any of them.
                var other = _speakingNow.First();
                if (_rpc.Participants.TryGetValue(other, out var p))
                    _ = ShowAvatarAsync(other, p.AvatarHash, p.Discriminator);
            }
        }
    }

    // FontAwesome Free – phone solid, viewBox 0 0 512 512
    private const string PhonePath =
        "M164.9 24.6c-7.7-18.6-28-28.5-47.4-23.2l-88 24C12.1 30.2 0 46 0 64" +
        "C0 311.4 200.6 512 448 512c18 0 33.8-12.1 38.6-29.5l24-88" +
        "c5.3-19.4-4.6-39.7-23.2-47.4l-96-40c-16.3-6.8-35.2-2.1-46.3 11.6" +
        "L304.7 368C234.3 334.7 177.3 277.7 144 207.3L193.3 167" +
        "c13.7-11.1 18.4-30 11.6-46.3l-40-96z";

    // FontAwesome Free 7.2.0 – headphones, viewBox 0 0 640 640
    private const string HeadphonesPath =
        "M160 288C160 199.6 231.6 128 320 128C408.4 128 480 199.6 480 288L480 325.5" +
        "C470 322 459.2 320 448 320L432 320C405.5 320 384 341.5 384 368L384 496" +
        "C384 522.5 405.5 544 432 544L448 544C501 544 544 501 544 448L544 288" +
        "C544 164.3 443.7 64 320 64C196.3 64 96 164.3 96 288L96 448C96 501 139 544 192 544" +
        "L208 544C234.5 544 256 522.5 256 496L256 368C256 341.5 234.5 320 208 320L192 320" +
        "C180.8 320 170 321.9 160 325.5L160 288z";

    /// <summary>Neutral placeholder shown when nobody is currently speaking.</summary>
    private void ShowIdle()
    {
        if (_host == null) return;
        _host.UiDispatcher.Invoke(() =>
        {
            // Wrap the path in a Canvas that matches the FontAwesome 640×640 viewBox.
            // That way the Viewbox scales the full square (not the tight glyph bbox),
            // keeping the icon perfectly centered relative to the bubble.
            var geometry = Geometry.Parse(HeadphonesPath);
            var iconPath = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))
            };
            var canvas = new Canvas { Width = 640, Height = 640 };
            canvas.Children.Add(iconPath);

            var box = new Viewbox
            {
                Child = canvas,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(4)
            };
            var grid = new Grid();
            grid.Children.Add(box);
            _host?.SetVisualizerOverride(grid);
        });
    }

    private void OnTokenRefreshed(string token)
    {
        _settings.AccessToken = token;
        _settings.TokenExpiresAt = DateTime.UtcNow.AddDays(6);
        try { _host?.RequestSettingsSave(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Discord RequestSettingsSave: {ex.Message}"); }
    }

    private async Task ShowAvatarAsync(string? userId, string? avatarHash, int discriminator = 0)
    {
        if (_host == null || _avatars == null || string.IsNullOrEmpty(userId)) return;
        var img = await _avatars.GetAvatarAsync(userId, avatarHash, discriminator).ConfigureAwait(false);
        if (img == null) return;

        _host.UiDispatcher.Invoke(() =>
        {
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = 22,
                Height = 22,
                Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill }
            };
            _host?.SetVisualizerOverride(ellipse);
        });
    }

    // ─── Incoming Call ───────────────────────────────────────

    // FontAwesome Free – phone-slash solid, viewBox 0 0 640 512
    private const string PhoneSlashPath =
        "M228.9 24.6c-7.7-18.6-28-28.5-47.4-23.2l-88 24C76.1 30.2 64 46 64 64" +
        "c0 28.4 2.8 56.2 8.2 83L38.6 118.7c-12.5-12.5-32.8-12.5-45.3 0" +
        "s-12.5 32.8 0 45.3l544 544c12.5 12.5 32.8 12.5 45.3 0" +
        "s12.5-32.8 0-45.3L380.2 485.2C432.1 478.5 481.5 463.6 527.2 441.8" +
        "l-49.6-49.6c-39.8 20.4-83.3 33.8-129.2 39.1L257.3 339.2" +
        "c13.7-11.2 18.4-30 11.6-46.3l-40-96zM383.4 299.6L129.3 45.5" +
        "l.3-.2 39.3 94.3 40 96c6.8 16.3 2.1 35.2-11.6 46.3l-49.3 40.1" +
        "C177.3 391.7 234.3 448.7 304.7 483l39.6-40.4c13.7-11.1 30-14.4 46.3-7.6" +
        "l-7.2-135.4z";

    private void OnIncomingCall(string callerId, string callerName, string? callerAvatar, string channelId)
    {
        if (_host == null) return;
        _incomingCallChannelId = channelId;

        _host.UiDispatcher.Invoke(() =>
        {
            var overlay = BuildCallOverlay(callerId, callerName, callerAvatar, channelId);
            _host.SetCollapsedOverlay(overlay);
            _host.SetExpansionBlocked(true);
        });

        // Auto-dismiss after 30 seconds
        _callDismissCts?.Cancel();
        _callDismissCts = new CancellationTokenSource();
        var ct = _callDismissCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                DismissCallOverlay();
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnIncomingCallDismissed()
    {
        DismissCallOverlay();
    }

    private void DismissCallOverlay()
    {
        _callDismissCts?.Cancel();
        _incomingCallChannelId = null;
        _host?.UiDispatcher.Invoke(() =>
        {
            _host?.SetCollapsedOverlay(null);
            _host?.SetExpansionBlocked(false);
        });
    }

    private UIElement BuildCallOverlay(string callerId, string callerName, string? callerAvatar, string channelId)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Caller name
        var nameText = new TextBlock
        {
            Text = callerName,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MaxWidth = 90,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // "calling..." label
        var callingText = new TextBlock
        {
            Text = "ruft an",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        // Accept button (green phone)
        var acceptBtn = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3B, 0xA5, 0x5C)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 4, 0),
            Child = new Viewbox
            {
                Width = 12, Height = 12,
                Child = new Canvas
                {
                    Width = 512, Height = 512,
                    Children = { new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse(PhonePath),
                        Fill = Brushes.White,
                        Stretch = Stretch.Uniform
                    }}
                }
            }
        };
        acceptBtn.MouseLeftButtonUp += (_, _) =>
        {
            if (_rpc != null && !string.IsNullOrEmpty(channelId))
                _ = _rpc.AcceptCallAsync(channelId);
            DismissCallOverlay();
        };

        // Decline button (red phone-slash)
        var declineBtn = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xED, 0x42, 0x45)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new Viewbox
            {
                Width = 12, Height = 12,
                Child = new Canvas
                {
                    Width = 640, Height = 512,
                    Children = { new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse(PhoneSlashPath),
                        Fill = Brushes.White,
                        Stretch = Stretch.Uniform
                    }}
                }
            }
        };
        declineBtn.MouseLeftButtonUp += (_, _) =>
        {
            DismissCallOverlay();
        };

        panel.Children.Add(nameText);
        panel.Children.Add(callingText);
        panel.Children.Add(acceptBtn);
        panel.Children.Add(declineBtn);

        return panel;
    }

    // ─── Collapsed Widget ───────────────────────────────────

    private FrameworkElement BuildCallCountUi()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var iconPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(PhonePath),
            Fill = Brushes.White,
            Stretch = Stretch.Uniform
        };
        var iconCanvas = new Canvas { Width = 512, Height = 512 };
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
        count.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DiscordCallCountDisplay") { Source = _host?.ViewModel });
        panel.Children.Add(icon);
        panel.Children.Add(count);
        return panel;
    }
}
