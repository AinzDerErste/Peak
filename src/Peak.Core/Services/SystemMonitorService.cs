using System.Diagnostics;
using System.Runtime.InteropServices;
using Peak.Core.Models;
using Windows.Devices.Power;

namespace Peak.Core.Services;

public class SystemMonitorService : IDisposable
{
    private PerformanceCounter? _cpuCounter;

    // GPU Engine has one instance per (process × engine), so the list churns as
    // apps start/stop. Cache the 3D-engine counters and re-enumerate periodically.
    private readonly List<PerformanceCounter> _gpuCounters = new();
    private DateTime _gpuCountersRefreshedAt = DateTime.MinValue;
    private static readonly TimeSpan GpuRefreshInterval = TimeSpan.FromSeconds(30);

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
                    CpuUsage = _cpuCounter?.NextValue() ?? 0,
                    GpuUsage = ReadGpuUsage()
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

    /// <summary>
    /// Reads total GPU 3D-engine utilisation by summing the "Utilization Percentage"
    /// counter across every <c>*_engtype_3D</c> instance. Mirrors what Task Manager
    /// shows as "GPU 3D %". Cached counter list is refreshed every 30s to track
    /// processes that come and go.
    /// </summary>
    private float ReadGpuUsage()
    {
        try
        {
            // Periodically rebuild the counter list — process IDs in the instance
            // name are baked in at counter-creation time, so dead processes leave
            // dead counters behind otherwise.
            if (DateTime.UtcNow - _gpuCountersRefreshedAt > GpuRefreshInterval)
            {
                foreach (var c in _gpuCounters) { try { c.Dispose(); } catch { } }
                _gpuCounters.Clear();

                var category = new PerformanceCounterCategory("GPU Engine");
                foreach (var instance in category.GetInstanceNames())
                {
                    if (!instance.EndsWith("engtype_3D")) continue;
                    try
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                        counter.NextValue(); // First read primes the counter; always returns 0
                        _gpuCounters.Add(counter);
                    }
                    catch { /* Instance vanished between enumeration and creation */ }
                }
                _gpuCountersRefreshedAt = DateTime.UtcNow;
            }

            float total = 0;
            foreach (var counter in _gpuCounters)
            {
                try { total += counter.NextValue(); }
                catch { /* Process exited — counter throws InvalidOperationException */ }
            }

            // Multiple engines can sum to >100% in edge cases; clamp for sane UI display.
            return Math.Min(total, 100f);
        }
        catch
        {
            // No GPU performance counters available (very old Windows / no driver)
            return 0;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
        _cpuCounter?.Dispose();
        foreach (var c in _gpuCounters) { try { c.Dispose(); } catch { } }
        _gpuCounters.Clear();
    }
}
