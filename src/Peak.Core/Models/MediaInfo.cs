namespace Peak.Core.Models;

public enum MediaRepeatMode
{
    Off,
    Track,
    List
}

public class MediaInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumTitle { get; set; } = string.Empty;
    public byte[]? Thumbnail { get; set; }
    public bool IsPlaying { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public MediaRepeatMode RepeatMode { get; set; } = MediaRepeatMode.Off;

    /// <summary>
    /// Stable identity for the playing track — used by the UI to detect
    /// "same track, just metadata refresh" vs "actually a different track".
    /// SMTC fires multiple events per track change (title/artist first, then
    /// thumbnail when ready); without a stable key the album-art widget
    /// would either flicker on every event or keep stale art when the new
    /// metadata arrived without a thumbnail.
    /// </summary>
    public string TrackKey => $"{Title}|{Artist}|{AlbumTitle}";
}
