namespace Peak.Core.Models;

public class SystemStats
{
    public float CpuUsage { get; set; }
    public float GpuUsage { get; set; }
    public float MemoryUsagePercent { get; set; }
    public ulong MemoryUsedMB { get; set; }
    public ulong MemoryTotalMB { get; set; }
    public float? BatteryPercent { get; set; }
    public bool? IsCharging { get; set; }
}
