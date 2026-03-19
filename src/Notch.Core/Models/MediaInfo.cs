namespace Notch.Core.Models;

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
}
