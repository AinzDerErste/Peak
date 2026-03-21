using System.Net.NetworkInformation;
using Peak.Core.Models;

namespace Peak.Core.Services;

public class NetworkMonitorService : IDisposable
{
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSample;

    // 5 minutes of history at 1s intervals = 300 samples
    public const int HistorySize = 300;
    private readonly Queue<double> _downloadHistory = new(HistorySize + 1);
    private readonly Queue<double> _uploadHistory = new(HistorySize + 1);

    public event Action<NetworkStats>? StatsUpdated;

    /// <summary>Current download history (oldest first). Thread-safe snapshot.</summary>
    public double[] GetDownloadHistory()
    {
        lock (_downloadHistory) return _downloadHistory.ToArray();
    }

    /// <summary>Current upload history (oldest first). Thread-safe snapshot.</summary>
    public double[] GetUploadHistory()
    {
        lock (_uploadHistory) return _uploadHistory.ToArray();
    }

    public void Start()
    {
        // Take initial sample
        (_lastBytesReceived, _lastBytesSent) = GetTotalBytes();
        _lastSample = DateTime.UtcNow;

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _ = PollAsync(_cts.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (await _timer!.WaitForNextTickAsync(ct))
        {
            try
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastSample).TotalSeconds;
                if (elapsed <= 0) continue;

                var (bytesReceived, bytesSent) = GetTotalBytes();

                var dlDelta = bytesReceived - _lastBytesReceived;
                var ulDelta = bytesSent - _lastBytesSent;

                // Guard against counter resets (e.g. adapter reconnect)
                if (dlDelta < 0) dlDelta = 0;
                if (ulDelta < 0) ulDelta = 0;

                var dlSpeed = dlDelta / elapsed;
                var ulSpeed = ulDelta / elapsed;

                // Push to history
                lock (_downloadHistory)
                {
                    _downloadHistory.Enqueue(dlSpeed);
                    if (_downloadHistory.Count > HistorySize) _downloadHistory.Dequeue();
                }
                lock (_uploadHistory)
                {
                    _uploadHistory.Enqueue(ulSpeed);
                    if (_uploadHistory.Count > HistorySize) _uploadHistory.Dequeue();
                }

                var stats = new NetworkStats
                {
                    DownloadBytesPerSec = dlSpeed,
                    UploadBytesPerSec = ulSpeed
                };

                _lastBytesReceived = bytesReceived;
                _lastBytesSent = bytesSent;
                _lastSample = now;

                StatsUpdated?.Invoke(stats);
            }
            catch { /* Ignore transient errors */ }
        }
    }

    private static (long received, long sent) GetTotalBytes()
    {
        long totalReceived = 0;
        long totalSent = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            // Skip loopback and tunnel interfaces
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel)
                continue;

            var stats = nic.GetIPv4Statistics();
            totalReceived += stats.BytesReceived;
            totalSent += stats.BytesSent;
        }

        return (totalReceived, totalSent);
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        return bytesPerSec switch
        {
            >= 1_048_576 => $"{bytesPerSec / 1_048_576:F1} MB/s",
            >= 1_024 => $"{bytesPerSec / 1_024:F0} KB/s",
            _ => $"{bytesPerSec:F0} B/s"
        };
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
    }
}
