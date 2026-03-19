namespace Notch.Core.Models;

public class QuickAccessItem
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
}
