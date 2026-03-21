namespace Peak.Core.Models;

public class AudioSession
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public float Volume { get; set; }
    public bool IsMuted { get; set; }
    public uint ProcessId { get; set; }
    public bool IsMaster { get; set; }
}
