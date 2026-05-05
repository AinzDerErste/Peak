using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Peak.Core.Models;

namespace Peak.Core.Services;

/// <summary>
/// Polls the system audio session manager for per-app volumes.
///
/// <para>Why <see cref="RefreshAsync"/> exists alongside <see cref="Refresh"/>:</para>
/// <para>
/// Enumerating sessions also reads <c>Process.MainModule.FileVersionInfo</c>
/// for the friendly app name — that touches the PE header on disk and can
/// take 5-50ms per process the first time. Multiplied by 8-15 audio sessions
/// every second on the UI thread, this was a major source of stutter when
/// the expanded state was visible. <see cref="RefreshAsync"/> moves the work
/// to a thread-pool thread, caches process-name lookups by PID, and only
/// fires <see cref="SessionsChanged"/> when something actually changed.
/// </para>
/// </summary>
public class VolumeMixerService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private readonly List<AudioSession> _sessions = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>PID → cached friendly name. Survives across refreshes;
    /// cleared when a PID is no longer in the session list (cheap LRU).</summary>
    private readonly Dictionary<uint, string> _nameCache = new();

    /// <summary>Snapshot used for change-detection; SessionsChanged fires
    /// only when this set differs from the freshly-polled one.</summary>
    private string _lastFingerprint = "";

    public event Action? SessionsChanged;

    public void Initialize()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            // No audio device available
        }
    }

    /// <summary>
    /// Background-thread refresh. Call from a 1-2s timer; returns immediately
    /// and dispatches <see cref="SessionsChanged"/> only if the session list
    /// actually changed (added/removed sessions, or volume/mute differs).
    /// </summary>
    public Task RefreshAsync() => Task.Run(RefreshCore);

    /// <summary>Synchronous version — kept for callers that aren't on a
    /// dispatcher (e.g. unit tests). Not for the polling timer.</summary>
    public void Refresh() => RefreshCore();

    private void RefreshCore()
    {
        if (_disposed || _device == null) return;

        var newSessions = new List<AudioSession>();
        var seenPids = new HashSet<uint>();

        try
        {
            // Per-app sessions
            var sessionManager = _device.AudioSessionManager;
            var sessions = sessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateExpired)
                    continue;

                var name = GetSessionName(session);
                if (string.IsNullOrEmpty(name)) continue;

                // Skip system services (AudioSrv, etc.)
                if (name.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("%SystemRoot%", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pid = session.GetProcessID;
                seenPids.Add(pid);

                newSessions.Add(new AudioSession
                {
                    Id = session.GetSessionIdentifier ?? $"session_{i}",
                    DisplayName = name,
                    Volume = session.SimpleAudioVolume.Volume,
                    IsMuted = session.SimpleAudioVolume.Mute,
                    ProcessId = pid
                });
            }
        }
        catch
        {
            // Audio subsystem can throw during device changes
        }

        // Build a cheap fingerprint that captures membership + volume/mute
        // changes without serializing the whole list. If unchanged, we don't
        // wake the UI for a no-op refresh — which is the common steady state.
        var fingerprint = BuildFingerprint(newSessions);

        bool changed;
        lock (_lock)
        {
            changed = fingerprint != _lastFingerprint;
            if (changed)
            {
                _sessions.Clear();
                _sessions.AddRange(newSessions);
                _lastFingerprint = fingerprint;

                // Drop name-cache entries for PIDs that disappeared so the
                // dict doesn't grow unbounded across long sessions.
                if (_nameCache.Count > seenPids.Count + 16)
                {
                    var stale = _nameCache.Keys.Where(k => !seenPids.Contains(k)).ToList();
                    foreach (var k in stale) _nameCache.Remove(k);
                }
            }
        }

        if (changed) SessionsChanged?.Invoke();
    }

    private static string BuildFingerprint(List<AudioSession> sessions)
    {
        // Stable order + ID + 2-decimal volume + mute. Volume rounding so we
        // don't churn UI on micro-drift from system mixer.
        var sb = new System.Text.StringBuilder(sessions.Count * 32);
        foreach (var s in sessions.OrderBy(x => x.Id, StringComparer.Ordinal))
            sb.Append(s.Id).Append('|').Append((int)(s.Volume * 100)).Append('|').Append(s.IsMuted ? 'M' : 'U').Append(';');
        return sb.ToString();
    }

    private string GetSessionName(AudioSessionControl session)
    {
        // Try display name first — free, no process lookup needed.
        var display = session.DisplayName;
        if (!string.IsNullOrWhiteSpace(display) && display != "@%SystemRoot%")
            return display;

        // Fall back to process name — cached because Process.MainModule
        // re-reads the PE header from disk every call. Cost is real on
        // browsers / electron apps.
        var pid = session.GetProcessID;
        if (pid == 0) return "";

        lock (_lock)
        {
            if (_nameCache.TryGetValue(pid, out var cached)) return cached;
        }

        string name = "";
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            var desc = proc.MainModule?.FileVersionInfo?.FileDescription;
            name = !string.IsNullOrWhiteSpace(desc) ? desc : proc.ProcessName;
        }
        catch
        {
            // process gone, access denied, etc. — leave name empty
        }

        lock (_lock)
        {
            _nameCache[pid] = name;
        }
        return name;
    }

    public IReadOnlyList<AudioSession> GetSessions()
    {
        lock (_lock)
            return _sessions.ToList().AsReadOnly();
    }

    public void SetVolume(string sessionId, float volume)
    {
        if (_device == null) return;
        volume = Math.Clamp(volume, 0f, 1f);

        try
        {
            var sessions = _device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].GetSessionIdentifier == sessionId)
                {
                    sessions[i].SimpleAudioVolume.Volume = volume;
                    return;
                }
            }
        }
        catch { }
    }

    public void ToggleMute(string sessionId)
    {
        if (_device == null) return;

        try
        {
            var sessions = _device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].GetSessionIdentifier == sessionId)
                {
                    var vol = sessions[i].SimpleAudioVolume;
                    vol.Mute = !vol.Mute;
                    return;
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _device?.Dispose(); } catch { }
        _device = null;
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
    }
}
