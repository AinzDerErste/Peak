using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Peak.Plugins.TeamSpeak;

/// <summary>
/// WebSocket client for the TeamSpeak 6 Remote Applications API.
/// Connects to the local TS6 client, authenticates, and receives voice events.
/// </summary>
public class TeamSpeakWsClient : IDisposable
{
    private readonly int _port;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    /// <summary>API key received from TS6 after first auth. Reuse for subsequent connections.</summary>
    public string? ApiKey { get; set; }

    // State: servers → channels → clients
    // We track the channel the local user is in + all clients in that channel.
    public string? CurrentChannelId { get; private set; }
    public string? CurrentServerConnectionId { get; private set; }
    public string? LocalClientId { get; private set; }
    public ConcurrentDictionary<string, TsParticipant> Participants { get; } = new();

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    // Events
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action? VoiceChannelChanged;
    public event Action? ParticipantsChanged;
    public event Action<string, bool>? SpeakingChanged;
    public event Action<string>? ApiKeyReceived;

    public TeamSpeakWsClient(int port = 5899)
    {
        _port = port;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        TeamSpeakLog.Write($"ConnectAsync: attempting ws://localhost:{_port}");
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri($"ws://localhost:{_port}"), ct).ConfigureAwait(false);
            TeamSpeakLog.Write("ConnectAsync: WebSocket connected.");

            _cts = new CancellationTokenSource();
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

            return true;
        }
        catch (Exception ex)
        {
            TeamSpeakLog.Write($"ConnectAsync failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends the auth payload. If ApiKey is set, it will be included for automatic re-auth.
    /// Otherwise TS6 will prompt the user to approve the application.
    /// </summary>
    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["type"] = "auth",
            ["payload"] = new JsonObject
            {
                ["identifier"] = "com.peaknotch.teamspeak",
                ["version"] = "1.0.0",
                ["name"] = "Peak Notch",
                ["description"] = "Peak Notch TeamSpeak Integration — voice channel display",
                ["content"] = new JsonObject
                {
                    ["apiKey"] = ApiKey ?? ""
                }
            }
        };

        TeamSpeakLog.Write($"AuthenticateAsync: sending auth (apiKey set={!string.IsNullOrEmpty(ApiKey)})");
        await SendAsync(payload.ToJsonString(), ct).ConfigureAwait(false);
    }

    private async Task SendAsync(string json, CancellationToken ct)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        TeamSpeakLog.Write($"-> {(json.Length > 400 ? json[..400] + "..." : json)}");
    }

    /// <summary>Sends a keyPress event to the TS6 client (for custom hotkey support).</summary>
    public async Task SendKeyPressAsync(string button, bool state, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["type"] = "keyPress",
            ["payload"] = new JsonObject
            {
                ["button"] = button,
                ["state"] = state
            }
        };
        await SendAsync(payload.ToJsonString(), ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16384];
        var sb = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        TeamSpeakLog.Write("ReceiveLoop: server closed connection.");
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var text = sb.ToString();
                TeamSpeakLog.Write($"<- {(text.Length > 1200 ? text[..1200] + "..." : text)}");

                try { HandleMessage(text); }
                catch (Exception ex) { TeamSpeakLog.Write($"HandleMessage error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TeamSpeakLog.Write($"ReceiveLoop error: {ex.Message}");
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    /// <summary>Safely reads a JSON value as string, handling both string and number types.</summary>
    private static string? GetString(JsonNode? node)
    {
        if (node == null) return null;
        try { return node.GetValue<string>(); } catch { }
        try { return node.GetValue<int>().ToString(); } catch { }
        try { return node.GetValue<long>().ToString(); } catch { }
        try { return node.ToString(); } catch { }
        return null;
    }

    private void HandleMessage(string json)
    {
        var node = JsonNode.Parse(json);
        if (node == null) return;

        var type = node["type"]?.GetValue<string>();
        var payload = node["payload"];
        var status = node["status"];
        var statusCode = status?["code"]?.GetValue<int>() ?? -1;

        switch (type)
        {
            case "auth":
                HandleAuth(payload, statusCode);
                break;

            case "clientMoved":
            case "clientEnteredView":
            case "clientLeftView":
            case "clientUpdated":
                HandleClientEvent(type, payload);
                break;

            case "talkStatusChanged":
            case "talkStatusChange":
                HandleTalkStatus(payload);
                break;

            case "clientSelfPropertyUpdated":
                HandleSelfPropertyUpdated(payload);
                break;

            case "channelEdited":
            case "channelCreated":
            case "channelDeleted":
            case "channelMoved":
                TeamSpeakLog.Write($"Channel event: {type}");
                break;

            case "connectStatusChanged":
                HandleConnectStatus(payload);
                break;

            default:
                TeamSpeakLog.Write($"Unhandled event type: {type}");
                break;
        }
    }

    private void HandleAuth(JsonNode? payload, int statusCode)
    {
        if (statusCode != 0)
        {
            TeamSpeakLog.Write($"Auth failed: status code {statusCode}");
            return;
        }

        // Extract and persist the API key
        var newApiKey = payload?["apiKey"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(newApiKey))
        {
            ApiKey = newApiKey;
            ApiKeyReceived?.Invoke(newApiKey);
            TeamSpeakLog.Write($"Auth success: API key received ({newApiKey[..Math.Min(8, newApiKey.Length)]}...)");
        }

        // The auth response includes the full server state.
        // Parse connected servers, channels, and clients to build initial state.
        ParseInitialState(payload);
        Connected?.Invoke();
    }

    private void ParseInitialState(JsonNode? payload)
    {
        if (payload == null) return;

        // The TS6 API provides complete server info after auth.
        // Look for "connections" or similar top-level keys.
        // Structure (expected from TS6 docs): servers with channels and clients.

        // Try "connections" array (common TS6 pattern)
        var connections = payload["connections"] as JsonArray;
        if (connections == null)
        {
            // Maybe it's nested differently — try other known patterns
            var servers = payload["servers"] as JsonArray;
            if (servers != null) connections = servers;
        }

        if (connections == null)
        {
            TeamSpeakLog.Write("ParseInitialState: no connections/servers found in auth payload.");
            // Try to extract any client/channel info from the flat payload
            TryParseFlat(payload);
            return;
        }

        foreach (var conn in connections)
        {
            if (conn == null) continue;

            var connId = GetString(conn["id"]) ?? GetString(conn["connectionId"]);
            if (connId == null) continue;

            CurrentServerConnectionId = connId;

            // Find local client
            var ownClient = GetString(conn["clientId"]) ?? GetString(conn["ownClientId"]);
            if (!string.IsNullOrEmpty(ownClient))
                LocalClientId = ownClient;

            // TS6 structure: clients are in "clientInfos" (NOT inside channels!)
            var clientInfos = conn["clientInfos"];
            if (clientInfos != null)
                ParseClientInfos(clientInfos);

            // Fallback: try "clients" array directly
            var clients = conn["clients"] as JsonArray;
            if (clients != null)
            {
                foreach (var client in clients)
                    ParseSingleClient(client);
            }

            // Fallback: try channels with nested clients (older TS API?)
            if (clientInfos == null && clients == null)
            {
                var channelInfos = conn["channelInfos"]?["rootChannels"] as JsonArray;
                if (channelInfos != null)
                {
                    var extracted = ExtractAllClients(channelInfos);
                    foreach (var c in extracted)
                        ParseSingleClient(c);
                }
            }
        }

        // Filter participants to only those in our channel
        if (!string.IsNullOrEmpty(CurrentChannelId))
        {
            var toRemove = Participants.Where(kv => kv.Value.ChannelId != CurrentChannelId).Select(kv => kv.Key).ToList();
            foreach (var key in toRemove)
                Participants.TryRemove(key, out _);
        }

        TeamSpeakLog.Write($"InitialState: server={CurrentServerConnectionId}, channel={CurrentChannelId}, localClient={LocalClientId}, participants={Participants.Count}");
        VoiceChannelChanged?.Invoke();
        ParticipantsChanged?.Invoke();
    }

    /// <summary>Parse the "clientInfos" node from TS6 auth response. Can be an array or object keyed by clientId.</summary>
    private void ParseClientInfos(JsonNode clientInfos)
    {
        if (clientInfos is JsonArray arr)
        {
            foreach (var item in arr)
                ParseSingleClient(item);
        }
        else if (clientInfos is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                var client = kv.Value;
                if (client is JsonObject clientObj)
                {
                    if (!clientObj.ContainsKey("id") && !clientObj.ContainsKey("clientId"))
                        clientObj["id"] = kv.Key;
                    ParseSingleClient(client);
                }
                else if (client is JsonArray clientArr)
                {
                    foreach (var c in clientArr)
                        ParseSingleClient(c);
                }
            }
        }
    }

    /// <summary>Parse a single client node and add to Participants.</summary>
    private void ParseSingleClient(JsonNode? client)
    {
        if (client == null) return;

        var clientId = GetString(client["id"]) ?? GetString(client["clientId"]);
        var channelId = GetString(client["channelId"]) ?? GetString(client["channel"]);
        var name = GetString(client["nickname"]) ?? GetString(client["name"])
                ?? GetString(client["properties"]?["nickname"])
                ?? GetString(client["properties"]?["name"])
                ?? "Unknown";

        if (string.IsNullOrEmpty(channelId))
            channelId = GetString(client["properties"]?["channelId"]);

        if (clientId == LocalClientId && !string.IsNullOrEmpty(channelId))
            CurrentChannelId = channelId;

        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(channelId))
        {
            Participants[clientId] = new TsParticipant
            {
                ClientId = clientId,
                Nickname = name,
                ChannelId = channelId
            };
        }
    }

    /// <summary>Recursively extract all clients from TS6's nested channel tree.</summary>
    private static JsonArray ExtractAllClients(JsonArray channels)
    {
        var result = new JsonArray();
        foreach (var ch in channels)
        {
            if (ch == null) continue;
            var channelId = GetString(ch["id"]);
            var clients = ch["clients"] as JsonArray;

            if (clients != null)
            {
                foreach (var c in clients)
                {
                    if (c == null) continue;
                    // Inject channelId into the client node so we know which channel they're in
                    if (c is JsonObject obj && !obj.ContainsKey("channelId") && channelId != null)
                        obj["channelId"] = channelId;
                    result.Add(c.DeepClone());
                }
            }
            // Recurse into subChannels
            var subChannels = ch["subChannels"] as JsonArray;
            if (subChannels != null)
            {
                var sub = ExtractAllClients(subChannels);
                foreach (var s in sub)
                    if (s != null) result.Add(s.DeepClone());
            }
        }
        return result;
    }

    private void TryParseFlat(JsonNode? payload)
    {
        // Fallback: try to get any useful info from a flat structure
        var clientId = GetString(payload?["clientId"]);
        if (!string.IsNullOrEmpty(clientId))
            LocalClientId = clientId;

        var channelId = GetString(payload?["channelId"]);
        if (!string.IsNullOrEmpty(channelId))
        {
            CurrentChannelId = channelId;
            VoiceChannelChanged?.Invoke();
        }
    }

    private void HandleClientEvent(string eventType, JsonNode? payload)
    {
        if (payload == null) return;

        var clientId = GetString(payload["clientId"]) ?? GetString(payload["id"]);
        var channelId = GetString(payload["channelId"]) ?? GetString(payload["targetChannelId"]);
        var nickname = GetString(payload["nickname"]) ?? GetString(payload["name"]);

        if (string.IsNullOrEmpty(clientId)) return;

        switch (eventType)
        {
            case "clientEnteredView":
            case "clientUpdated":
                if (!string.IsNullOrEmpty(channelId))
                {
                    // Is this client in our channel?
                    if (channelId == CurrentChannelId)
                    {
                        Participants[clientId] = new TsParticipant
                        {
                            ClientId = clientId,
                            Nickname = nickname ?? Participants.GetValueOrDefault(clientId)?.Nickname ?? "Unknown",
                            ChannelId = channelId
                        };
                        ParticipantsChanged?.Invoke();
                    }
                    // Did our local client move to a new channel?
                    if (clientId == LocalClientId && channelId != CurrentChannelId)
                    {
                        var oldChannel = CurrentChannelId;
                        CurrentChannelId = channelId;
                        // Clear participants and rebuild for new channel
                        Participants.Clear();
                        Participants[clientId] = new TsParticipant
                        {
                            ClientId = clientId,
                            Nickname = nickname ?? "Me",
                            ChannelId = channelId
                        };
                        TeamSpeakLog.Write($"Local client moved: {oldChannel} -> {channelId}");
                        VoiceChannelChanged?.Invoke();
                        ParticipantsChanged?.Invoke();
                    }
                }
                break;

            case "clientMoved":
                if (!string.IsNullOrEmpty(channelId))
                {
                    if (clientId == LocalClientId)
                    {
                        // We moved channels
                        CurrentChannelId = channelId;
                        Participants.Clear();
                        Participants[clientId] = new TsParticipant
                        {
                            ClientId = clientId,
                            Nickname = nickname ?? "Me",
                            ChannelId = channelId
                        };
                        TeamSpeakLog.Write($"Local client moved to channel {channelId}");
                        VoiceChannelChanged?.Invoke();
                        ParticipantsChanged?.Invoke();
                    }
                    else if (channelId == CurrentChannelId)
                    {
                        // Someone moved INTO our channel
                        Participants[clientId] = new TsParticipant
                        {
                            ClientId = clientId,
                            Nickname = nickname ?? "Unknown",
                            ChannelId = channelId
                        };
                        ParticipantsChanged?.Invoke();
                    }
                    else if (Participants.TryRemove(clientId, out _))
                    {
                        // Someone moved OUT of our channel
                        ParticipantsChanged?.Invoke();
                    }
                }
                break;

            case "clientLeftView":
                if (Participants.TryRemove(clientId, out _))
                    ParticipantsChanged?.Invoke();

                if (clientId == LocalClientId)
                {
                    CurrentChannelId = null;
                    Participants.Clear();
                    VoiceChannelChanged?.Invoke();
                    ParticipantsChanged?.Invoke();
                }
                break;
        }
    }

    private void HandleTalkStatus(JsonNode? payload)
    {
        if (payload == null) return;

        var clientId = GetString(payload["clientId"]) ?? GetString(payload["id"]);
        int status = -1;
        try { status = payload?["status"]?.GetValue<int>() ?? -1; } catch { }
        try { if (status < 0) status = payload?["talkStatus"]?.GetValue<int>() ?? -1; } catch { }
        var talking = (bool?)null;
        try { talking = payload?["talking"]?.GetValue<bool>(); } catch { }

        if (string.IsNullOrEmpty(clientId)) return;

        bool isSpeaking;
        if (talking.HasValue)
            isSpeaking = talking.Value;
        else
            isSpeaking = status > 0; // 0 = not talking, 1 = talking, 2 = whispering

        SpeakingChanged?.Invoke(clientId, isSpeaking);
    }

    private void HandleSelfPropertyUpdated(JsonNode? payload)
    {
        if (payload == null) return;
        var flag = GetString(payload["flag"]);
        if (flag != "flagTalking") return;

        bool? newValue = null;
        try { newValue = payload["newValue"]?.GetValue<bool>(); } catch { }
        if (newValue == null) return;

        if (!string.IsNullOrEmpty(LocalClientId))
            SpeakingChanged?.Invoke(LocalClientId, newValue.Value);
    }

    private void HandleConnectStatus(JsonNode? payload)
    {
        var status = GetString(payload?["status"]) ?? GetString(payload?["connectionStatus"]);
        TeamSpeakLog.Write($"ConnectStatus: {status}");

        if (status is "disconnected" or "connectionLost")
        {
            CurrentChannelId = null;
            CurrentServerConnectionId = null;
            Participants.Clear();
            VoiceChannelChanged?.Invoke();
            ParticipantsChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _receiveLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try
        {
            if (_ws?.State == WebSocketState.Open)
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Plugin shutdown", CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(1));
        }
        catch { }
        try { _ws?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _ws = null;
    }
}

public class TsParticipant
{
    public string ClientId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string ChannelId { get; set; } = "";
}
