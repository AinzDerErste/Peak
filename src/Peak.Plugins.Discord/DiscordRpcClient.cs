using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Peak.Plugins.Discord;

/// <summary>
/// Minimal Discord RPC client over the local named pipe.
/// Supports: handshake, AUTHORIZE/AUTHENTICATE, voice event subscriptions.
/// </summary>
public class DiscordRpcClient : IDisposable
{
    // Discord RPC opcodes
    private const int OP_HANDSHAKE = 0;
    private const int OP_FRAME = 1;
    private const int OP_CLOSE = 2;
    private const int OP_PING = 3;
    private const int OP_PONG = 4;

    private readonly string _clientId;
    private readonly string? _clientSecret;
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private readonly HttpClient _http = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private TaskCompletionSource<bool>? _readyTcs;

    public string? AccessToken { get; set; }

    // State
    public string? CurrentUserId { get; private set; }
    public string? CurrentUserName { get; private set; }
    public string? CurrentUserAvatar { get; private set; }
    public string? CurrentChannelId { get; private set; }
    public Dictionary<string, VoiceParticipant> Participants { get; } = new();

    // Events
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action? VoiceChannelChanged;
    public event Action? ParticipantsChanged;
    public event Action<string, bool>? SpeakingChanged;
    public event Action<string>? TokenRefreshed;

    public DiscordRpcClient(string clientId, string? clientSecret = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_clientId))
        {
            DiscordLog.Write("ConnectAsync: ClientId is empty, aborting.");
            return false;
        }

        DiscordLog.Write($"ConnectAsync: attempting pipe connect (clientId={_clientId.Substring(0, Math.Min(6, _clientId.Length))}…)");
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var pipeName = $"discord-ipc-{i}";
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(500, ct).ConfigureAwait(false);
                _pipe = pipe;
                DiscordLog.Write($"ConnectAsync: connected to {pipeName}");
                break;
            }
            catch (Exception ex)
            {
                DiscordLog.Write($"ConnectAsync: pipe discord-ipc-{i} failed: {ex.Message}");
            }
        }

        if (_pipe == null || !_pipe.IsConnected)
        {
            DiscordLog.Write("ConnectAsync: no pipe available.");
            return false;
        }

        _cts = new CancellationTokenSource();
        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

        // Handshake
        var handshake = new JsonObject
        {
            ["v"] = 1,
            ["client_id"] = _clientId
        };
        DiscordLog.Write("ConnectAsync: sending handshake…");
        await SendFrameAsync(OP_HANDSHAKE, handshake.ToJsonString(), ct).ConfigureAwait(false);

        // Wait for READY dispatch
        try
        {
            using var reg = ct.Register(() => _readyTcs?.TrySetCanceled());
            var readyTask = _readyTcs.Task;
            var timeout = Task.Delay(5000, ct);
            var finished = await Task.WhenAny(readyTask, timeout).ConfigureAwait(false);
            if (finished != readyTask)
            {
                DiscordLog.Write("ConnectAsync: handshake READY timeout after 5s.");
                return false;
            }
            DiscordLog.Write("ConnectAsync: READY received.");
        }
        catch (Exception ex)
        {
            DiscordLog.Write($"ConnectAsync: waiting for READY failed: {ex.Message}");
            return false;
        }

        Connected?.Invoke();
        return true;
    }

    /// <summary>
    /// Starts the OAuth flow. If <see cref="AccessToken"/> is set, it is used directly.
    /// Otherwise AUTHORIZE is sent (Discord shows a consent popup) and the returned
    /// code is exchanged for a token via the REST API.
    /// </summary>
    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(AccessToken))
        {
            DiscordLog.Write("AuthenticateAsync: using cached access token.");
            try
            {
                await SendCommandAsync("AUTHENTICATE", new JsonObject { ["access_token"] = AccessToken }, ct, timeoutMs: 15000).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                DiscordLog.Write($"AuthenticateAsync: cached token failed ({ex.Message}), retrying with AUTHORIZE.");
                AccessToken = null;
            }
        }

        // Request an authorization code – Discord desktop client shows consent popup.
        var authArgs = new JsonObject
        {
            ["client_id"] = _clientId,
            ["scopes"] = new JsonArray("rpc", "rpc.voice.read", "identify")
        };
        DiscordLog.Write("AuthenticateAsync: sending AUTHORIZE command, waiting for user consent in Discord…");
        JsonNode? codeResp;
        try
        {
            // User may take a long time to click – allow up to 2 minutes.
            codeResp = await SendCommandAsync("AUTHORIZE", authArgs, ct, timeoutMs: 120_000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiscordLog.Write($"AuthenticateAsync: AUTHORIZE failed: {ex.Message}");
            throw;
        }

        var code = codeResp?["data"]?["code"]?.GetValue<string>();
        DiscordLog.Write($"AuthenticateAsync: AUTHORIZE response code length={code?.Length ?? 0}");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Discord AUTHORIZE returned no code.");

        // Exchange code → token (requires client_secret).
        if (string.IsNullOrEmpty(_clientSecret))
            throw new InvalidOperationException("Discord client_secret is required for token exchange.");

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("client_id", _clientId),
            new KeyValuePair<string,string>("client_secret", _clientSecret!),
            new KeyValuePair<string,string>("grant_type", "authorization_code"),
            new KeyValuePair<string,string>("code", code!),
            new KeyValuePair<string,string>("redirect_uri", "http://localhost")
        });
        DiscordLog.Write("AuthenticateAsync: exchanging code for access token…");
        var tokenResp = await _http.PostAsync("https://discord.com/api/oauth2/token", form, ct).ConfigureAwait(false);
        if (!tokenResp.IsSuccessStatusCode)
        {
            var body = await tokenResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            DiscordLog.Write($"AuthenticateAsync: token exchange HTTP {(int)tokenResp.StatusCode}: {body}");
            throw new InvalidOperationException($"Discord token exchange failed: {(int)tokenResp.StatusCode}");
        }
        var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        AccessToken = tokenJson.GetProperty("access_token").GetString();
        DiscordLog.Write("AuthenticateAsync: token obtained, firing TokenRefreshed.");
        TokenRefreshed?.Invoke(AccessToken!);

        await SendCommandAsync("AUTHENTICATE", new JsonObject { ["access_token"] = AccessToken }, ct, timeoutMs: 15000).ConfigureAwait(false);
        DiscordLog.Write("AuthenticateAsync: AUTHENTICATE complete.");
    }

    public async Task SubscribeVoiceEventsAsync(CancellationToken ct = default)
    {
        DiscordLog.Write("SubscribeVoiceEventsAsync: subscribing to voice events…");
        try
        {
            await SendCommandAsync("SUBSCRIBE", new JsonObject(), ct, evt: "VOICE_CHANNEL_SELECT", timeoutMs: 5000).ConfigureAwait(false);
            await RequestVoiceChannelAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiscordLog.Write($"SubscribeVoiceEventsAsync failed: {ex.Message}");
        }
    }

    public async Task RequestVoiceChannelAsync(CancellationToken ct = default)
    {
        try
        {
            await SendCommandAsync("GET_SELECTED_VOICE_CHANNEL", new JsonObject(), ct, timeoutMs: 5000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiscordLog.Write($"RequestVoiceChannelAsync failed: {ex.Message}");
        }
    }

    private async Task SubscribeChannelEventsAsync(string channelId, CancellationToken ct = default)
    {
        try
        {
            var args = new JsonObject { ["channel_id"] = channelId };
            await SendCommandAsync("SUBSCRIBE", (JsonObject)args.DeepClone(), ct, evt: "VOICE_STATE_CREATE", timeoutMs: 5000).ConfigureAwait(false);
            await SendCommandAsync("SUBSCRIBE", (JsonObject)args.DeepClone(), ct, evt: "VOICE_STATE_DELETE", timeoutMs: 5000).ConfigureAwait(false);
            await SendCommandAsync("SUBSCRIBE", (JsonObject)args.DeepClone(), ct, evt: "VOICE_STATE_UPDATE", timeoutMs: 5000).ConfigureAwait(false);
            await SendCommandAsync("SUBSCRIBE", (JsonObject)args.DeepClone(), ct, evt: "SPEAKING_START", timeoutMs: 5000).ConfigureAwait(false);
            await SendCommandAsync("SUBSCRIBE", (JsonObject)args.DeepClone(), ct, evt: "SPEAKING_STOP", timeoutMs: 5000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiscordLog.Write($"SubscribeChannelEventsAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a command and waits for the response frame that carries the same nonce.
    /// </summary>
    private async Task<JsonNode?> SendCommandAsync(string cmd, JsonObject args, CancellationToken ct, string? evt = null, int timeoutMs = 10_000)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var payload = new JsonObject
        {
            ["cmd"] = cmd,
            ["args"] = args,
            ["nonce"] = nonce
        };
        if (evt != null) payload["evt"] = evt;

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;

        DiscordLog.Write($"→ {cmd}{(evt != null ? $" evt={evt}" : "")} nonce={nonce.Substring(0, 8)}");
        try
        {
            await SendFrameAsync(OP_FRAME, payload.ToJsonString(), ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(nonce, out _);
            throw;
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var timeout = Task.Delay(timeoutMs, ct);
        var finished = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
        if (finished != tcs.Task)
        {
            _pending.TryRemove(nonce, out _);
            DiscordLog.Write($"✗ {cmd} nonce={nonce.Substring(0, 8)} TIMEOUT after {timeoutMs}ms");
            throw new TimeoutException($"Discord RPC command {cmd} timed out.");
        }

        var response = await tcs.Task.ConfigureAwait(false);
        // Surface RPC-level errors
        if (response?["evt"]?.GetValue<string>() == "ERROR")
        {
            var code = response["data"]?["code"]?.ToString();
            var msg = response["data"]?["message"]?.GetValue<string>() ?? "unknown error";
            DiscordLog.Write($"✗ {cmd} ERROR code={code} msg={msg}");
            throw new InvalidOperationException($"Discord RPC error ({code}): {msg}");
        }
        return response;
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private async Task SendFrameAsync(int op, string json, CancellationToken ct)
    {
        if (_pipe == null) return;
        var data = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.GetBytes(op).CopyTo(header, 0);
        BitConverter.GetBytes(data.Length).CopyTo(header, 4);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(header, ct).ConfigureAwait(false);
            await _pipe.WriteAsync(data, ct).ConfigureAwait(false);
            await _pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var header = new byte[8];
        try
        {
            while (!ct.IsCancellationRequested && _pipe != null && _pipe.IsConnected)
            {
                int read = await ReadExactAsync(_pipe, header, 8, ct).ConfigureAwait(false);
                if (read == 0) break;

                int op = BitConverter.ToInt32(header, 0);
                int len = BitConverter.ToInt32(header, 4);
                if (len <= 0 || len > 1_000_000) break;

                var buf = new byte[len];
                read = await ReadExactAsync(_pipe, buf, len, ct).ConfigureAwait(false);
                if (read < len) break;

                var text = Encoding.UTF8.GetString(buf);
                DiscordLog.Write($"← op={op} {(text.Length > 400 ? text.Substring(0, 400) + "…" : text)}");
                try { HandleMessage(op, text); }
                catch (Exception ex) { DiscordLog.Write($"Discord msg handler: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            DiscordLog.Write($"Discord read loop: {ex.Message}");
        }
        finally
        {
            // Fail all pending
            foreach (var kv in _pending)
                kv.Value.TrySetException(new IOException("Discord pipe closed."));
            _pending.Clear();
            Disconnected?.Invoke();
        }
    }

    private static async Task<int> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    private void HandleMessage(int op, string json)
    {
        var node = JsonNode.Parse(json);
        if (node == null) return;

        var cmd = node["cmd"]?.GetValue<string>();
        var evt = node["evt"]?.GetValue<string>();
        var nonce = node["nonce"]?.GetValue<string>();
        var data = node["data"];

        // Nonce-matched response → complete pending command
        if (!string.IsNullOrEmpty(nonce) && _pending.TryRemove(nonce, out var tcs))
        {
            tcs.TrySetResult(node);
            // Continue to also process side-effect fields below (AUTHENTICATE user data, etc.)
        }

        // Handshake READY (Discord sends this as cmd=DISPATCH evt=READY with no nonce)
        if (cmd == "DISPATCH" && evt == "READY")
        {
            var user = data?["user"];
            CurrentUserId = user?["id"]?.GetValue<string>();
            CurrentUserName = user?["username"]?.GetValue<string>();
            DiscordLog.Write($"READY user={CurrentUserName} id={CurrentUserId}");
            _readyTcs?.TrySetResult(true);
            return;
        }

        if (cmd == "AUTHENTICATE")
        {
            var user = data?["user"];
            if (user != null)
            {
                CurrentUserId = user["id"]?.GetValue<string>();
                CurrentUserName = user["username"]?.GetValue<string>();
                CurrentUserAvatar = user["avatar"]?.GetValue<string>();
                DiscordLog.Write($"AUTHENTICATE user={CurrentUserName}");
            }
            // kick off subscriptions once authenticated
            _ = SubscribeVoiceEventsAsync();
        }
        else if (cmd == "GET_SELECTED_VOICE_CHANNEL")
        {
            HandleVoiceChannel(data);
        }
        else if (cmd == "DISPATCH" && evt == "VOICE_CHANNEL_SELECT")
        {
            var newChannel = data?["channel_id"]?.GetValue<string>();
            if (newChannel != CurrentChannelId)
            {
                CurrentChannelId = newChannel;
                Participants.Clear();
                if (!string.IsNullOrEmpty(newChannel))
                    _ = SubscribeChannelEventsAsync(newChannel);
                VoiceChannelChanged?.Invoke();
                ParticipantsChanged?.Invoke();
                _ = RequestVoiceChannelAsync();
            }
        }
        else if (cmd == "DISPATCH" && (evt == "VOICE_STATE_CREATE" || evt == "VOICE_STATE_UPDATE"))
        {
            var p = ParseParticipant(data);
            if (p != null)
            {
                Participants[p.UserId] = p;
                ParticipantsChanged?.Invoke();
            }
        }
        else if (cmd == "DISPATCH" && evt == "VOICE_STATE_DELETE")
        {
            var uid = data?["user"]?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(uid) && Participants.Remove(uid))
                ParticipantsChanged?.Invoke();
        }
        else if (cmd == "DISPATCH" && evt == "SPEAKING_START")
        {
            var uid = data?["user_id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(uid)) SpeakingChanged?.Invoke(uid, true);
        }
        else if (cmd == "DISPATCH" && evt == "SPEAKING_STOP")
        {
            var uid = data?["user_id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(uid)) SpeakingChanged?.Invoke(uid, false);
        }
    }

    private void HandleVoiceChannel(JsonNode? data)
    {
        if (data == null || data is not JsonObject obj || !obj.ContainsKey("id"))
        {
            CurrentChannelId = null;
            Participants.Clear();
            VoiceChannelChanged?.Invoke();
            ParticipantsChanged?.Invoke();
            return;
        }

        var channelId = data["id"]?.GetValue<string>();
        if (channelId == null) return;

        CurrentChannelId = channelId;
        Participants.Clear();

        var voiceStates = data["voice_states"] as JsonArray;
        if (voiceStates != null)
        {
            foreach (var vs in voiceStates)
            {
                var p = ParseParticipant(vs);
                if (p != null) Participants[p.UserId] = p;
            }
        }

        _ = SubscribeChannelEventsAsync(channelId);
        VoiceChannelChanged?.Invoke();
        ParticipantsChanged?.Invoke();
    }

    private static VoiceParticipant? ParseParticipant(JsonNode? data)
    {
        var user = data?["user"];
        var id = user?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id)) return null;
        return new VoiceParticipant
        {
            UserId = id!,
            Username = user?["username"]?.GetValue<string>() ?? "",
            AvatarHash = user?["avatar"]?.GetValue<string>(),
            Discriminator = int.TryParse(user?["discriminator"]?.GetValue<string>(), out var d) ? d : 0
        };
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _http.Dispose();
    }
}

public class VoiceParticipant
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string? AvatarHash { get; set; }
    public int Discriminator { get; set; }
}

/// <summary>
/// Simple file logger — writes to %APPDATA%\Peak\plugins\discord\plugin.log.
/// </summary>
internal static class DiscordLog
{
    private static readonly object _lock = new();
    private static readonly string _path;

    static DiscordLog()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Peak", "plugins", "discord");
        try { Directory.CreateDirectory(dir); } catch { }
        _path = Path.Combine(dir, "plugin.log");
    }

    public static void Write(string line)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
