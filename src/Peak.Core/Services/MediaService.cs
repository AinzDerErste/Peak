using Peak.Core.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Peak.Core.Services;

/// <summary>
/// Wraps Windows' GlobalSystemMediaTransportControlsSessionManager (SMTC) and
/// surfaces a stable, race-free stream of <see cref="MediaInfo"/> + position
/// updates regardless of how chaotically the underlying API events fire.
///
/// <para><b>Race conditions this class hardens against:</b></para>
/// <list type="bullet">
///   <item><b>Overlapping refreshes</b> — SMTC fires multiple events per
///         track change (PropertiesChanged + PlaybackInfoChanged + the
///         async thumbnail load). Naive code awaiting them in parallel
///         lets a slow refresh from track N overwrite a fast refresh from
///         track N+1. Every refresh now captures a generation token at
///         start and discards its result if the session/generation moved
///         on while we were awaiting.</item>
///   <item><b>Stale event handlers</b> — when the user switches from
///         Spotify to YouTube, the old session can still fire one final
///         event before SMTC emits CurrentSessionChanged. We filter
///         every handler by <c>sender == _currentSession</c>.</item>
///   <item><b>Position-poller leak</b> — the previous PeriodicTimer task
///         read <c>_currentSession</c> directly each iteration, so it
///         could emit <see cref="PositionChanged"/> from a session that
///         had just been replaced. Each polling task now captures the
///         (session, generation) pair at start and exits when it differs
///         from the live values.</item>
/// </list>
/// </summary>
public class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    /// <summary>
    /// All access to <see cref="_currentSession"/> and <see cref="_generation"/>
    /// goes through this lock. Both fields are read together by every async
    /// handler so a torn read could mismatch them.
    /// </summary>
    private readonly object _sessionLock = new();
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    /// <summary>
    /// Increments every time <see cref="UpdateCurrentSession"/> swaps the
    /// active session (including swap-to-null on session-closed). Async
    /// operations capture the value at start and bail if it changed by
    /// the time their await resumes.
    /// </summary>
    private int _generation;

    private CancellationTokenSource? _positionCts;

    public event Action<MediaInfo>? MediaChanged;
    public event Action<TimeSpan, TimeSpan>? PositionChanged;
    public event Action? SessionClosed;

    public MediaInfo? CurrentMedia { get; private set; }

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        UpdateCurrentSession(_manager.GetCurrentSession());
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        var session = sender.GetCurrentSession();
        UpdateCurrentSession(session);

        if (session == null)
        {
            // Bug fix vs previous version: also tear down listeners + clear
            // CurrentMedia. The old code returned early without unsubscribing
            // from the (now-gone) session, leaving zombie handlers that
            // could still fire on disposal.
            CurrentMedia = null;
            SessionClosed?.Invoke();
        }
    }

    /// <summary>
    /// Swaps the tracked session under <see cref="_sessionLock"/>, bumping
    /// <see cref="_generation"/> so any in-flight refresh becomes stale.
    /// Subscribes the new session's events and kicks off a fresh refresh
    /// + position-poll pair on the new generation.
    /// </summary>
    private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
    {
        int gen;
        GlobalSystemMediaTransportControlsSession? newSession;

        lock (_sessionLock)
        {
            if (ReferenceEquals(_currentSession, session)) return;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }

            _positionCts?.Cancel();
            _currentSession = session;
            newSession = session;
            _generation++;
            gen = _generation;

            if (session != null)
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
        }

        if (newSession != null)
        {
            _ = RefreshAsync(newSession, gen);
            StartPositionPolling(newSession, gen);
        }
    }

    /// <summary>True if the (session, generation) pair still matches the live
    /// state. Used by every async path to bail before touching shared state.</summary>
    private bool IsCurrent(GlobalSystemMediaTransportControlsSession session, int gen)
    {
        lock (_sessionLock)
            return gen == _generation && ReferenceEquals(session, _currentSession);
    }

    private void StartPositionPolling(GlobalSystemMediaTransportControlsSession session, int gen)
    {
        var cts = new CancellationTokenSource();
        lock (_sessionLock) _positionCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    // Generation gate — drop out as soon as the session is
                    // swapped, even if our token cancellation hasn't been
                    // observed yet. Without this we could emit a Position-
                    // Changed for a session that was just replaced, freezing
                    // the UI on the OLD track's position.
                    if (!IsCurrent(session, gen)) break;

                    try
                    {
                        var tl = session.GetTimelineProperties();
                        var playback = session.GetPlaybackInfo();
                        var pos = tl.Position;

                        // SMTC reports position at LastUpdatedTime — extrapolate
                        // forward to "now" so the progress bar advances second-
                        // by-second even when the source app only updates SMTC
                        // on seek (Spotify, some Electron apps).
                        if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                            && tl.LastUpdatedTime != default)
                        {
                            var elapsed = DateTimeOffset.Now - tl.LastUpdatedTime;
                            pos += elapsed;
                            if (tl.EndTime > TimeSpan.Zero && pos > tl.EndTime)
                                pos = tl.EndTime;
                        }

                        // Re-check after the SMTC calls — they're synchronous
                        // but a session swap could land on another thread
                        // between GetTimelineProperties and Invoke.
                        if (IsCurrent(session, gen))
                            PositionChanged?.Invoke(pos, tl.EndTime);
                    }
                    catch { /* session disposed mid-tick — loop will exit on next IsCurrent check */ }
                }
            }
            catch (OperationCanceledException) { }
            finally { timer.Dispose(); }
        }, token);
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        // Filter by sender — when sessions swap, the OLD session can still
        // briefly fire one last event. Refreshing from a stale sender would
        // produce MediaInfo with the previous track's metadata that
        // overwrites the new one.
        int gen;
        lock (_sessionLock)
        {
            if (!ReferenceEquals(sender, _currentSession)) return;
            gen = _generation;
        }
        _ = RefreshAsync(sender, gen);
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        int gen;
        lock (_sessionLock)
        {
            if (!ReferenceEquals(sender, _currentSession)) return;
            gen = _generation;
        }
        _ = RefreshAsync(sender, gen);
    }

    /// <summary>
    /// Snapshot the session's metadata + thumbnail, but only commit the
    /// result if no newer refresh / session swap landed during the awaits.
    /// Multiple concurrent calls are safe; the loser silently drops its
    /// result rather than racing to write <see cref="CurrentMedia"/>.
    /// </summary>
    private async Task RefreshAsync(GlobalSystemMediaTransportControlsSession session, int gen)
    {
        try
        {
            // First await — getting media properties is genuinely async on
            // SMTC (it RPCs to the source app). A track change during this
            // wait is the most common race.
            var props = await session.TryGetMediaPropertiesAsync();
            if (!IsCurrent(session, gen)) return;

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var repeatMode = MediaRepeatMode.Off;
            if (playback.AutoRepeatMode.HasValue)
            {
                repeatMode = playback.AutoRepeatMode.Value switch
                {
                    Windows.Media.MediaPlaybackAutoRepeatMode.Track => MediaRepeatMode.Track,
                    Windows.Media.MediaPlaybackAutoRepeatMode.List => MediaRepeatMode.List,
                    _ => MediaRepeatMode.Off
                };
            }

            var info = new MediaInfo
            {
                Title = props.Title,
                Artist = props.Artist,
                AlbumTitle = props.AlbumTitle,
                IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Position = timeline.Position,
                Duration = timeline.EndTime,
                RepeatMode = repeatMode
            };

            // Thumbnail load is a separate async hop. Capture the bytes
            // before re-checking the gate so we don't burn time on
            // something we'll discard.
            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    if (!IsCurrent(session, gen)) return;

                    using var reader = new DataReader(stream);
                    await reader.LoadAsync((uint)stream.Size);
                    if (!IsCurrent(session, gen)) return;

                    var bytes = new byte[stream.Size];
                    reader.ReadBytes(bytes);
                    info.Thumbnail = bytes;
                }
                catch { /* Thumbnail not available — leave null, VM will clear stale art */ }
            }

            // Final gate — last chance to drop a stale snapshot.
            if (!IsCurrent(session, gen)) return;

            CurrentMedia = info;
            MediaChanged?.Invoke(info);
        }
        catch { /* Session may have been disposed mid-call */ }
    }

    public async Task PlayPauseAsync()
    {
        var s = SnapshotSession();
        if (s != null) await s.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        var s = SnapshotSession();
        if (s != null) await s.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        var s = SnapshotSession();
        if (s != null) await s.TrySkipPreviousAsync();
    }

    public async Task<MediaRepeatMode> ToggleRepeatAsync()
    {
        var session = SnapshotSession();
        if (session == null) return MediaRepeatMode.Off;

        var playback = session.GetPlaybackInfo();
        var current = playback.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;

        // Cycle: Off → List → Track → Off
        var next = current switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None
        };

        await session.TryChangeAutoRepeatModeAsync(next);

        return next switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.Track => MediaRepeatMode.Track,
            Windows.Media.MediaPlaybackAutoRepeatMode.List => MediaRepeatMode.List,
            _ => MediaRepeatMode.Off
        };
    }

    /// <summary>
    /// Returns the current session under the lock — used by user-initiated
    /// commands that don't need the full generation/race guard but still
    /// need a torn-read-safe reference.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? SnapshotSession()
    {
        lock (_sessionLock) return _currentSession;
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            _positionCts?.Cancel();
            _currentSession = null;
            _generation++;
        }
        if (_manager != null)
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
    }
}
