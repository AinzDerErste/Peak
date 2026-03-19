using Notch.Core.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Notch.Core.Services;

public class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
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
        if (session == null)
        {
            CurrentMedia = null;
            SessionClosed?.Invoke();
            return;
        }
        UpdateCurrentSession(session);
    }

    private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _positionCts?.Cancel();
        _currentSession = session;
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _ = RefreshAsync();
            StartPositionPolling();
        }
    }

    private void StartPositionPolling()
    {
        _positionCts?.Cancel();
        _positionCts = new CancellationTokenSource();
        var token = _positionCts.Token;
        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    if (_currentSession == null) break;
                    try
                    {
                        var tl = _currentSession.GetTimelineProperties();
                        var playback = _currentSession.GetPlaybackInfo();
                        var pos = tl.Position;

                        // SMTC reports position at LastUpdatedTime — calculate real position
                        if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                            && tl.LastUpdatedTime != default)
                        {
                            var elapsed = DateTimeOffset.Now - tl.LastUpdatedTime;
                            pos += elapsed;
                            if (tl.EndTime > TimeSpan.Zero && pos > tl.EndTime)
                                pos = tl.EndTime;
                        }

                        PositionChanged?.Invoke(pos, tl.EndTime);
                    }
                    catch { /* session disposed */ }
                }
            }
            catch (OperationCanceledException) { }
            finally { timer.Dispose(); }
        }, token);
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshAsync();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_currentSession == null) return;

        try
        {
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            var playback = _currentSession.GetPlaybackInfo();
            var timeline = _currentSession.GetTimelineProperties();

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

            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var memStream = new MemoryStream();
                    var reader = new DataReader(stream);
                    await reader.LoadAsync((uint)stream.Size);
                    var bytes = new byte[stream.Size];
                    reader.ReadBytes(bytes);
                    info.Thumbnail = bytes;
                }
                catch { /* Thumbnail not available */ }
            }

            CurrentMedia = info;
            MediaChanged?.Invoke(info);
        }
        catch { /* Session may have been disposed */ }
    }

    public async Task PlayPauseAsync()
    {
        if (_currentSession != null)
            await _currentSession.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_currentSession != null)
            await _currentSession.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_currentSession != null)
            await _currentSession.TrySkipPreviousAsync();
    }

    public async Task<MediaRepeatMode> ToggleRepeatAsync()
    {
        if (_currentSession == null) return MediaRepeatMode.Off;

        var playback = _currentSession.GetPlaybackInfo();
        var current = playback.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;

        // Cycle: Off → List → Track → Off
        var next = current switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None
        };

        await _currentSession.TryChangeAutoRepeatModeAsync(next);

        return next switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.Track => MediaRepeatMode.Track,
            Windows.Media.MediaPlaybackAutoRepeatMode.List => MediaRepeatMode.List,
            _ => MediaRepeatMode.Off
        };
    }

    public void Dispose()
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        _positionCts?.Cancel();
        if (_manager != null)
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
    }
}
