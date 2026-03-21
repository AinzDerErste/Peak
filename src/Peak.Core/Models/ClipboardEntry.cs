namespace Peak.Core.Models;

public class ClipboardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ContentType { get; set; } = "text"; // "text", "image", "file"
    public string? TextContent { get; set; }
    public string? FilePath { get; set; } // saved image path or file path
    public string DisplayText { get; set; } = "";
    public DateTime CopiedAt { get; set; } = DateTime.Now;
}
