using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Peak.Core.Models;

namespace Peak.Core.Services;

public class VolumeMixerService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private readonly List<AudioSession> _sessions = new();
    private readonly object _lock = new();
    private bool _disposed;

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
    /// Called from DispatcherTimer to refresh session list.
    /// </summary>
    public void Refresh()
    {
        if (_disposed || _device == null) return;

        var newSessions = new List<AudioSession>();

        try
        {
            // Master volume
            var vol = _device.AudioEndpointVolume;
            newSessions.Add(new AudioSession
            {
                Id = "master",
                DisplayName = "Master",
                Volume = vol.MasterVolumeLevelScalar,
                IsMuted = vol.Mute,
                IsMaster = true
            });

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

                // Filter system services (AudioSrv, etc.)
                if (name.Contains("AudioSrv", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("%SystemRoot%", StringComparison.OrdinalIgnoreCase))
                {
                    name = "System Sounds";
                }

                newSessions.Add(new AudioSession
                {
                    Id = session.GetSessionIdentifier ?? $"session_{i}",
                    DisplayName = name,
                    Volume = session.SimpleAudioVolume.Volume,
                    IsMuted = session.SimpleAudioVolume.Mute,
                    ProcessId = session.GetProcessID
                });
            }
        }
        catch
        {
            // Audio subsystem can throw during device changes
        }

        lock (_lock)
        {
            _sessions.Clear();
            _sessions.AddRange(newSessions);
        }

        SessionsChanged?.Invoke();
    }

    private static string GetSessionName(AudioSessionControl session)
    {
        // Try display name first
        var display = session.DisplayName;
        if (!string.IsNullOrWhiteSpace(display) && display != "@%SystemRoot%")
            return display;

        // Fall back to process name
        try
        {
            var pid = session.GetProcessID;
            if (pid == 0) return "";
            var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = proc.MainModule?.FileVersionInfo?.FileDescription;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return proc.ProcessName;
        }
        catch
        {
            return "";
        }
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
            if (sessionId == "master")
            {
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
                return;
            }

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
            if (sessionId == "master")
            {
                _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
                return;
            }

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
        _disposed = true;
        _device = null;
        _enumerator = null;
    }
}
