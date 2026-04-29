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
    public string? CurrentServerName { get; private set; }
    /// <summary>
    /// Stable cryptographic server identity (TS6 <c>serverUid</c>). Unlike
    /// <see cref="CurrentServerConnectionId"/> — which is a per-session local
    /// counter — this UID survives reconnects, so it's the right key for any
    /// per-server settings the user wants to persist (e.g. AFK channel list).
    /// </summary>
    public string? CurrentServerUid { get; private set; }
    public string? LocalClientId { get; private set; }
    public ConcurrentDictionary<string, TsParticipant> Participants { get; } = new();

    /// <summary>
    /// Flat map (channelId → channelName) of every channel on the currently-
    /// connected server. Populated during <c>ParseInitialState</c> from the
    /// channelInfos.rootChannels tree the TS6 API hands us at auth time.
    /// Cleared on disconnect. Used by the AFK-channel picker dialog.
    /// </summary>
    public ConcurrentDictionary<string, string> Channels { get; } = new();

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    // Events
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action? VoiceChannelChanged;
    public event Action? ParticipantsChanged;
    public event Action<string, bool>? SpeakingChanged;
    public event Action<string>? ApiKeyReceived;
    /// <summary>Fires whenever the <see cref="Channels"/> map is rebuilt (server connect / switch).</summary>
    public event Action? ChannelsUpdated;

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

        // Best-effort: every TS6 event that carries server identity (serverName,
        // serverUid) gets that captured here, regardless of message type. The
        // exact event order varies between cached-key and fresh-auth flows, so
        // doing it once centrally is more robust than scattering it across each
        // handler.
        TryCaptureServerIdentity(payload);

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
            case "clientChannelGroupChanged":
                break;

            case "connectStatusChanged":
                HandleConnectStatus(payload);
                break;

            // Standalone channel-tree push. Sent during cached-key auth flow
            // (when the big auth-reply payload is replaced by piecemeal events).
            // Without handling this, the Channels cache stays empty and the
            // AFK picker can't show anything.
            case "channels":
                HandleChannelsEvent(payload);
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
            // Empirically (TS6 v6.x) the auth-reply puts the server's display
            // name and stable UID inside conn.properties: name + uniqueIdentifier.
            CurrentServerName = GetString(conn["serverName"])
                                ?? GetString(conn["properties"]?["serverName"])
                                ?? GetString(conn["properties"]?["virtualserverName"])
                                ?? GetString(conn["properties"]?["name"])
                                ?? GetString(conn["info"]?["serverName"])
                                ?? GetString(conn["name"]);
            CurrentServerUid = GetString(conn["serverUid"])
                               ?? GetString(conn["properties"]?["serverUid"])
                               ?? GetString(conn["properties"]?["virtualserverUid"])
                               ?? GetString(conn["properties"]?["virtualserverUniqueIdentifier"])
                               ?? GetString(conn["properties"]?["uniqueIdentifier"])
                               ?? GetString(conn["uniqueIdentifier"])
                               ?? GetString(conn["info"]?["serverUid"]);

            // Find local client
            var ownClient = GetString(conn["clientId"]) ?? GetString(conn["ownClientId"]);
            if (!string.IsNullOrEmpty(ownClient))
                LocalClientId = ownClient;

            // Cache the full channel tree (id → name) so the AFK-picker dialog
            // can show every channel on the server, not just the current one.
            var channelTree = conn["channelInfos"]?["rootChannels"] as JsonArray;
            if (channelTree != null)
            {
                Channels.Clear();
                ExtractAllChannels(channelTree, Channels);
            }

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
                if (channelTree != null)
                {
                    var extracted = ExtractAllClients(channelTree);
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

        TeamSpeakLog.Write($"InitialState: server={CurrentServerConnectionId} \"{CurrentServerName}\" uid={(CurrentServerUid ?? "<none>")}, channel={CurrentChannelId}, localClient={LocalClientId}, participants={Participants.Count}, channels={Channels.Count}");
        ChannelsUpdated?.Invoke();
        VoiceChannelChanged?.Invoke();
        ParticipantsChanged?.Invoke();
    }

    /// <summary>
    /// Recursively walks TS6's channel tree and fills <paramref name="result"/>
    /// with (channelId, channelName) pairs. Mirrors <see cref="ExtractAllClients"/>
    /// but cares about the channels, not the people inside them.
    /// </summary>
    private static void ExtractAllChannels(JsonArray channels, ConcurrentDictionary<string, string> result)
    {
        foreach (var ch in channels)
        {
            if (ch == null) continue;
            var id = GetString(ch["id"]);
            var name = GetString(ch["name"])
                       ?? GetString(ch["properties"]?["name"])
                       ?? id;

            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                result[id] = name;

            // Recurse into subChannels.
            if (ch["subChannels"] is JsonArray subChannels)
                ExtractAllChannels(subChannels, result);
        }
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
        // TS6 uses "newChannelId"/"oldChannelId" for clientMoved events
        var newChannelId = GetString(payload["newChannelId"]) ?? GetString(payload["channelId"])
                        ?? GetString(payload["targetChannelId"]);
        var oldChannelId = GetString(payload["oldChannelId"]);
        var nickname = GetString(payload["nickname"]) ?? GetString(payload["name"])
                    ?? GetString(payload["properties"]?["nickname"]);

        if (string.IsNullOrEmpty(clientId)) return;

        TeamSpeakLog.Write($"ClientEvent: {eventType} client={clientId} newCh={newChannelId} oldCh={oldChannelId}");

        switch (eventType)
        {
            case "clientEnteredView":
            case "clientUpdated":
                if (!string.IsNullOrEmpty(newChannelId))
                {
                    if (newChannelId == CurrentChannelId)
                    {
                        Participants[clientId] = new TsParticipant
                        {
                            ClientId = clientId,
                            Nickname = nickname ?? Participants.GetValueOrDefault(clientId)?.Nickname ?? "Unknown",
                            ChannelId = newChannelId
                        };
                        ParticipantsChanged?.Invoke();
                    }
                    if (clientId == LocalClientId && newChannelId != CurrentChannelId)
                    {
                        CurrentChannelId = newChannelId;
                        Participants.Clear();
                        Participants[clientId] = new TsParticipant
                        {
                            ClientId = clientId,
                            Nickname = nickname ?? "Me",
                            ChannelId = newChannelId
                        };
                        VoiceChannelChanged?.Invoke();
                        ParticipantsChanged?.Invoke();
                    }
                }
                break;

            case "clientMoved":
                if (!string.IsNullOrEmpty(newChannelId))
                {
                    if (clientId == LocalClientId)
                    {
                        CurrentChannelId = newChannelId;
                        Participants.Clear();
                        Participants[clientId] = new TsParticipant
                        {
                            ClientId = clientId,
                            Nickname = nickname ?? "Me",
                            ChannelId = newChannelId
                        };
                        TeamSpeakLog.Write($"Local client moved to channel {newChannelId}");
                        VoiceChannelChanged?.Invoke();
                        ParticipantsChanged?.Invoke();
                    }
                    else if (newChannelId == CurrentChannelId)
                    {
                        // Someone moved INTO our channel
                        Participants[clientId] = new TsParticipant
                        {
                            ClientId = clientId,
                            Nickname = nickname ?? "Unknown",
                            ChannelId = newChannelId
                        };
                        ParticipantsChanged?.Invoke();
                    }
                    else if (oldChannelId == CurrentChannelId && Participants.TryRemove(clientId, out _))
                    {
                        // Someone moved OUT of our channel
                        TeamSpeakLog.Write($"Client {clientId} left our channel ({oldChannelId} -> {newChannelId}), participants={Participants.Count}");
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

        // TS6 emits status="2" once it has the basic server info (clientId,
        // serverName, serverUid). Capture those so the AFK picker has a real
        // server identity to attach the channel list to even on cached-key
        // reconnects (where the big auth payload doesn't arrive).
        if (status == "2")
        {
            var connId = GetString(payload?["connectionId"]);
            if (!string.IsNullOrEmpty(connId)) CurrentServerConnectionId = connId;

            var info = payload?["info"];
            var name = GetString(info?["serverName"]);
            if (!string.IsNullOrEmpty(name)) CurrentServerName = name;
            var uid = GetString(info?["serverUid"]);
            if (!string.IsNullOrEmpty(uid)) CurrentServerUid = uid;
            var clientId = GetString(info?["clientId"]);
            if (!string.IsNullOrEmpty(clientId)) LocalClientId = clientId;
        }

        if (status is "disconnected" or "connectionLost" or "0")
        {
            CurrentChannelId = null;
            CurrentServerConnectionId = null;
            CurrentServerName = null;
            CurrentServerUid = null;
            Participants.Clear();
            Channels.Clear();
            VoiceChannelChanged?.Invoke();
            ParticipantsChanged?.Invoke();
            ChannelsUpdated?.Invoke();
        }
    }

    /// <summary>
    /// Pulls <c>serverName</c> / <c>serverUid</c> / <c>connectionId</c> out of
    /// any payload that happens to carry them — TS6 sprinkles this info across
    /// several event types (connectStatusChanged, channels, serverPropertiesUpdated,
    /// auth reply, …) and the order varies. This way whichever event arrives
    /// first sets the identity, later ones just reaffirm.
    /// </summary>
    private void TryCaptureServerIdentity(JsonNode? payload)
    {
        if (payload == null) return;

        var connId = GetString(payload["connectionId"]);
        if (!string.IsNullOrEmpty(connId)) CurrentServerConnectionId = connId;

        // Server name + UID can be at the top level OR nested in "info" / "properties"
        // depending on the event type. Try each location.
        var name = GetString(payload["serverName"])
                   ?? GetString(payload["info"]?["serverName"])
                   ?? GetString(payload["properties"]?["serverName"])
                   ?? GetString(payload["properties"]?["virtualserverName"]);
        if (!string.IsNullOrEmpty(name)) CurrentServerName = name;

        var uid = GetString(payload["serverUid"])
                  ?? GetString(payload["info"]?["serverUid"])
                  ?? GetString(payload["properties"]?["serverUid"])
                  ?? GetString(payload["properties"]?["virtualserverUid"])
                  ?? GetString(payload["properties"]?["uniqueIdentifier"]);
        if (!string.IsNullOrEmpty(uid)) CurrentServerUid = uid;
    }

    /// <summary>
    /// Handles the standalone <c>channels</c> push that TS6 sends after a
    /// cached-key auth (the channel tree never appears in the auth reply in
    /// that flow). Repopulates the in-memory <see cref="Channels"/> cache.
    /// </summary>
    private void HandleChannelsEvent(JsonNode? payload)
    {
        if (payload == null) return;

        var connId = GetString(payload["connectionId"]);
        if (!string.IsNullOrEmpty(connId)) CurrentServerConnectionId = connId;

        if (payload["info"]?["rootChannels"] is JsonArray roots)
        {
            Channels.Clear();
            ExtractAllChannels(roots, Channels);
            TeamSpeakLog.Write($"Channels event: cached {Channels.Count} channels for server {CurrentServerConnectionId}");
            ChannelsUpdated?.Invoke();
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
