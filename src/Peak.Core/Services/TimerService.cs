namespace Peak.Core.Services;

public class TimerService : IDisposable
{
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private DateTimeOffset _endTime;

    public event Action<TimeSpan>? Tick;
    public event Action? Finished;

    public TimeSpan Remaining { get; private set; }
    public bool IsRunning { get; private set; }

    public void Start(TimeSpan duration)
    {
        Stop();
        _endTime = DateTimeOffset.Now + duration;
        Remaining = duration;
        IsRunning = true;

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (await _timer!.WaitForNextTickAsync(ct))
        {
            Remaining = _endTime - DateTimeOffset.Now;
            if (Remaining <= TimeSpan.Zero)
            {
                Remaining = TimeSpan.Zero;
                IsRunning = false;
                Finished?.Invoke();
                return;
            }
            Tick?.Invoke(Remaining);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _timer?.Dispose();
        _timer = null;
        IsRunning = false;
        Remaining = TimeSpan.Zero;
    }

    public void Dispose() => Stop();
}
