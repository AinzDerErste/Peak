namespace Peak.Core.Models;

public class CalendarEvent
{
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Location { get; set; }
}
