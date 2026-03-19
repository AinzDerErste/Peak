namespace Notch.Core.Models;

public class NotificationData
{
    public string AppName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public uint Id { get; set; }
    public byte[]? IconBytes { get; set; }
}
