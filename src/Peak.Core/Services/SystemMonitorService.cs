using System.Diagnostics;
using System.Runtime.InteropServices;
using Peak.Core.Models;
using Windows.Devices.Power;

namespace Peak.Core.Services;

public class SystemMonitorService : IDisposable
{
    private PerformanceCounter? _cpuCounter;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public event Action<SystemStats>? StatsUpdated;
    public SystemStats? CurrentStats { get; private set; }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    public void Start()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _cpuCounter.NextValue(); // First call always returns 0

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        _ = PollAsync(_cts.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (await _timer!.WaitForNextTickAsync(ct))
        {
            try
            {
                var stats = new SystemStats
                {
                    CpuUsage = _cpuCounter?.NextValue() ?? 0
                };

                // Memory
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref mem))
                {
                    stats.MemoryUsagePercent = mem.dwMemoryLoad;
                    stats.MemoryTotalMB = mem.ullTotalPhys / (1024 * 1024);
                    stats.MemoryUsedMB = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024 * 1024);
                }

                // Battery
                try
                {
                    var battery = Battery.AggregateBattery;
                    var report = battery.GetReport();
                    if (report.RemainingCapacityInMilliwattHours.HasValue &&
                        report.FullChargeCapacityInMilliwattHours.HasValue &&
                        report.FullChargeCapacityInMilliwattHours.Value > 0)
                    {
                        stats.BatteryPercent = (float)report.RemainingCapacityInMilliwattHours.Value
                            / report.FullChargeCapacityInMilliwattHours.Value * 100;
                        stats.IsCharging = report.Status == Windows.System.Power.BatteryStatus.Charging;
                    }
                }
                catch { /* No battery (desktop PC) */ }

                CurrentStats = stats;
                StatsUpdated?.Invoke(stats);
            }
            catch { /* Ignore transient errors */ }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
        _cpuCounter?.Dispose();
    }
}
